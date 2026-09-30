"""``receipt-adapter``: provider v0.2.0 legacy callback -> signed receipt v1 -> generic C# API.

The adapter has no database credentials and keeps no authoritative state. It answers the
provider only after the platform answered: the provider's own retry policy covers outages.
"""

from __future__ import annotations

import hmac
import http.client
import json
import os
import re
import threading
import time
import urllib.parse
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable, Mapping

from .config import AdapterSettings, adapter_settings
from .logs import Logger, describe_failure
from .observability import (
    JSON_MEDIA_TYPE,
    LIVE_BODY,
    NOT_READY_BODY,
    OPENMETRICS_MEDIA_TYPE,
    READY_BODY,
    Metrics,
    Readiness,
)
from .receipt import (
    SIGNATURE_HEADER,
    CallbackRejected,
    canonical_bytes,
    parse_legacy_callback,
    signature,
    to_receipt,
)
from .runtime import StopSignal

CALLBACK_PREFIX = "/callbacks/provider-v02/"
MAX_CALLBACK_BYTES = 64 * 1024
MAX_API_RESPONSE_BYTES = 1024 * 1024
# Upper bound of a rejected request body that is still read and discarded before answering.
MAX_DISCARDED_BYTES = 1024 * 1024
# Budget of the readiness probe of the gateway dependency.
DEPENDENCY_PROBE_TIMEOUT_S = 2.0
DEPENDENCY_UNAVAILABLE = json.dumps(
    {"status": "error", "code": "dependency.unavailable"}, separators=(",", ":")
).encode("utf-8")

# Idempotency-Key travels in an HTTP header, so the identifier must be header-safe.
_HEADER_SAFE_IDENTIFIER = re.compile(r"^[\x21-\x7e](?:[\x20-\x7e]*[\x21-\x7e])?$")


@dataclass(frozen=True)
class Reply:
    status: int
    body: bytes = b""
    content_type: str | None = None


@dataclass(frozen=True)
class SignedReceipt:
    message_id: str
    external_request_id: str
    body: bytes
    headers: dict[str, str]


def sign_callback(raw: bytes, settings: AdapterSettings) -> SignedReceipt:
    """Validate, map, serialize and sign. Raises :class:`CallbackRejected` for a bad callback."""
    callback = parse_legacy_callback(raw)
    receipt = to_receipt(callback)
    if not _HEADER_SAFE_IDENTIFIER.fullmatch(receipt["messageId"]):
        raise CallbackRejected("providerPaymentId cannot be carried as an idempotency key")
    body = canonical_bytes(receipt)
    return SignedReceipt(
        message_id=receipt["messageId"],
        external_request_id=receipt["externalRequestId"],
        body=body,
        headers={
            "Authorization": f"Bearer {settings.callback_token}",
            "Content-Type": "application/json",
            "Idempotency-Key": receipt["messageId"],
            "X-Action-Version": "1",
            SIGNATURE_HEADER: signature(settings.hmac_secret, body),
        },
    )


Transport = Callable[[str, bytes, dict[str, str], float], Reply]


class ResponseTooLarge(http.client.HTTPException):
    """The API answered with more than the adapter relays; treated as an unusable dependency."""


def build_metrics() -> Metrics:
    metrics = Metrics()
    metrics.register("provider_callbacks_received", "counter", "Legacy provider callbacks accepted on the route.")
    metrics.register("provider_callbacks_rejected", "counter", "Callbacks refused before reaching the platform.")
    metrics.register("provider_callbacks_forwarded", "counter", "Signed receipts relayed to the generic API.")
    metrics.register("provider_callbacks_dependency_unavailable", "counter", "Relays that found the platform away.")
    return metrics


def probe_dependency(url: str, timeout_s: float) -> bool:
    """Readiness of the adapter is readiness of the gateway it relays through.

    PostgreSQL is not probed here and cannot be: the adapter holds no database credentials.
    """
    parsed = urllib.parse.urlsplit(url)
    default_port = 443 if parsed.scheme == "https" else 80
    if parsed.scheme == "https":
        connection: http.client.HTTPConnection = http.client.HTTPSConnection(
            parsed.hostname or "", parsed.port or default_port, timeout=timeout_s
        )
    else:
        connection = http.client.HTTPConnection(
            parsed.hostname or "", parsed.port or default_port, timeout=timeout_s
        )
    try:
        connection.request("GET", parsed.path or "/")
        response = connection.getresponse()
        response.read(1024)
        return response.status == 200
    except (OSError, http.client.HTTPException):
        return False
    finally:
        connection.close()


