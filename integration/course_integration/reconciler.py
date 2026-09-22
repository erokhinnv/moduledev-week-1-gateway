"""``inbox-reconciler``: one fixed PostgreSQL function, no flow logic in Python.

``delivery.reconcile_inbox`` decides which stored messages fit a waiting process, creates the
workflow signal and settles the Inbox atomically. The queue is PostgreSQL state only, so a
recreated reconciler simply continues.
"""

from __future__ import annotations

import os
from typing import Callable, Mapping

from .config import ReconcilerSettings, reconciler_settings
from .database import InboxBoundary, ReconnectingConnection
from .logs import Logger, describe_failure
from .runtime import StopSignal, run_loop


class Reconciler:
    def __init__(
        self,
        settings: ReconcilerSettings,
        boundary: Callable[[], InboxBoundary],
        logger: Logger,
    ) -> None:
        self.settings = settings
        self.boundary = boundary
        self.logger = logger

    def cycle(self) -> bool:
        applied = self.boundary().reconcile(self.settings.batch_size)
        if applied > 0:
            self.logger.info("inbox.reconciled", applied=applied)
        return applied > 0


def main(env: Mapping[str, str] | None = None) -> int:
    logger = Logger("inbox-reconciler")
    settings = reconciler_settings(os.environ if env is None else env)
    stop = StopSignal().install()
    database = ReconnectingConnection(settings.database)
    reconciler = Reconciler(settings, lambda: InboxBoundary(database.get()), logger)

    def on_failure(error: BaseException) -> None:
        database.reset()
        logger.warning("inbox.cycle_failed", **describe_failure(error))

    logger.info("reconciler.configured", testProfile=settings.test_profile, batchSize=settings.batch_size)
    return run_loop(logger, stop, reconciler.cycle, settings.poll_interval_s, on_failure)
