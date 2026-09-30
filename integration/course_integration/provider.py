"""Provider v0.2.0 client: exact request bytes and attempt classification.

One call of :func:`send_payment` is exactly one HTTP attempt. It never retries and never follows
redirects: retry policy and schedule belong to PostgreSQL (``delivery.fail_outbox``).
"""

from __future__ import annotations

import http.client
import json
import socket
import threading
import urllib.parse
from dataclasses import dataclass
from typing import Callable

MAX_RESPONSE_BYTES = 64 * 1024
TRANSPORT_RETRYABLE = "transport.error.retryable"
RESPONSE_INVALID = "response.invalid.terminal"


@dataclass(frozen=True)
class OutboxMessage:
    outbox_id: str
    lease_version: int
    external_request_id: str
    correlation_id: str
    amount: str
    currency: str


@dataclass(frozen=True)
class PaymentRequest:
    path: str
    headers: dict[str, str]
    body: bytes


@dataclass(frozen=True)
class AttemptResult:
    """Either ``provider_payment_id`` (accepted) or ``error_code`` (failed) is set."""

    provider_payment_id: str | None = None
    error_code: str | None = None
    http_status: int | None = None

    @property
    def accepted(self) -> bool:
        return self.provider_payment_id is not None


def build_payment_request(message: OutboxMessage) -> PaymentRequest:
    """Legacy body ``operationId`` intentionally carries ``externalRequestId``.

    Key, body and correlation id are functions of the stored Outbox row only, so every retry of
    one delivery sends identical bytes and headers.
    """
    body = json.dumps(
        {
            "operationId": message.external_request_id,
            "amount": message.amount,
            "currency": message.currency,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return PaymentRequest(
        path="/payments",
        headers={
            "Content-Type": "application/json",
            "Idempotency-Key": message.external_request_id,
            "X-Correlation-ID": message.correlation_id,
        },
        body=body,
    )


def classify_status(status: int) -> str:
    """Error code for a non-accepted HTTP status."""
    if status in (408, 429) or 500 <= status <= 599:
        return f"http.{status}.retryable"
    return f"http.{status}.terminal"


def _unique_members(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON member")
        result[key] = value
    return result


def parse_accepted(body: bytes) -> str | None:
    """Strict ``{"providerPaymentId":"...","status":"ACCEPTED"}``; ``None`` when invalid."""
    try:
        value = json.loads(body.decode("utf-8", errors="strict"), object_pairs_hook=_unique_members)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError):
        return None
    if not isinstance(value, dict) or set(value) != {"providerPaymentId", "status"}:
        return None
    payment_id = value["providerPaymentId"]
    if (
        value["status"] != "ACCEPTED"
        or not isinstance(payment_id, str)
        or not 1 <= len(payment_id) <= 128
        or "\r" in payment_id
        or "\n" in payment_id
    ):
        return None
    return payment_id


def classify_response(status: int, body: bytes | None) -> AttemptResult:
    if status != 202:
        return AttemptResult(error_code=classify_status(status), http_status=status)
    payment_id = parse_accepted(body or b"")
    if payment_id is None:
        return AttemptResult(error_code=RESPONSE_INVALID, http_status=status)
    return AttemptResult(provider_payment_id=payment_id, http_status=status)


ConnectionFactory = Callable[[str, int, float], http.client.HTTPConnection]


def _default_connection(scheme: str) -> ConnectionFactory:
    if scheme == "https":
        return lambda host, port, timeout: http.client.HTTPSConnection(host, port, timeout=timeout)
    return lambda host, port, timeout: http.client.HTTPConnection(host, port, timeout=timeout)


def _within_budget(attempt: Callable[[], AttemptResult], timeout_s: float) -> AttemptResult:
    """Bound one attempt by the configured budget, INCLUDING name resolution.

    A socket timeout covers connect and read, but never ``getaddrinfo``: an absent provider name
    costs whatever the resolver decides, and on a Docker host that is seconds while the declared
    budget is milliseconds. Such an attempt also outlives the delivery lease, so the report it
    eventually brings is refused by fencing - the attempt is then wasted twice, and the delivery
    looks stuck for reasons nothing in the stored state explains.

    The attempt therefore runs on a daemon thread and is abandoned when the budget expires. An
    abandoned attempt may still reach the provider: the stable ``externalRequestId`` keeps that to
    one payment, and its result is ignored anyway, because the report is conditional on owner and
    lease version.
    """
    outcome: list[AttemptResult | BaseException] = []
    finished = threading.Event()

    def run() -> None:
        try:
            outcome.append(attempt())
        except BaseException as error:  # noqa: BLE001 - re-raised on the calling thread
            outcome.append(error)
        finally:
            finished.set()

    threading.Thread(target=run, name="provider-attempt", daemon=True).start()
    if not finished.wait(timeout_s):
        return AttemptResult(error_code=TRANSPORT_RETRYABLE)

    result = outcome[0]
    if isinstance(result, BaseException):
        raise result
    return result


def send_payment(
    base_url: str,
    request: PaymentRequest,
    timeout_s: float,
    connection_factory: ConnectionFactory | None = None,
) -> AttemptResult:
    parsed = urllib.parse.urlsplit(base_url)
    default_port = 443 if parsed.scheme == "https" else 80
    factory = connection_factory or _default_connection(parsed.scheme)
    path = parsed.path.rstrip("/") + request.path
    return _within_budget(
        lambda: _exchange(factory, parsed, default_port, path, request, timeout_s), timeout_s
    )


def _exchange(
    factory: ConnectionFactory,
    parsed: urllib.parse.SplitResult,
    default_port: int,
    path: str,
    request: PaymentRequest,
    timeout_s: float,
) -> AttemptResult:
    connection = factory(parsed.hostname or "", parsed.port or default_port, timeout_s)
    try:
        connection.request("POST", path, body=request.body, headers=request.headers)
        response = connection.getresponse()
        status = response.status
        if status != 202:
            return classify_response(status, None)
        body = response.read(MAX_RESPONSE_BYTES + 1)
        if len(body) > MAX_RESPONSE_BYTES:
            return AttemptResult(error_code=RESPONSE_INVALID, http_status=status)
        return classify_response(status, body)
    except (OSError, socket.timeout, http.client.HTTPException):
        # Timeout, refused connection, reset or a broken HTTP exchange: the provider may or may
        # not have accepted the payment. The retry reuses the idempotency key, so a lost
        # response cannot create a second payment.
        return AttemptResult(error_code=TRANSPORT_RETRYABLE)
    finally:
        connection.close()
