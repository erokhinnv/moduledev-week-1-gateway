"""Environment configuration of the three entrypoints.

Every required value is validated at start-up. A missing secret or URL stops the process with
an explicit error that names the variable but never echoes its value.
"""

from __future__ import annotations

import re
import urllib.parse
from dataclasses import dataclass, field
from typing import Mapping

_HEADER_UNSAFE = re.compile(r"[\x00-\x20\x7f]")

# Room the report of an attempt needs after the provider call: the claim has already been
# committed, the lease clock is running, and ``succeed_outbox`` / ``fail_outbox`` are accepted
# only while the lease is still ours. The same margin the job lease keeps around an action.
LEASE_REPORT_MARGIN_MS = 500


class ConfigError(ValueError):
    """Configuration is missing or malformed."""


def _required(env: Mapping[str, str], name: str) -> str:
    value = env.get(name)
    # Values are not trimmed: the HMAC key, for instance, is the exact byte string.
    if value is None or value == "":
        raise ConfigError(f"{name} is required")
    return value


def _flag(env: Mapping[str, str], name: str) -> bool:
    return env.get(name, "0").strip() == "1"


def _positive_int(env: Mapping[str, str], name: str, default: int, *, maximum: int) -> int:
    raw = env.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        value = int(raw.strip())
    except ValueError as error:
        raise ConfigError(f"{name} must be an integer") from error
    if value < 1 or value > maximum:
        raise ConfigError(f"{name} must be between 1 and {maximum}")
    return value


def _http_url(env: Mapping[str, str], name: str) -> urllib.parse.SplitResult:
    value = _required(env, name)
    try:
        parsed = urllib.parse.urlsplit(value)
        parsed.port  # noqa: B018 - validates the port component
    except ValueError as error:
        raise ConfigError(f"{name} must be an absolute http(s) URL") from error
    if (
        parsed.scheme not in ("http", "https")
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or _HEADER_UNSAFE.search(value)
    ):
        raise ConfigError(f"{name} must be an absolute http(s) URL without credentials")
    return parsed


@dataclass(frozen=True)
class DatabaseSettings:
    host: str
    port: int
    dbname: str
    user: str
    password: str = field(repr=False)
    application_name: str
    statement_timeout_ms: int

    def connect_kwargs(self) -> dict[str, object]:
        return {
            "host": self.host,
            "port": self.port,
            "dbname": self.dbname,
            "user": self.user,
            "password": self.password,
            "application_name": self.application_name,
            "connect_timeout": 5,
            # Every boundary call is one short statement. A lock wait or a stuck server must end
            # in an error the loop can recover from, not freeze the process indefinitely.
            "options": f"-c statement_timeout={self.statement_timeout_ms}",
            "autocommit": True,
        }


def database_settings(env: Mapping[str, str], application_name: str) -> DatabaseSettings:
    return DatabaseSettings(
        host=env.get("COURSE_POSTGRES_HOST") or "postgres",
        port=_positive_int(env, "COURSE_POSTGRES_PORT", 5432, maximum=65535),
        dbname=env.get("COURSE_POSTGRES_DB") or "course",
        user=_required(env, "COURSE_POSTGRES_USER"),
        password=_required(env, "COURSE_POSTGRES_PASSWORD"),
        application_name=env.get("PGAPPNAME") or application_name,
        statement_timeout_ms=_positive_int(
            env, "COURSE_POSTGRES_STATEMENT_TIMEOUT_MS", 10000, maximum=300000
        ),
    )


