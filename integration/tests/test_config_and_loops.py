import contextlib
import io
import json
import unittest
from unittest import mock

from course_integration.__main__ import EXIT_CONFIG, EXIT_FAILURE, EXIT_USAGE, main as cli_main
from course_integration.config import (
    ConfigError,
    adapter_settings,
    dispatcher_settings,
    reconciler_settings,
)
from course_integration.dispatcher import Dispatcher
from course_integration.logs import Logger
from course_integration.provider import AttemptResult, OutboxMessage
from course_integration.reconciler import Reconciler
from course_integration.runtime import StopSignal

DATABASE = {"COURSE_POSTGRES_USER": "outbox_dispatcher", "COURSE_POSTGRES_PASSWORD": "db-password-value"}
DISPATCHER = {"PROVIDER_URL": "http://provider-simulator:8081", "OUTBOX_OWNER": "outbox-dispatcher", **DATABASE}
ADAPTER = {
    "PROVIDER_CALLBACK_CAPABILITY": "capability-value",
    "PROVIDER_CALLBACK_TOKEN": "token-value",
    "PROVIDER_HMAC_SECRET": "secret-value",
    "RECEIPT_API_URL": "http://gateway:8080/api/receipt/accept",
}


class ConfigurationTests(unittest.TestCase):
    def assert_rejects(self, factory, env, variable):
        with self.assertRaises(ConfigError) as caught:
            factory(env)
        self.assertIn(variable, str(caught.exception))
        for value in env.values():
            if len(value) > 6:
                self.assertNotIn(value, str(caught.exception))

    def test_dispatcher_requires_url_owner_and_credentials(self):
        for name in ("PROVIDER_URL", "OUTBOX_OWNER", "COURSE_POSTGRES_USER", "COURSE_POSTGRES_PASSWORD"):
            env = {key: value for key, value in DISPATCHER.items() if key != name}
            with self.subTest(name=name):
                self.assert_rejects(dispatcher_settings, env, name)

    def test_adapter_requires_every_secret_and_url(self):
        for name in ADAPTER:
            env = {key: value for key, value in ADAPTER.items() if key != name}
            with self.subTest(name=name):
                self.assert_rejects(adapter_settings, env, name)
            with self.subTest(name=name, empty=True):
                self.assert_rejects(adapter_settings, {**ADAPTER, name: ""}, name)

    def test_urls_must_be_absolute_http_without_credentials(self):
        for bad in ("provider-simulator:8081", "ftp://provider", "http://user:pw@provider", "http://", "http://p/x?y=1"):
            with self.subTest(url=bad):
                self.assert_rejects(dispatcher_settings, {**DISPATCHER, "PROVIDER_URL": bad}, "PROVIDER_URL")
                self.assert_rejects(adapter_settings, {**ADAPTER, "RECEIPT_API_URL": bad}, "RECEIPT_API_URL")

    def test_capability_must_be_one_path_segment(self):
        self.assert_rejects(adapter_settings, {**ADAPTER, "PROVIDER_CALLBACK_CAPABILITY": "a/b"}, "PROVIDER_CALLBACK_CAPABILITY")

    def test_hmac_secret_is_not_trimmed(self):
        settings = adapter_settings({**ADAPTER, "PROVIDER_HMAC_SECRET": " padded "})
        self.assertEqual(settings.hmac_secret, b" padded ")

    def test_adapter_settings_repr_hides_secrets(self):
        text = repr(adapter_settings(ADAPTER))
        for name in ("capability-value", "token-value", "secret-value"):
            self.assertNotIn(name, text)

    def test_test_profile_intervals(self):
        settings = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1"})
        self.assertEqual(settings.provider_timeout_s, 0.5)
        self.assertLessEqual(settings.poll_interval_s, 0.1)
        reconciler = reconciler_settings({**DATABASE, "COURSE_TEST_PROFILE": "1", "COURSE_INBOX_POLL_MS": "5000"})
        self.assertLessEqual(reconciler.poll_interval_s, 0.5)

    def test_the_attempt_budget_never_outlives_the_delivery_lease(self):
        # The published test profile is untouched: 500 ms fits into the two-second lease.
        published = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1"})
        self.assertEqual((published.provider_timeout_s, published.lease_ms), (0.5, 2000))
        self.assertFalse(published.provider_timeout_limited)
        conservative = dispatcher_settings(DISPATCHER)
        self.assertEqual((conservative.provider_timeout_s, conservative.lease_ms), (5.0, 30000))
        self.assertFalse(conservative.provider_timeout_limited)

        # A timeout that would outlive the lease is cut to fit into it with room for the report.
        # Otherwise a slow provider is reclaimed mid-attempt, every report is refused as stale and
        # the attempt budget is spent without a single backoff.
        for timeout_ms, lease_ms, expected_s in (
            ("5000", "2000", 1.5),    # the documented conservative timeout under the published lease
            ("2000", "2000", 1.5),    # the boundary: a budget equal to the lease leaves no room to report
            ("1501", "2000", 1.5),
            ("1500", "2000", 1.5),    # exactly the lease minus the margin is still allowed
            ("60000", "30000", 29.5),
            ("1000", "600", 0.3),     # a lease shorter than twice the margin keeps half of itself
            ("1000", "250", 0.125),
        ):
            with self.subTest(timeout_ms=timeout_ms, lease_ms=lease_ms):
                settings = dispatcher_settings(
                    {**DISPATCHER, "COURSE_PROVIDER_TIMEOUT_MS": timeout_ms, "COURSE_OUTBOX_LEASE_MS": lease_ms}
                )
                self.assertEqual(settings.provider_timeout_s, expected_s)
                self.assertLess(settings.provider_timeout_s * 1000, settings.lease_ms)
                self.assertEqual(settings.provider_timeout_limited, int(timeout_ms) > expected_s * 1000)

    def test_the_instance_is_named_by_its_lease_owner_not_by_the_container(self):
        # HOSTNAME inside a container is the container id: it changes on every recreate and says
        # nothing about which tenant of the queue reached the barrier.
        for owner in ("outbox-dispatcher", "outbox-dispatcher-b"):
            settings = dispatcher_settings({**DISPATCHER, "OUTBOX_OWNER": owner, "HOSTNAME": "3f2a9c1b7d5e"})
            self.assertEqual(settings.instance_id, owner)

    def test_database_calls_are_bounded_by_a_statement_timeout(self):
        kwargs = dispatcher_settings(DISPATCHER).database.connect_kwargs()
        self.assertEqual(kwargs["options"], "-c statement_timeout=10000")
        custom = reconciler_settings({**DATABASE, "COURSE_POSTGRES_STATEMENT_TIMEOUT_MS": "2500"})
        self.assertEqual(custom.database.connect_kwargs()["options"], "-c statement_timeout=2500")
        self.assert_rejects(reconciler_settings, {**DATABASE, "COURSE_POSTGRES_STATEMENT_TIMEOUT_MS": "0"},
                            "COURSE_POSTGRES_STATEMENT_TIMEOUT_MS")

    def test_database_settings_repr_hides_password(self):
        self.assertNotIn("db-password-value", repr(dispatcher_settings(DISPATCHER)))

    def test_failpoints_exist_only_in_test_profile(self):
        self.assertEqual(dispatcher_settings({**DISPATCHER, "COURSE_FAILPOINT": "after_outbox_claim"}).failpoint, "")

    def test_cli_reports_configuration_error_without_values(self):
        self.assertEqual(cli_main(["unknown"]), EXIT_USAGE)
        with (
            mock.patch.dict("os.environ", {"OUTBOX_OWNER": "outbox-dispatcher"}, clear=True),
            contextlib.redirect_stdout(io.StringIO()) as output,
        ):
            self.assertEqual(cli_main(["outbox-dispatcher"]), EXIT_CONFIG)
        record = json.loads(output.getvalue())
        self.assertEqual((record["event"], record["reason"]), ("configuration.invalid", "PROVIDER_URL is required"))

    def test_unexpected_startup_failure_is_a_structured_record(self):
        with (
            mock.patch("course_integration.reconciler.main", side_effect=OSError("port 8082 secret-detail")),
            contextlib.redirect_stdout(io.StringIO()) as output,
        ):
            self.assertEqual(cli_main(["inbox-reconciler"]), EXIT_FAILURE)
        record = json.loads(output.getvalue())
        self.assertEqual((record["event"], record["failure"]), ("process.failed", "OSError"))
        self.assertNotIn("secret-detail", output.getvalue())


