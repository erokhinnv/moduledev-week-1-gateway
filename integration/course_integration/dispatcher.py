"""``outbox-dispatcher``: durable Outbox -> provider v0.2.0.

One claim is exactly one HTTP attempt. The dispatcher only reports what happened on the wire;
whether the delivery is retried, when, and what state it lands in is decided by PostgreSQL.
"""

from __future__ import annotations

import os
from typing import Callable, Mapping

from .config import DispatcherSettings, dispatcher_settings
from .database import OutboxBoundary, ReconnectingConnection
from .logs import Logger, describe_failure
from .provider import AttemptResult, OutboxMessage, PaymentRequest, build_payment_request, send_payment
from .runtime import StopSignal, reach_failpoint, run_loop

FAILPOINT_AFTER_CLAIM = "after_outbox_claim"
FAILPOINT_AFTER_RESPONSE = "after_provider_response"

Sender = Callable[[str, PaymentRequest, float], AttemptResult]


class Dispatcher:
    def __init__(
        self,
        settings: DispatcherSettings,
        boundary: Callable[[], OutboxBoundary],
        logger: Logger,
        stop: StopSignal,
        sender: Sender = send_payment,
    ) -> None:
        self.settings = settings
        self.boundary = boundary
        self.logger = logger
        self.stop = stop
        self.sender = sender

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
        result = self.sender(self.settings.provider_url, request, self.settings.provider_timeout_s)

        if self.settings.failpoint == FAILPOINT_AFTER_RESPONSE:
            reach_failpoint(FAILPOINT_AFTER_RESPONSE, self.settings.instance_id, self.stop)

        if result.accepted:
            report = boundary.succeed(message, self.settings.owner, result.provider_payment_id or "")
        else:
            report = boundary.fail(message, self.settings.owner, result.error_code or "")

        # A refused report (stale lease, rejected code) is not routine: the delivery stays leased
        # until the lease expires, so it must stand out in the log.
        log = self.logger.info if report.get("status") == "ok" else self.logger.warning
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
    dispatcher = Dispatcher(settings, lambda: OutboxBoundary(database.get()), logger, stop)

    def on_failure(error: BaseException) -> None:
        # A lost database mid-attempt leaves the lease to expire: the next claim repeats the same
        # key and body, and the provider's idempotency keeps it to one payment.
        database.reset()
        logger.warning("outbox.cycle_failed", **describe_failure(error))

    logger.info(
        "dispatcher.configured",
        owner=settings.owner,
        testProfile=settings.test_profile,
        providerTimeoutMs=int(settings.provider_timeout_s * 1000),
    )
    return run_loop(logger, stop, dispatcher.cycle, settings.poll_interval_s, on_failure)