def attempt_budget_ms(timeout_ms: int, lease_ms: int) -> int:
    """Budget of one provider attempt: the configured timeout, but never past the delivery lease.

    An attempt that outlives its lease is reclaimed by the other dispatcher while it is still in
    flight. Its report is then refused as stale, the delivery is re-sent at once with no backoff,
    and a provider that merely answers slowly burns the whole attempt budget in a few lease
    periods - ending ``DEAD`` although every request was accepted. The lease cannot be stretched
    from here (PostgreSQL owns it and does not know this timeout), so the attempt is cut to fit:
    the lease minus the report margin, and for a lease shorter than twice that margin - half of it.
    """
    return max(1, min(timeout_ms, max(lease_ms - LEASE_REPORT_MARGIN_MS, lease_ms // 2)))


@dataclass(frozen=True)
class DispatcherSettings:
    provider_url: str
    owner: str
    test_profile: bool
    # Effective budget of one attempt, already fitted into the lease (see attempt_budget_ms).
    provider_timeout_s: float
    # True when the configured COURSE_PROVIDER_TIMEOUT_MS did not fit into the delivery lease.
    provider_timeout_limited: bool
    poll_interval_s: float
    # The retry policy itself belongs to PostgreSQL: the dispatcher never decides whether a
    # delivery is retried, when, or in which state it lands. These two values are the effective
    # policy as configured for the contour, published as gauges so the endpoint answers "under
    # which policy is this process running" without reading anyone's environment. The lease has
    # one more use here: it bounds the attempt budget above.
    max_attempts: int
    lease_ms: int
    health_port: int
    database: DatabaseSettings
    failpoint: str
    instance_id: str


def dispatcher_settings(env: Mapping[str, str]) -> DispatcherSettings:
    parsed = _http_url(env, "PROVIDER_URL")
    owner = _required(env, "OUTBOX_OWNER")
    if len(owner) > 128 or _HEADER_UNSAFE.search(owner):
        raise ConfigError("OUTBOX_OWNER must be a printable identifier up to 128 characters")
    test_profile = _flag(env, "COURSE_TEST_PROFILE")
    timeout_ms = _positive_int(
        env, "COURSE_PROVIDER_TIMEOUT_MS", 500 if test_profile else 5000, maximum=60000
    )
    poll_ms = _positive_int(
        env, "COURSE_OUTBOX_POLL_MS", 100 if test_profile else 1000, maximum=60000
    )
    if test_profile:
        # Test profile contract: the dispatcher polls at least every 100 ms.
        poll_ms = min(poll_ms, 100)
    failpoint = env.get("COURSE_FAILPOINT", "").strip() if test_profile else ""
    lease_ms = _positive_int(
        env, "COURSE_OUTBOX_LEASE_MS", 2000 if test_profile else 30000, maximum=600000
    )
    budget_ms = attempt_budget_ms(timeout_ms, lease_ms)
    return DispatcherSettings(
        provider_url=urllib.parse.urlunsplit(parsed),
        owner=owner,
        test_profile=test_profile,
        provider_timeout_s=budget_ms / 1000.0,
        provider_timeout_limited=budget_ms < timeout_ms,
        poll_interval_s=poll_ms / 1000.0,
        max_attempts=_positive_int(
            env, "COURSE_OUTBOX_MAX_ATTEMPTS", 4 if test_profile else 5, maximum=20
        ),
        lease_ms=lease_ms,
        health_port=_positive_int(env, "COURSE_DISPATCHER_HEALTH_PORT", 8080, maximum=65535),
        database=database_settings(env, "course-outbox-dispatcher"),
        failpoint=failpoint,
        # The instance is identified by its lease owner - the same value autocheck.outbox shows
        # in lease_owner - not by the container hostname, which changes on every recreate.
        instance_id=owner,
    )


@dataclass(frozen=True)
class AdapterSettings:
    capability: str = field(repr=False)
    callback_token: str = field(repr=False)
    hmac_secret: bytes = field(repr=False)
    receipt_api_url: str
    # Readiness of the adapter is readiness of the gateway: it owns no state and cannot confirm a
    # callback without the platform. PostgreSQL is deliberately absent - the adapter has no
    # database credentials at all, so it could not probe it even if the contract asked.
    dependency_probe_url: str
    listen_host: str
    listen_port: int
    api_timeout_s: float
    test_profile: bool


def adapter_settings(env: Mapping[str, str]) -> AdapterSettings:
    capability = _required(env, "PROVIDER_CALLBACK_CAPABILITY")
    if len(capability) > 512 or "/" in capability or _HEADER_UNSAFE.search(capability):
        raise ConfigError("PROVIDER_CALLBACK_CAPABILITY must be a single printable path segment")
    token = _required(env, "PROVIDER_CALLBACK_TOKEN")
    if _HEADER_UNSAFE.search(token):
        raise ConfigError("PROVIDER_CALLBACK_TOKEN must not contain whitespace or control characters")
    secret = _required(env, "PROVIDER_HMAC_SECRET")
    parsed = _http_url(env, "RECEIPT_API_URL")
    test_profile = _flag(env, "COURSE_TEST_PROFILE")
    return AdapterSettings(
        capability=capability,
        callback_token=token,
        hmac_secret=secret.encode("utf-8"),
        receipt_api_url=urllib.parse.urlunsplit(parsed),
        dependency_probe_url=urllib.parse.urlunsplit(
            (parsed.scheme, parsed.netloc, "/health/live", "", "")
        ),
        listen_host=env.get("RECEIPT_ADAPTER_HOST") or "0.0.0.0",
        listen_port=_positive_int(env, "RECEIPT_ADAPTER_PORT", 8082, maximum=65535),
        api_timeout_s=_positive_int(env, "RECEIPT_API_TIMEOUT_MS", 10000, maximum=60000) / 1000.0,
        test_profile=test_profile,
    )


@dataclass(frozen=True)
class ReconcilerSettings:
    poll_interval_s: float
    batch_size: int
    test_profile: bool
    health_port: int
    database: DatabaseSettings


def reconciler_settings(env: Mapping[str, str]) -> ReconcilerSettings:
    test_profile = _flag(env, "COURSE_TEST_PROFILE")
    poll_ms = _positive_int(
        env, "COURSE_INBOX_POLL_MS", 500 if test_profile else 1000, maximum=60000
    )
    if test_profile:
        # Test profile contract: reconciliation interval is at most 500 ms.
        poll_ms = min(poll_ms, 500)
    return ReconcilerSettings(
        poll_interval_s=poll_ms / 1000.0,
        batch_size=_positive_int(env, "INBOX_BATCH_SIZE", 50, maximum=100),
        test_profile=test_profile,
        health_port=_positive_int(env, "COURSE_RECONCILER_HEALTH_PORT", 8080, maximum=65535),
        database=database_settings(env, "course-inbox-reconciler"),
    )
