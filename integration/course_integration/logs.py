"""Structured JSON logs.

Only technical facts go to the log: identifiers, states, HTTP statuses, error classes. Callback
messages, bodies, tokens, signatures, capability and database errors text never do.
"""

from __future__ import annotations

import datetime as dt
import json
import sys
import threading
from typing import Any, TextIO

_LOCK = threading.Lock()


class Logger:
    def __init__(self, service: str, stream: TextIO | None = None) -> None:
        self.service = service
        self._stream = stream

    def _write(self, level: str, event: str, fields: dict[str, Any]) -> None:
        record = {
            "ts": dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds"),
            "level": level,
            "service": self.service,
            "event": event,
            **fields,
        }
        line = json.dumps(record, ensure_ascii=True, separators=(",", ":"), default=str)
        stream = self._stream or sys.stdout
        with _LOCK:
            stream.write(line + "\n")
            stream.flush()

    def info(self, event: str, **fields: Any) -> None:
        self._write("info", event, fields)

    def warning(self, event: str, **fields: Any) -> None:
        self._write("warning", event, fields)

    def error(self, event: str, **fields: Any) -> None:
        self._write("error", event, fields)


def describe_failure(error: BaseException) -> dict[str, str]:
    """Class and SQLSTATE only: driver messages may quote values from the database."""
    result = {"failure": type(error).__name__}
    sqlstate = getattr(error, "sqlstate", None)
    if isinstance(sqlstate, str) and sqlstate:
        result["sqlstate"] = sqlstate
    return result