MESSAGE = OutboxMessage("o-1", 1, "ext-1", "c-1", "10.00", "RUB")


class FakeOutbox:
    def __init__(self, batches):
        self.batches = list(batches)
        self.reports = []

    def claim(self, owner, limit):
        self.claim_limit = limit
        return self.batches.pop(0) if self.batches else []

    def succeed(self, message, owner, provider_payment_id):
        self.reports.append(("succeed", message.lease_version, owner, provider_payment_id))
        return {"status": "ok", "state": "DELIVERED"}

    def fail(self, message, owner, error_code):
        self.reports.append(("fail", message.lease_version, owner, error_code))
        return {"status": "ok", "state": "RETRY_WAIT", "nextAttemptInMs": 200}


class DispatcherLoopTests(unittest.TestCase):
    def dispatcher(self, outbox, results):
        settings = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1"})
        sent = []

        def sender(url, request, timeout):
            sent.append(request)
            return results.pop(0)

        dispatcher = Dispatcher(settings, lambda: outbox, Logger("outbox-dispatcher", io.StringIO()), StopSignal(), sender)
        return dispatcher, sent

    def test_one_claim_is_one_attempt_and_postgres_gets_the_report(self):
        outbox = FakeOutbox([[MESSAGE]])
        dispatcher, sent = self.dispatcher(outbox, [AttemptResult(provider_payment_id="provider-1", http_status=202)])
        self.assertTrue(dispatcher.cycle())
        self.assertEqual(outbox.claim_limit, 1)
        self.assertEqual(len(sent), 1)
        self.assertEqual(outbox.reports, [("succeed", 1, "outbox-dispatcher", "provider-1")])
        self.assertFalse(dispatcher.cycle())

    def test_stopped_failpoint_never_crosses_its_boundary(self):
        for failpoint, expected_sent in (("after_outbox_claim", 0), ("after_provider_response", 1)):
            with self.subTest(failpoint=failpoint):
                outbox = FakeOutbox([[MESSAGE]])
                settings = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1", "COURSE_FAILPOINT": failpoint})
                sent = []
                stop = StopSignal()
                stop.set()

                def sender(url, request, timeout):
                    sent.append(request)
                    return AttemptResult(provider_payment_id="provider-1", http_status=202)

                dispatcher = Dispatcher(settings, lambda: outbox, Logger("outbox-dispatcher", io.StringIO()), stop, sender)
                with contextlib.redirect_stdout(io.StringIO()) as acknowledgement, self.assertRaises(SystemExit):
                    dispatcher.cycle()
                self.assertEqual(len(sent), expected_sent)
                self.assertEqual(outbox.reports, [])
                self.assertEqual(
                    json.loads(acknowledgement.getvalue()),
                    {"event": "failpoint.reached", "name": failpoint, "instanceId": settings.instance_id},
                )

    def test_refused_report_is_logged_as_a_warning(self):
        class StaleOutbox(FakeOutbox):
            def succeed(self, message, owner, provider_payment_id):
                return {"status": "error", "code": "delivery.lease_stale", "state": "LEASED"}

        log = io.StringIO()
        settings = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1"})
        dispatcher = Dispatcher(settings, lambda: StaleOutbox([[MESSAGE]]), Logger("outbox-dispatcher", log), StopSignal(),
                                lambda url, request, timeout: AttemptResult(provider_payment_id="p", http_status=202))
        dispatcher.cycle()
        record = json.loads(log.getvalue())
        self.assertEqual((record["level"], record["reportCode"]), ("warning", "delivery.lease_stale"))

    def test_the_attempt_record_repeats_the_schedule_decided_by_postgres(self):
        log = io.StringIO()
        settings = dispatcher_settings({**DISPATCHER, "COURSE_TEST_PROFILE": "1"})
        dispatcher = Dispatcher(settings, lambda: FakeOutbox([[MESSAGE]]), Logger("outbox-dispatcher", log), StopSignal(),
                                lambda url, request, timeout: AttemptResult(error_code="transport.error.retryable"))
        dispatcher.cycle()
        record = json.loads(log.getvalue())
        self.assertEqual((record["event"], record["state"], record["nextAttemptInMs"]), ("outbox.attempt", "RETRY_WAIT", 200))

    def test_failure_is_reported_and_retry_repeats_exact_request(self):
        retry = OutboxMessage("o-1", 2, "ext-1", "c-1", "10.00", "RUB")
        outbox = FakeOutbox([[MESSAGE], [retry]])
        dispatcher, sent = self.dispatcher(
            outbox,
            [AttemptResult(error_code="transport.error.retryable"), AttemptResult(provider_payment_id="p", http_status=202)],
        )
        dispatcher.cycle()
        dispatcher.cycle()
        self.assertEqual(outbox.reports[0], ("fail", 1, "outbox-dispatcher", "transport.error.retryable"))
        self.assertEqual(outbox.reports[1][0:2], ("succeed", 2))
        self.assertEqual(sent[0], sent[1])


class ReconcilerLoopTests(unittest.TestCase):
    def test_cycle_reports_work_only_when_messages_were_applied(self):
        class FakeInbox:
            def __init__(self):
                self.results = [2, 0]

            def reconcile(self, limit):
                return self.results.pop(0)

        inbox = FakeInbox()
        reconciler = Reconciler(reconciler_settings(DATABASE), lambda: inbox, Logger("inbox-reconciler", io.StringIO()))
        self.assertTrue(reconciler.cycle())
        self.assertFalse(reconciler.cycle())


if __name__ == "__main__":
    unittest.main()
