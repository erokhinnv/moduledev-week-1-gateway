#!/usr/bin/env python3
"""Print a fresh local-only environment for ``docker compose up``.

    eval "$(python3 scripts/dev_env.py)"

Tracked Compose has no defaults for JWT, HMAC, capability, tokens or the observer password. This
helper generates random synthetic values for one local session and issues the ``receipt-provider``
JWT (scope ``receipt:write``) with the same signing key the API validates. Nothing is written to
disk: the values live only in the current shell.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import secrets
import shlex
import time

ISSUER = "moduledev-course"
AUDIENCE = "moduledev-api"


def _b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def issue_token(key: str, subject: str, consumer: str, scopes: list[str], ttl_s: int) -> str:
    now = int(time.time())
    header = _b64url(b'{"alg":"HS256","typ":"JWT"}')
    payload = _b64url(
        json.dumps(
            {
                "iss": ISSUER,
                "aud": AUDIENCE,
                "sub": subject,
                "consumer": consumer,
                "scope": " ".join(scopes),
                "iat": now - 5,
                "exp": now + ttl_s,
            },
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
    )
    signing_input = f"{header}.{payload}".encode("ascii")
    signature = _b64url(hmac.new(key.encode("utf-8"), signing_input, hashlib.sha256).digest())
    return f"{header}.{payload}.{signature}"


def main() -> None:
    key = secrets.token_urlsafe(48)
    values = {
        "COURSE_TEST_PROFILE": "1",
        "COURSE_JWT_ISSUER": ISSUER,
        "COURSE_JWT_AUDIENCE": AUDIENCE,
        "COURSE_JWT_SIGNING_KEY": key,
        "PROVIDER_HMAC_SECRET": secrets.token_urlsafe(40),
        "PROVIDER_CALLBACK_CAPABILITY": secrets.token_urlsafe(24),
        "PROVIDER_AUDIT_TOKEN": secrets.token_urlsafe(24),
        # Password of the read-only observer role. It reaches PostgreSQL once, when the volume is
        # initialised, so the value of the session that created the volume is the one that works.
        "COURSE_AUTOCHECK_PASSWORD": secrets.token_urlsafe(24),
        "PROVIDER_CALLBACK_TOKEN": issue_token(
            key, "receipt-provider", "integration", ["receipt:write"], ttl_s=7 * 24 * 3600
        ),
    }
    for name, value in values.items():
        print(f"export {name}={shlex.quote(value)}")


if __name__ == "__main__":
    main()
