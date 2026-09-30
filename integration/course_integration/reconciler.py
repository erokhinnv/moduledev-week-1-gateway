"""``inbox-reconciler``: one fixed PostgreSQL function, no flow logic in Python.

``delivery.reconcile_inbox`` decides which stored messages fit a waiting process, creates the
workflow signal and settles the Inbox atomically. The queue is PostgreSQL state only, so a
recreated reconciler simply continues.

Two instances of this same image run side by side without any coordination of their own: the
boundary function claims candidates with ``FOR UPDATE SKIP LOCKED``, so a message is applied at
most once no matter how many reconcilers ask for it.
"""

from __future__ import annotations

import os
from typing import Callable, Mapping

from .config import ReconcilerSettings, reconciler_settings
from .database import InboxBoundary, ReconnectingConnection
from .logs import Logger, describe_failure
from .observability import Metrics, Observability, Readiness
from .runtime import StopSignal, run_loop


def build_metrics() -> Metrics:
    metrics = Metrics()
    metrics.register("inbox_reconcile_cycles", "counter", "Reconciliation cycles run by this instance.")
    metrics.register("inbox_messages_applied", "counter", "Stored messages this instance applied.")
    return metrics


class Reconciler:
    def __init__(
        self,
        settings: ReconcilerSettings,
        boundary: Callable[[], InboxBoundary],
        logger: Logger,
        metrics: Metrics | None = None,
    ) -> None:
        self.settings = settings
        self.boundary = boundary
        self.logger = logger
        self.metrics = metrics if metrics is not None else build_metrics()

    def cycle(self) -> bool:
        applied = self.boundary().reconcile(self.settings.batch_size)
        self.metrics.increment("inbox_reconcile_cycles")
        if applied > 0:
            self.metrics.increment("inbox_messages_applied", applied)
            self.logger.info("inbox.reconciled", applied=applied)
        return applied > 0


def main(env: Mapping[str, str] | None = None) -> int:
    logger = Logger("inbox-reconciler")
    settings = reconciler_settings(os.environ if env is None else env)
    stop = StopSignal().install()
    database = ReconnectingConnection(settings.database)
    metrics = build_metrics()
    reconciler = Reconciler(settings, lambda: InboxBoundary(database.get()), logger, metrics)

    # A separate probe connection: the reconciliation call holds row locks for the length of one
    # batch, and a health check must never wait behind them.
    probe = ReconnectingConnection(settings.database)
    readiness = Readiness(probe.ping, logger)

    def on_failure(error: BaseException) -> None:
        database.reset()
        logger.warning("inbox.cycle_failed", **describe_failure(error))

    logger.info(
        "reconciler.configured",
        testProfile=settings.test_profile,
        batchSize=settings.batch_size,
        pollIntervalMs=int(settings.poll_interval_s * 1000),
    )
    with Observability(settings.health_port, readiness, metrics, logger):
        try:
            return run_loop(logger, stop, reconciler.cycle, settings.poll_interval_s, on_failure)
        finally:
            probe.reset()
            database.reset()
