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

    def connect_kwargs(self) -> dict[str, object]:
        return {
            "host": self.host,
            "port": self.port,
            "dbname": self.dbname,
            "user": self.user,
            "password": self.password,
            "application_name": self.application_name,
            "connect_timeout": 5,
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
    )


@dataclass(frozen=True)
class DispatcherSettings:
    provider_url: str
    owner: str
    test_profile: bool
    provider_timeout_s: float
    poll_interval_s: float
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
        env, "PROVIDER_TIMEOUT_MS", 500 if test_profile else 5000, maximum=60000
    )
    poll_ms = _positive_int(
        env, "OUTBOX_POLL_INTERVAL_MS", 100 if test_profile else 1000, maximum=60000
    )
    failpoint = env.get("COURSE_FAILPOINT", "") if test_profile else ""
    return DispatcherSettings(
        provider_url=urllib.parse.urlunsplit(parsed),
        owner=owner,
        test_profile=test_profile,
        provider_timeout_s=timeout_ms / 1000.0,
        poll_interval_s=poll_ms / 1000.0,
        database=database_settings(env, "course-outbox-dispatcher"),
        failpoint=failpoint,
        instance_id=env.get("HOSTNAME") or owner,
    )


@dataclass(frozen=True)
class AdapterSettings:
    capability: str = field(repr=False)
    callback_token: str = field(repr=False)
    hmac_secret: bytes = field(repr=False)
    receipt_api_url: str
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
    database: DatabaseSettings


def reconciler_settings(env: Mapping[str, str]) -> ReconcilerSettings:
    test_profile = _flag(env, "COURSE_TEST_PROFILE")
    poll_ms = _positive_int(
        env, "INBOX_POLL_INTERVAL_MS", 200 if test_profile else 1000, maximum=60000
    )
    if test_profile:
        # Test profile contract: reconciliation interval is at most 500 ms.
        poll_ms = min(poll_ms, 500)
    return ReconcilerSettings(
        poll_interval_s=poll_ms / 1000.0,
        batch_size=_positive_int(env, "INBOX_BATCH_SIZE", 50, maximum=100),
        test_profile=test_profile,
        database=database_settings(env, "course-inbox-reconciler"),
    )
