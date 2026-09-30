"""Health and OpenMetrics contract of the integration processes."""

import http.client
import json
import re
import unittest

from course_integration.adapter import build_metrics as adapter_metrics
from course_integration.adapter import probe_dependency
from course_integration.config import dispatcher_settings, reconciler_settings
from course_integration.dispatcher import Dispatcher, build_metrics as dispatcher_metrics
from course_integration.logs import Logger
from course_integration.observability import (
    JSON_MEDIA_TYPE,
    OPENMETRICS_MEDIA_TYPE,
    Metrics,
    Observability,
    Readiness,
)
from course_integration.provider import AttemptResult, OutboxMessage
from course_integration.reconciler import Reconciler, build_metrics as reconciler_metrics
from course_integration.runtime import StopSignal

DATABASE = {
    "COURSE_POSTGRES_USER": "outbox_dispatcher",
    "COURSE_POSTGRES_PASSWORD": "db-password-value",
}
DISPATCHER = {
    "PROVIDER_URL": "http://provider-simulator:8081",
    "OUTBOX_OWNER": "outbox-dispatcher",
    **DATABASE,
}

SAMPLE = re.compile(r"^([A-Za-z_:][A-Za-z0-9_:]*)(?:\{([^}]*)\})?\s+(-?[0-9.]+)$")
MESSAGE = OutboxMessage(
    outbox_id="11111111-1111-4111-8111-111111111111",
    lease_version=3,
    external_request_id="external-1",
    correlation_id="22222222-2222-4222-8222-222222222222",
    amount="1000.00",
    currency="RUB",
)


def parse_document(document: bytes) -> tuple[dict[str, str], dict[str, float], set[str]]:
    """Minimal OpenMetrics reader: declared types, sample values and every label name seen."""
    text = document.decode("utf-8")
    if "\r" in text or not text.endswith("# EOF\n"):
        raise AssertionError("document must be LF-only and end with # EOF")
    types: dict[str, str] = {}
    samples: dict[str, float] = {}
    labels: set[str] = set()
    for line in text.splitlines():
        declared = re.fullmatch(r"# TYPE ([A-Za-z_:][A-Za-z0-9_:]*) (counter|gauge)", line)
        if declared:
            types[declared.group(1)] = declared.group(2)
            continue
        if line.startswith("#"):
            continue
        matched = SAMPLE.fullmatch(line)
        if matched is None:
            raise AssertionError(f"invalid OpenMetrics sample: {line!r}")
        samples[matched.group(1)] = float(matched.group(3))
        for label in re.finditer(r'([A-Za-z_][A-Za-z0-9_]*)\s*=', matched.group(2) or ""):
            labels.add(label.group(1))
    return types, samples, labels


class MetricsDocumentTests(unittest.TestCase):
    def test_counter_family_is_declared_without_the_total_suffix(self):
        metrics = Metrics()
        metrics.register("widgets_made", "counter", "Widgets made.")
        metrics.increment("widgets_made", 2)
        types, samples, _ = parse_document(metrics.render())
        # The contract is explicit: TYPE names the family, the "_total" suffix belongs to the
        # sample. Declaring "widgets_made_total" as a counter family is the usual way to produce
        # a document that parses everywhere except in a validator.
        self.assertEqual(types, {"widgets_made": "counter"})
        self.assertEqual(samples, {"widgets_made_total": 2.0})

    def test_gauge_sample_carries_the_family_name(self):
        metrics = Metrics()
        metrics.register("queue_depth", "gauge", "Queue depth.")
        metrics.set("queue_depth", 7)
        types, samples, _ = parse_document(metrics.render())
        self.assertEqual(types, {"queue_depth": "gauge"})
        self.assertEqual(samples, {"queue_depth": 7.0})

    def test_help_text_cannot_break_out_of_its_line(self):
        metrics = Metrics()
        metrics.register("odd_metric", "gauge", "Queue depth.")
        metrics.set("odd_metric", 1)
        self.assertNotIn(b"\r", metrics.render())

    def test_published_series_carry_no_labels_at_all(self):
        for metrics in (
            dispatcher_metrics(dispatcher_settings(DISPATCHER)),
            reconciler_metrics(),
            adapter_metrics(),
        ):
            with self.subTest(metrics=metrics):
                _, _, labels = parse_document(metrics.render())
                # No labels means no cardinality to bound and no place for an identifier.
                self.assertEqual(labels, set())

    def test_only_counters_and_gauges_are_publishable(self):
        with self.assertRaises(ValueError):
            Metrics().register("weird", "histogram", "Not published.")


class ReadinessTests(unittest.TestCase):
    def test_probe_failure_is_not_ready_and_is_not_raised(self):
        def broken() -> bool:
            raise OSError("connection refused to secret-host")

        log = []
        readiness = Readiness(broken, Logger("probe", _Sink(log)))
        self.assertFalse(readiness())
        self.assertIn("readiness.failed", "".join(log))
        self.assertNotIn("secret-host", "".join(log))

    def test_result_is_cached_for_a_short_window(self):
        calls = []

        def counting() -> bool:
            calls.append(1)
            return True

        readiness = Readiness(counting, Logger("probe"))
        self.assertTrue(readiness())
        self.assertTrue(readiness())
        self.assertEqual(len(calls), 1)


class _Sink:
    def __init__(self, target: list) -> None:
        self.target = target

    def write(self, value: str) -> None:
        self.target.append(value)

    def flush(self) -> None:
        return


