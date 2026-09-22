"""Anti-corruption layer: provider v0.2.0 legacy callback -> receipt v1.

The legacy wire contract stays at the edge. Inside the platform only the versioned receipt
exists, serialized as exact compact bytes and signed over those bytes.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import hmac
import json
import re
from typing import Any

LEGACY_FIELDS = frozenset({"providerPaymentId", "operationId", "result", "message", "occurredAt"})
OUTCOMES = frozenset({"COMPLETED", "REJECTED"})
MAX_IDENTIFIER = 128
MAX_MESSAGE = 500
MAX_TIMESTAMP = 64
RECEIPT_VERSION = 1
SIGNATURE_HEADER = "X-Provider-Signature"

# RFC 3339 date-time in UTC with an uppercase designator and any fractional precision.
_UTC_DATE_TIME = re.compile(
    r"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?Z$", re.ASCII
)


class CallbackRejected(ValueError):
    """Legacy callback does not match provider v0.2.0 contract. Maps to HTTP 400."""


def _no_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise CallbackRejected("duplicate JSON member")
        result[key] = value
    return result


def _reject_constant(name: str) -> Any:
    raise CallbackRejected(f"non-standard JSON constant {name}")


def _transport_text(value: Any, *, minimum: int, maximum: int) -> str:
    # bool is not str, so JSON true/false cannot pass as text.
    if not isinstance(value, str):
        raise CallbackRejected("transport field must be a string")
    if not minimum <= len(value) <= maximum:
        raise CallbackRejected("transport field length is out of range")
    if "\r" in value or "\n" in value:
        raise CallbackRejected("transport field contains CR or LF")
    return value


def _occurred_at(value: Any) -> str:
    text = _transport_text(value, minimum=1, maximum=MAX_TIMESTAMP)
    match = _UTC_DATE_TIME.fullmatch(text)
    if match is None:
        raise CallbackRejected("occurredAt must be an RFC 3339 UTC date-time")
    year, month, day, hour, minute, second = (int(part) for part in match.groups()[:6])
    try:
        dt.datetime(year, month, day, hour, minute, second, tzinfo=dt.timezone.utc)
    except ValueError as error:
        raise CallbackRejected("occurredAt is not a calendar date-time") from error
    # The original string is preserved byte for byte; parsing only validates it.
    return text


def parse_legacy_callback(raw: bytes) -> dict[str, str]:
    """Strictly parse and validate a provider v0.2.0 callback body."""
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as error:
        raise CallbackRejected("callback body is not UTF-8") from error
    try:
        value = json.loads(
            text,
            object_pairs_hook=_no_duplicate_keys,
            parse_constant=_reject_constant,
        )
    except json.JSONDecodeError as error:
        raise CallbackRejected("callback body is not JSON") from error
    if not isinstance(value, dict):
        raise CallbackRejected("callback body must be a JSON object")
    if set(value) != LEGACY_FIELDS:
        raise CallbackRejected("callback fields do not match provider v0.2.0")

    callback = {
        "providerPaymentId": _transport_text(
            value["providerPaymentId"], minimum=1, maximum=MAX_IDENTIFIER
        ),
        "operationId": _transport_text(value["operationId"], minimum=1, maximum=MAX_IDENTIFIER),
        "message": _transport_text(value["message"], minimum=0, maximum=MAX_MESSAGE),
        "occurredAt": _occurred_at(value["occurredAt"]),
    }
    result = value["result"]
    if not isinstance(result, str) or result not in OUTCOMES:
        raise CallbackRejected("result must be COMPLETED or REJECTED")
    callback["result"] = result
    return callback


def to_receipt(callback: dict[str, str]) -> dict[str, Any]:
    """Map a validated legacy callback to receipt v1. ``message`` is validated and dropped.

    ``providerPaymentId`` becomes ``messageId``: a duplicate or conflicting callback of the same
    provider payment falls into one deduplication scope.
    """
    return {
        "externalRequestId": callback["operationId"],
        "messageId": callback["providerPaymentId"],
        "occurredAt": callback["occurredAt"],
        "outcome": callback["result"],
        "providerPaymentId": callback["providerPaymentId"],
        "version": RECEIPT_VERSION,
    }


def canonical_bytes(receipt: dict[str, Any]) -> bytes:
    """Compact UTF-8 JSON with sorted keys, no BOM and no trailing LF."""
    return json.dumps(
        receipt, ensure_ascii=False, separators=(",", ":"), sort_keys=True
    ).encode("utf-8")


def signature(secret: bytes, body: bytes) -> str:
    """``v1=<lowercase hex HMAC-SHA256>`` over the exact HTTP body bytes."""
    return "v1=" + hmac.new(secret, body, hashlib.sha256).hexdigest()


def body_hash(body: bytes) -> str:
    return hashlib.sha256(body).hexdigest()
