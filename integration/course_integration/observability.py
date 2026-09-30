"""Health and OpenMetrics endpoints of one integration process.

The three answers are fixed in shape by the week-4 observability contract:

    GET /health/live   -> 200 {"status":"live"}
    GET /health/ready  -> 200 {"status":"ready"} | 503 {"status":"not_ready","code":"..."}
    GET /metrics       -> 200 application/openmetrics-text; version=1.0.0; charset=utf-8

Liveness never touches a dependency: a process able to answer must not be restarted because
PostgreSQL or the provider is away. Readiness checks exactly the dependency the process cannot
work without - PostgreSQL for the dispatcher and the reconciler, the gateway for the adapter.

Metric names carry no identifiers and the series published here have no labels at all, so a UUID
cannot reach a label name or value even by accident.
"""

from __future__ import annotations

import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable

from .logs import Logger, describe_failure

OPENMETRICS_MEDIA_TYPE = "application/openmetrics-text; version=1.0.0; charset=utf-8"
JSON_MEDIA_TYPE = "application/json; charset=utf-8"

LIVE_BODY = json.dumps({"status": "live"}, separators=(",", ":")).encode("utf-8")
READY_BODY = json.dumps({"status": "ready"}, separators=(",", ":")).encode("utf-8")
NOT_READY_BODY = json.dumps(
    {"status": "not_ready", "code": "dependency.unavailable"}, separators=(",", ":")
).encode("utf-8")

# Readiness is cached for a short window: a probe loop must not turn into a connection storm,
# and the window expires on its own so recovery needs no external signal.
READINESS_CACHE_S = 0.25


def _number(value: float) -> str:
    """OpenMetrics number without a locale decimal separator or an exponent."""
    rounded = round(float(value), 3)
    if rounded == int(rounded):
        return str(int(rounded))
    return f"{rounded:.3f}".rstrip("0")


class Metrics:
    """Counters and gauges of one process, rendered as an OpenMetrics document."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._order: list[tuple[str, str, str]] = []
        self._values: dict[str, float] = {}

    def register(self, family: str, kind: str, help_text: str) -> None:
        if kind not in ("counter", "gauge"):
            raise ValueError("only counters and gauges are published")
        with self._lock:
            if family not in self._values:
                self._order.append((family, kind, help_text))
                self._values[family] = 0.0

    def increment(self, family: str, delta: float = 1.0) -> None:
        with self._lock:
            self._values[family] = self._values.get(family, 0.0) + delta

    def set(self, family: str, value: float) -> None:
        with self._lock:
            self._values[family] = float(value)

    def render(self) -> bytes:
        lines: list[str] = []
        with self._lock:
            for family, kind, help_text in self._order:
                value = self._values.get(family, 0.0)
                # In OpenMetrics a counter family is declared without the "_total" suffix; the
                # suffix belongs to the sample. Declaring "name_total" as the family is the most
                # common way to produce a document that parses everywhere except in a validator.
                sample = f"{family}_total" if kind == "counter" else family
                lines.append(f"# HELP {family} {help_text}")
                lines.append(f"# TYPE {family} {kind}")
                lines.append(f"{sample} {_number(value)}")
        lines.append("# EOF")
        return ("\n".join(lines) + "\n").encode("utf-8")


class Readiness:
    """A cached dependency probe: ``probe()`` returns True when the dependency answered."""

    def __init__(self, probe: Callable[[], bool], logger: Logger) -> None:
        self._probe = probe
        self._logger = logger
        self._lock = threading.Lock()
        self._checked_at = 0.0
        self._ready = False

    def __call__(self) -> bool:
        with self._lock:
            if time.monotonic() - self._checked_at < READINESS_CACHE_S:
                return self._ready
            try:
                self._ready = bool(self._probe())
            except Exception as error:  # noqa: BLE001 - an unavailable dependency is not a crash
                self._logger.warning("readiness.failed", **describe_failure(error))
                self._ready = False
            finally:
                self._checked_at = time.monotonic()
            return self._ready


def handler_class(
    readiness: Callable[[], bool], metrics: Metrics
) -> type[BaseHTTPRequestHandler]:
    class ObservabilityHandler(BaseHTTPRequestHandler):
        server_version = "course-observability"
        sys_version = ""
        protocol_version = "HTTP/1.1"
        timeout = 15

        def log_message(self, format: str, *args: object) -> None:  # noqa: A002
            # A probe every 100 ms would otherwise bury every meaningful record in access logs.
            return

        def _send(self, status: int, body: bytes, content_type: str) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            route = self.path.split("?", 1)[0]
            if route == "/health/live":
                self._send(200, LIVE_BODY, JSON_MEDIA_TYPE)
            elif route == "/health/ready":
                ready = readiness()
                self._send(
                    200 if ready else 503,
                    READY_BODY if ready else NOT_READY_BODY,
                    JSON_MEDIA_TYPE,
                )
            elif route == "/metrics":
                self._send(200, metrics.render(), OPENMETRICS_MEDIA_TYPE)
            else:
                self._send(404, b"{}", JSON_MEDIA_TYPE)

        do_HEAD = do_GET

        def do_POST(self) -> None:  # noqa: N802
            # No failpoint and no state change is reachable from here: the observability port
            # answers GET only, so the closed test profile stays closed.
            self._send(404, b"{}", JSON_MEDIA_TYPE)

        do_PUT = do_POST
        do_PATCH = do_POST
        do_DELETE = do_POST
        do_OPTIONS = do_POST

    return ObservabilityHandler


class ObservabilityServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    logger: Logger | None = None

    def handle_error(self, request: object, client_address: object) -> None:
        import sys

        error = sys.exception()
        if self.logger is not None and error is not None:
            self.logger.warning("observability.connection_failed", **describe_failure(error))


class Observability:
    """Owns the background HTTP thread; a failed bind never stops the polling loop."""

    def __init__(
        self,
        port: int,
        readiness: Callable[[], bool],
        metrics: Metrics,
        logger: Logger,
        host: str = "0.0.0.0",
    ) -> None:
        self.port = port
        self.host = host
        self.metrics = metrics
        self._readiness = readiness
        self._logger = logger
        self._server: ObservabilityServer | None = None

    def __enter__(self) -> "Observability":
        try:
            server = ObservabilityServer(
                (self.host, self.port), handler_class(self._readiness, self.metrics)
            )
        except OSError as error:
            # A busy service port is a contour configuration fault, not a reason to stop
            # delivering messages that are already durable in PostgreSQL.
            self._logger.error("observability.bind_failed", port=self.port, **describe_failure(error))
            return self
        server.logger = self._logger
        threading.Thread(
            target=server.serve_forever, kwargs={"poll_interval": 0.2}, daemon=True
        ).start()
        self._server = server
        self._logger.info("observability.started", port=self.port)
        return self

    def __exit__(self, *_: object) -> None:
        server, self._server = self._server, None
        if server is not None:
            server.shutdown()
            server.server_close()
            self._logger.info("observability.stopped")
