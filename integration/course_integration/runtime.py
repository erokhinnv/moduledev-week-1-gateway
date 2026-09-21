"""Process lifecycle shared by the polling entrypoints."""

from __future__ import annotations

import json
import signal
import sys
import threading
import time
from typing import Callable

from .logs import Logger


class StopSignal:
    """SIGTERM/SIGINT request a graceful stop between units of work.

    The interpreter is PID 1 in the container and has no default SIGTERM handler there; without
    this, ``docker compose stop`` would wait for the kill timeout on every stop.
    """

    def __init__(self) -> None:
        self._event = threading.Event()

    def install(self) -> "StopSignal":
        for name in ("SIGTERM", "SIGINT"):
            signum = getattr(signal, name, None)
            if signum is not None:
                signal.signal(signum, lambda *_: self._event.set())
        return self

    def set(self) -> None:
        self._event.set()

    @property
    def requested(self) -> bool:
        return self._event.is_set()

    def wait(self, seconds: float) -> bool:
        """Sleep up to ``seconds``; returns True when a stop was requested."""
        return self._event.wait(max(seconds, 0.0))


class Backoff:
    """Bounded exponential pause after infrastructure failures (database unavailable, etc.)."""

    def __init__(self, initial_s: float = 0.25, maximum_s: float = 5.0) -> None:
        self.initial_s = initial_s
        self.maximum_s = maximum_s
        self.failures = 0

    def reset(self) -> None:
        self.failures = 0

    def next_delay(self) -> float:
        self.failures += 1
        return min(self.initial_s * (2 ** min(self.failures - 1, 10)), self.maximum_s)


def reach_failpoint(name: str, instance_id: str, stop: StopSignal) -> None:
    """Deterministic acknowledgement barrier of the closed test profile.

    Prints exactly one JSON line and blocks until the container is stopped.
    """
    sys.stdout.write(
        json.dumps(
            {"event": "failpoint.reached", "name": name, "instanceId": instance_id},
            separators=(",", ":"),
        )
        + "\n"
    )
    sys.stdout.flush()
    while not stop.wait(3600):
        pass


def run_loop(
    logger: Logger,
    stop: StopSignal,
    cycle: Callable[[], bool],
    idle_s: float,
    on_failure: Callable[[BaseException], None],
) -> int:
    """Run ``cycle`` until stopped. ``cycle`` returns True when it did work (poll again now)."""
    backoff = Backoff()
    logger.info("loop.started", idleMs=int(idle_s * 1000))
    while not stop.requested:
        try:
            busy = cycle()
            backoff.reset()
        except Exception as error:  # noqa: BLE001 - the loop must survive infrastructure faults
            on_failure(error)
            if stop.wait(backoff.next_delay()):
                break
            continue
        if not busy and stop.wait(idle_s):
            break
    logger.info("loop.stopped")
    return 0


def monotonic_ms() -> int:
    return int(time.monotonic() * 1000)
