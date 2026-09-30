"""``outbox-dispatcher``: durable Outbox -> provider v0.2.0.

One claim is exactly one HTTP attempt. The dispatcher only reports what happened on the wire;
whether the delivery is retried, when, and what state it lands in is decided by PostgreSQL.

Two instances of this same image run side by side with different lease owners. They may send the
same request twice, but the stable ``externalRequestId`` and the provider's own idempotency keep
it to one external payment - and the conditional owner/leaseVersion report keeps a late attempt
from overwriting the result of the new owner.
"""

from __future__ import annotations

import os
from typing import Callable, Mapping

from .config import DispatcherSettings, dispatcher_settings
from .database import OutboxBoundary, ReconnectingConnection
from .logs import Logger, describe_failure
from .observability import Metrics, Observability, Readiness
from .provider import AttemptResult, OutboxMessage, PaymentRequest, build_payment_request, send_payment
from .runtime import StopSignal, reach_failpoint, run_loop

FAILPOINT_AFTER_CLAIM = "after_outbox_claim"
FAILPOINT_AFTER_RESPONSE = "after_provider_response"

Sender = Callable[[str, PaymentRequest, float], AttemptResult]


def build_metrics(settings: DispatcherSettings) -> Metrics:
    """Own metrics of one dispatcher instance: no labels, therefore no cardinality to bound."""
    metrics = Metrics()
    metrics.register("outbox_delivery_attempts", "counter", "Provider HTTP attempts made by this instance.")
    metrics.register("outbox_delivery_accepted", "counter", "Attempts the provider accepted.")
    metrics.register("outbox_delivery_rejected", "counter", "Attempts that failed on the wire.")
    metrics.register("outbox_delivery_reports_refused", "counter", "Reports PostgreSQL refused as stale.")
    metrics.register("outbox_delivery_policy_max_attempts", "gauge", "Configured attempt budget of one delivery.")
    metrics.register("outbox_delivery_policy_lease_ms", "gauge", "Configured delivery lease in milliseconds.")
    metrics.set("outbox_delivery_policy_max_attempts", settings.max_attempts)
    metrics.set("outbox_delivery_policy_lease_ms", settings.lease_ms)
    return metrics


class Dispatcher:
    def __init__(
        self,
        settings: DispatcherSettings,
        boundary: Callable[[], OutboxBoundary],
        logger: Logger,
        stop: StopSignal,
        sender: Sender = send_payment,
        metrics: Metrics | None = None,
    ) -> None:
        self.settings = settings
        self.boundary = boundary
        self.logger = logger
        self.stop = stop
        self.sender = sender
        self.metrics = metrics if metrics is not None else build_metrics(settings)

    def cycle(self) -> bool:
        boundary = self.boundary()
        # One message per claim keeps the lease honest: nothing waits leased behind another
        # message's HTTP timeout.
        messages = boundary.claim(self.settings.owner, 1)
        for message in messages:
            self.deliver(boundary, message)
            if self.stop.requested:
                break
        return bool(messages)

    def deliver(self, boundary: OutboxBoundary, message: OutboxMessage) -> None:
        if self.settings.failpoint == FAILPOINT_AFTER_CLAIM:
            reach_failpoint(FAILPOINT_AFTER_CLAIM, self.settings.instance_id, self.stop)

        request = build_payment_request(message)
        self.metrics.increment("outbox_delivery_attempts")
        result = self.sender(self.settings.provider_url, request, self.settings.provider_timeout_s)

        if self.settings.failpoint == FAILPOINT_AFTER_RESPONSE:
            reach_failpoint(FAILPOINT_AFTER_RESPONSE, self.settings.instance_id, self.stop)

        if result.accepted:
            self.metrics.increment("outbox_delivery_accepted")
            report = boundary.succeed(message, self.settings.owner, result.provider_payment_id or "")
        else:
            self.metrics.increment("outbox_delivery_rejected")
            report = boundary.fail(message, self.settings.owner, result.error_code or "")

        # A refused report (stale lease, rejected code) is not routine: the delivery stays leased
        # until the lease expires, so it must stand out in the log and in the metrics.
        accepted_report = report.get("status") == "ok"
        if not accepted_report:
            self.metrics.increment("outbox_delivery_reports_refused")
        log = self.logger.info if accepted_report else self.logger.warning
        log(
            "outbox.attempt",
            outboxId=message.outbox_id,
            externalRequestId=message.external_request_id,
            correlationId=message.correlation_id,
            leaseVersion=message.lease_version,
            httpStatus=result.http_status,
            errorCode=result.error_code,
            reportStatus=report.get("status"),
            reportCode=report.get("code"),
            state=report.get("state"),
            # The schedule is PostgreSQL's decision; the log only repeats it so that a delivery can
            # be traced attempt by attempt without reading the physical tables.
            nextAttemptInMs=report.get("nextAttemptInMs"),
        )


def main(env: Mapping[str, str] | None = None) -> int:
    logger = Logger("outbox-dispatcher")
    settings = dispatcher_settings(os.environ if env is None else env)
    stop = StopSignal().install()
    database = ReconnectingConnection(settings.database)
    metrics = build_metrics(settings)
    dispatcher = Dispatcher(
        settings, lambda: OutboxBoundary(database.get()), logger, stop, metrics=metrics
    )

    # Readiness uses its OWN connection: a probe arriving during a provider timeout must not
    # borrow the connection that the attempt in flight is about to report on.
    probe = ReconnectingConnection(settings.database)
    readiness = Readiness(probe.ping, logger)

    def on_failure(error: BaseException) -> None:
        # A lost database mid-attempt leaves the lease to expire: the next claim repeats the same
        # key and body, and the provider's idempotency keeps it to one payment.
        database.reset()
        logger.warning("outbox.cycle_failed", **describe_failure(error))

    logger.info(
        "dispatcher.configured",
        owner=settings.owner,
        testProfile=settings.test_profile,
        # The effective budget: a configured timeout that would outlive the delivery lease is cut
        # to fit into it, and the record says so instead of leaving it to be guessed.
        providerTimeoutMs=int(settings.provider_timeout_s * 1000),
        providerTimeoutLimitedByLease=settings.provider_timeout_limited,
        pollIntervalMs=int(settings.poll_interval_s * 1000),
        maxAttempts=settings.max_attempts,
        leaseMs=settings.lease_ms,
    )
    # Provider outage is NOT part of readiness: a growing Outbox is the designed behaviour, and
    # reporting "not ready" would ask the orchestrator to take the process out of rotation at the
    # exact moment its work starts accumulating.
    with Observability(settings.health_port, readiness, metrics, logger):
        try:
            return run_loop(logger, stop, dispatcher.cycle, settings.poll_interval_s, on_failure)
        finally:
            probe.reset()
            database.reset()