def http_transport(url: str, body: bytes, headers: dict[str, str], timeout_s: float) -> Reply:
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme == "https":
        connection: http.client.HTTPConnection = http.client.HTTPSConnection(
            parsed.hostname or "", parsed.port or 443, timeout=timeout_s
        )
    else:
        connection = http.client.HTTPConnection(
            parsed.hostname or "", parsed.port or 80, timeout=timeout_s
        )
    try:
        connection.request("POST", parsed.path or "/", body=body, headers=headers)
        response = connection.getresponse()
        data = response.read(MAX_API_RESPONSE_BYTES + 1)
        if len(data) > MAX_API_RESPONSE_BYTES:
            # A truncated envelope must not reach the provider under the original status.
            raise ResponseTooLarge("generic API response exceeds the adapter limit")
        return Reply(response.status, data, response.getheader("Content-Type"))
    finally:
        connection.close()


class ReceiptAdapter:
    """Transport-independent request handling, unit-testable without sockets."""

    def __init__(
        self,
        settings: AdapterSettings,
        logger: Logger,
        transport: Transport = http_transport,
        metrics: Metrics | None = None,
    ) -> None:
        self.settings = settings
        self.logger = logger
        self.transport = transport
        self.metrics = metrics if metrics is not None else build_metrics()
        self._capability = settings.capability.encode("utf-8")

    def route_matches(self, method: str, path: str) -> bool:
        if method != "POST" or not path.startswith(CALLBACK_PREFIX):
            return False
        segment = path[len(CALLBACK_PREFIX):]
        # Constant-time comparison: response timing does not reveal the capability prefix.
        return hmac.compare_digest(segment.encode("utf-8", errors="surrogateescape"), self._capability)

    @staticmethod
    def content_length(values: list[str]) -> int | None:
        if len(values) != 1 or not re.fullmatch(r"[0-9]{1,6}", values[0].strip()):
            return None
        length = int(values[0].strip())
        if length < 1 or length > MAX_CALLBACK_BYTES:
            return None
        return length

    def handle(self, raw: bytes) -> Reply:
        started = time.monotonic()
        self.metrics.increment("provider_callbacks_received")
        try:
            signed = sign_callback(raw, self.settings)
        except CallbackRejected as error:
            # The reason names the rule, never the offending value.
            self.metrics.increment("provider_callbacks_rejected")
            self.logger.info("callback.rejected", status=400, reason=str(error))
            return Reply(400)

        try:
            reply = self.transport(
                self.settings.receipt_api_url, signed.body, signed.headers, self.settings.api_timeout_s
            )
        except (OSError, http.client.HTTPException) as error:
            self.metrics.increment("provider_callbacks_dependency_unavailable")
            self.logger.warning(
                "callback.dependency_unavailable",
                messageId=signed.message_id,
                externalRequestId=signed.external_request_id,
                **describe_failure(error),
            )
            return Reply(503, DEPENDENCY_UNAVAILABLE, "application/json")
        except Exception as error:  # noqa: BLE001 - the provider must get an answer, not a reset
            # Nothing was confirmed: an empty 500 lets the provider retry by its own policy.
            self.logger.error(
                "callback.failed",
                messageId=signed.message_id,
                externalRequestId=signed.external_request_id,
                **describe_failure(error),
            )
            return Reply(500)

        self.metrics.increment("provider_callbacks_forwarded")
        self.logger.info(
            "callback.forwarded",
            messageId=signed.message_id,
            externalRequestId=signed.external_request_id,
            apiStatus=reply.status,
            durationMs=int((time.monotonic() - started) * 1000),
        )
        return reply