class ObservabilityServerTests(unittest.TestCase):
    def setUp(self):
        self.ready = True
        self.metrics = Metrics()
        self.metrics.register("probe_total_calls", "counter", "Calls.")
        self.observability = Observability(
            0, lambda: self.ready, self.metrics, Logger("probe"), host="127.0.0.1"
        ).__enter__()
        self.port = self.observability._server.server_address[1]

    def tearDown(self):
        self.observability.__exit__()

    def get(self, path: str, method: str = "GET"):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            connection.request(method, path)
            response = connection.getresponse()
            return response.status, response.read(), response.getheader("Content-Type")
        finally:
            connection.close()

    def test_liveness_does_not_depend_on_the_dependency(self):
        self.ready = False
        status, body, content_type = self.get("/health/live")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body), {"status": "live"})
        self.assertEqual(content_type, JSON_MEDIA_TYPE)

    def test_readiness_reflects_the_dependency(self):
        status, body, content_type = self.get("/health/ready")
        self.assertEqual((status, json.loads(body)), (200, {"status": "ready"}))
        self.assertEqual(content_type, JSON_MEDIA_TYPE)

        self.ready = False
        status, body, _ = self.get("/health/ready")
        self.assertEqual(status, 503)
        self.assertEqual(json.loads(body), {"status": "not_ready", "code": "dependency.unavailable"})

    def test_metrics_media_type_and_document(self):
        status, body, content_type = self.get("/metrics")
        self.assertEqual(status, 200)
        self.assertEqual(content_type, OPENMETRICS_MEDIA_TYPE)
        types, samples, _ = parse_document(body)
        self.assertEqual(types, {"probe_total_calls": "counter"})
        self.assertEqual(samples, {"probe_total_calls_total": 0.0})

    def test_unknown_route_and_state_changing_methods_are_404(self):
        for method, path in (("GET", "/"), ("POST", "/metrics"), ("POST", "/failpoint")):
            with self.subTest(method=method, path=path):
                status, _, _ = self.get(path, method)
                self.assertEqual(status, 404)


class DependencyProbeTests(unittest.TestCase):
    def test_unreachable_dependency_is_not_ready(self):
        # Port 1 on loopback refuses immediately: the probe must answer False, not raise.
        self.assertFalse(probe_dependency("http://127.0.0.1:1/health/live", 0.5))

    def test_adapter_probes_the_gateway_origin_not_the_action_route(self):
        from course_integration.config import adapter_settings

        settings = adapter_settings({
            "PROVIDER_CALLBACK_CAPABILITY": "capability-value",
            "PROVIDER_CALLBACK_TOKEN": "token-value",
            "PROVIDER_HMAC_SECRET": "secret-value",
            "RECEIPT_API_URL": "http://gateway:8080/api/receipt/accept",
        })
        self.assertEqual(settings.dependency_probe_url, "http://gateway:8080/health/live")


class ProcessMetricsTests(unittest.TestCase):
    def test_dispatcher_publishes_the_configured_delivery_policy(self):
        settings = dispatcher_settings({
            **DISPATCHER,
            "COURSE_TEST_PROFILE": "1",
            "COURSE_OUTBOX_MAX_ATTEMPTS": "4",
            "COURSE_OUTBOX_LEASE_MS": "2000",
        })
        _, samples, _ = parse_document(dispatcher_metrics(settings).render())
        self.assertEqual(samples["outbox_delivery_policy_max_attempts"], 4.0)
        self.assertEqual(samples["outbox_delivery_policy_lease_ms"], 2000.0)

    def test_dispatcher_counts_attempts_accepted_and_refused_reports(self):
        settings = dispatcher_settings(DISPATCHER)
        metrics = dispatcher_metrics(settings)
        dispatcher = Dispatcher(
            settings,
            lambda: _StaleBoundary(),
            Logger("outbox-dispatcher", _Sink([])),
            StopSignal(),
            sender=lambda *_: AttemptResult(provider_payment_id="provider-1", http_status=202),
            metrics=metrics,
        )
        dispatcher.deliver(_StaleBoundary(), MESSAGE)
        _, samples, _ = parse_document(metrics.render())
        self.assertEqual(samples["outbox_delivery_attempts_total"], 1.0)
        self.assertEqual(samples["outbox_delivery_accepted_total"], 1.0)
        # A refused report is not routine: the delivery stays leased until the lease expires.
        self.assertEqual(samples["outbox_delivery_reports_refused_total"], 1.0)

    def test_reconciler_counts_cycles_and_applied_messages(self):
        settings = reconciler_settings(DATABASE)
        metrics = reconciler_metrics()
        reconciler = Reconciler(settings, lambda: _CountingInbox(2), Logger("inbox-reconciler", _Sink([])), metrics)
        self.assertTrue(reconciler.cycle())
        _, samples, _ = parse_document(metrics.render())
        self.assertEqual(samples["inbox_reconcile_cycles_total"], 1.0)
        self.assertEqual(samples["inbox_messages_applied_total"], 2.0)


class _StaleBoundary:
    def claim(self, owner, limit):
        return []

    def succeed(self, message, owner, provider_payment_id):
        return {"status": "error", "code": "delivery.lease_stale", "state": "PENDING"}

    def fail(self, message, owner, error_code):
        return {"status": "error", "code": "delivery.lease_stale", "state": "PENDING"}


class _CountingInbox:
    def __init__(self, applied: int) -> None:
        self.applied = applied

    def reconcile(self, limit: int) -> int:
        return self.applied


if __name__ == "__main__":
    unittest.main()
