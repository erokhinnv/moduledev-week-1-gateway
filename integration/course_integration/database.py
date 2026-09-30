"""The only SQL the integration processes ever send: calls of fixed ``delivery.*`` functions.

There is no table access here, no ``api.invoke`` and no dynamic SQL. The role behind the
connection has ``EXECUTE`` on exactly these functions and nothing else.
"""

from __future__ import annotations

import json
from typing import Any, Protocol

from .config import DatabaseSettings
from .provider import OutboxMessage


class Connection(Protocol):
    closed: bool

    def execute(self, query: str, params: tuple[Any, ...]) -> Any: ...

    def close(self) -> None: ...


def connect(settings: DatabaseSettings) -> Connection:
    import psycopg

    return psycopg.connect(**settings.connect_kwargs())


def _json_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if isinstance(value, str):
        parsed = json.loads(value)
        if isinstance(parsed, dict):
            return parsed
    raise TypeError("delivery boundary returned a non-object")


class OutboxBoundary:
    def __init__(self, connection: Connection) -> None:
        self.connection = connection

    def claim(self, owner: str, limit: int) -> list[OutboxMessage]:
        rows = self.connection.execute(
            "SELECT outbox_id::text, lease_version, external_request_id, correlation_id::text, "
            "amount, currency FROM delivery.claim_outbox(%s, %s)",
            (owner, limit),
        ).fetchall()
        return [
            OutboxMessage(
                outbox_id=row[0],
                lease_version=int(row[1]),
                external_request_id=row[2],
                correlation_id=row[3],
                amount=row[4],
                currency=row[5],
            )
            for row in rows
        ]

    def succeed(self, message: OutboxMessage, owner: str, provider_payment_id: str) -> dict[str, Any]:
        row = self.connection.execute(
            "SELECT delivery.succeed_outbox(%s::uuid, %s, %s, %s)",
            (message.outbox_id, owner, message.lease_version, provider_payment_id),
        ).fetchone()
        return _json_object(row[0])

    def fail(self, message: OutboxMessage, owner: str, error_code: str) -> dict[str, Any]:
        row = self.connection.execute(
            "SELECT delivery.fail_outbox(%s::uuid, %s, %s, %s)",
            (message.outbox_id, owner, message.lease_version, error_code),
        ).fetchone()
        return _json_object(row[0])


class InboxBoundary:
    def __init__(self, connection: Connection) -> None:
        self.connection = connection

    def reconcile(self, limit: int) -> int:
        row = self.connection.execute("SELECT delivery.reconcile_inbox(%s)", (limit,)).fetchone()
        return int(row[0])


class ReconnectingConnection:
    """Keeps one connection and reopens it after a failure; state lives in PostgreSQL only."""

    def __init__(self, settings: DatabaseSettings, factory=connect) -> None:
        self._settings = settings
        self._factory = factory
        self._connection: Connection | None = None

    def get(self) -> Connection:
        if self._connection is None or self._connection.closed:
            self._connection = self._factory(self._settings)
        return self._connection

    def reset(self) -> None:
        connection, self._connection = self._connection, None
        if connection is not None:
            try:
                connection.close()
            except Exception:  # noqa: BLE001 - closing a broken connection must not fail
                pass

    def ping(self) -> bool:
        """Readiness probe: PostgreSQL answered a trivial statement.

        The probe sends no boundary function and needs no privilege beyond CONNECT, so a role
        with EXECUTE on exactly one function stays a role with EXECUTE on exactly one function.
        A broken connection is dropped here, not on the next unit of work.
        """
        try:
            row = self.get().execute("SELECT 1", ()).fetchone()
        except Exception:  # noqa: BLE001 - an unavailable dependency is not a crash
            self.reset()
            return False
        return bool(row) and row[0] == 1