def _handler_class(
    adapter: ReceiptAdapter, readiness: Callable[[], bool]
) -> type[BaseHTTPRequestHandler]:
    class CallbackHandler(BaseHTTPRequestHandler):
        server_version = "receipt-adapter"
        sys_version = ""
        protocol_version = "HTTP/1.1"
        timeout = 15

        def log_message(self, format: str, *args: object) -> None:  # noqa: A002
            # The default access log prints the request line, which contains the capability.
            return

        def _send(self, reply: Reply) -> None:
            self.send_response(reply.status)
            if reply.content_type:
                self.send_header("Content-Type", reply.content_type)
            self.send_header("Content-Length", str(len(reply.body)))
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            if reply.body and self.command != "HEAD":
                self.wfile.write(reply.body)

        def _reject(self, status: int) -> None:
            # Closing a socket with unread request bytes makes the TCP stack answer with RST, which
            # can destroy the response before the client reads it. A declared body up to a bounded
            # size is therefore read and discarded first; anything larger or unframed is not.
            values = self.headers.get_all("Content-Length") or []
            if not self.headers.get_all("Transfer-Encoding") and len(values) == 1:
                declared = values[0].strip()
                if declared.isdigit() and int(declared) <= MAX_DISCARDED_BYTES:
                    remaining = int(declared)
                    try:
                        while remaining > 0:
                            chunk = self.rfile.read(min(remaining, 65536))
                            if not chunk:
                                break
                            remaining -= len(chunk)
                    except OSError:
                        pass
            self.close_connection = True
            self._send(Reply(status))

        def _not_found(self) -> None:
            self._reject(404)

        def do_POST(self) -> None:  # noqa: N802
            if not adapter.route_matches("POST", self.path):
                self._not_found()
                return
            if self.headers.get_all("Transfer-Encoding"):
                self._reject(400)
                return
            length = adapter.content_length(self.headers.get_all("Content-Length") or [])
            if length is None:
                self._reject(400)
                return
            raw = self.rfile.read(length)
            if len(raw) != length:
                self.close_connection = True
                self._send(Reply(400))
                return
            self._send(adapter.handle(raw))

        # Наблюдаемость живёт на том же служебном порту, что и callback: контракт недели 4
        # закрепляет adapter на 8082, и отдельный сокет тут был бы вторым адресом одного и того
        # же процесса. Точку аварийной остановки этот порт НЕ включает: он отвечает только на
        # GET трёх известных путей, поэтому закрытый профиль остаётся закрытым.
        def do_GET(self) -> None:  # noqa: N802
            route = self.path.split("?", 1)[0]
            if route == "/health/live":
                self._send(Reply(200, LIVE_BODY, JSON_MEDIA_TYPE))
            elif route == "/health/ready":
                ready = readiness()
                self._send(Reply(
                    200 if ready else 503,
                    READY_BODY if ready else NOT_READY_BODY,
                    JSON_MEDIA_TYPE,
                ))
            elif route == "/metrics":
                self._send(Reply(200, adapter.metrics.render(), OPENMETRICS_MEDIA_TYPE))
            else:
                self._not_found()

        do_HEAD = do_GET

        def do_PUT(self) -> None:  # noqa: N802
            self._not_found()

        do_PATCH = do_PUT
        do_DELETE = do_PUT
        do_OPTIONS = do_PUT

    return CallbackHandler


class CallbackServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    logger: Logger | None = None

    def handle_error(self, request: object, client_address: object) -> None:
        # The default prints a traceback to stderr. A client that dropped the connection is not
        # a platform failure, and a traceback is not a structured, redaction-safe record.
        import sys

        error = sys.exception()
        if self.logger is not None and error is not None:
            self.logger.warning("callback.connection_failed", **describe_failure(error))


def serve(settings: AdapterSettings, logger: Logger, stop: StopSignal) -> int:
    adapter = ReceiptAdapter(settings, logger, metrics=build_metrics())
    # The probe budget is deliberately shorter than the relay budget: a health check that waits as
    # long as a real callback answers the question too late to be useful.
    readiness = Readiness(
        lambda: probe_dependency(settings.dependency_probe_url, DEPENDENCY_PROBE_TIMEOUT_S),
        logger,
    )
    server = CallbackServer(
        (settings.listen_host, settings.listen_port), _handler_class(adapter, readiness)
    )
    server.logger = logger
    thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.2}, daemon=True)
    thread.start()
    logger.info("adapter.started", port=settings.listen_port)
    try:
        while not stop.wait(3600):
            pass
    finally:
        server.shutdown()
        server.server_close()
        logger.info("adapter.stopped")
    return 0


def main(env: Mapping[str, str] | None = None) -> int:
    logger = Logger("receipt-adapter")
    settings = adapter_settings(os.environ if env is None else env)
    return serve(settings, logger, StopSignal().install())
