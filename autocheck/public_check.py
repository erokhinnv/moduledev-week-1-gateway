#!/usr/bin/env python3
"""Run the published week-1 black-box checks against a local submission."""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import datetime as dt
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


COMPOSE_NAMES = (
    "compose.yaml",
    "compose.yml",
    "docker-compose.yml",
    "docker-compose.yaml",
)
REQUIRED_SERVICES = {"gateway", "api", "cli", "postgres"}
SOLUTION_HEADINGS = {
    "архитектура",
    "запуск",
    "конфигурация",
    "миграции",
    "проверка",
    "диагностика",
    "ограничения",
}
SOLUTION_LITERALS = (
    "docker compose up -d --build",
    "./check.sh",
    "http://localhost:8080",
    "/health/live",
    "/health/ready",
    "/openapi/default.json",
    "COURSE_JWT_ISSUER",
    "gateway",
    "api",
    "cli",
    "postgres",
    "C4",
    "ADR",
)


@dataclass
class CommandResult:
    command: list[str]
    exitCode: int
    stdout: str
    stderr: str


@dataclass
class Response:
    status: int
    body: Any
    text: str
    error: str | None = None


@dataclass
class Check:
    name: str
    group: str
    passed: bool
    expected: Any
    actual: Any
    required: bool = True


def extract_solution_section(text: str) -> str | None:
    match = re.search(r"(?mi)^##[ \t]+Решение[ \t]*$", text)
    if not match:
        return None
    next_heading = re.search(r"(?m)^##[ \t]+\S.*$", text[match.end() :])
    end = match.end() + next_heading.start() if next_heading else len(text)
    return text[match.end() : end]


def readme_findings(repo: Path) -> list[str]:
    path = repo / "README.md"
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return ["missing root README.md"]
    section = extract_solution_section(text)
    if section is None:
        return ["missing level-2 section: Решение"]
    headings = {
        value.strip().casefold()
        for value in re.findall(r"(?mi)^###[ \t]+(.+?)[ \t]*$", section)
    }
    findings = [
        f"missing solution subsection: {heading}"
        for heading in sorted(SOLUTION_HEADINGS - headings)
    ]
    findings.extend(
        f"missing solution README value: {value}"
        for value in SOLUTION_LITERALS
        if value not in section
    )
    return findings


def tracked_paths(repo: Path) -> list[str]:
    try:
        result = subprocess.run(
            ["git", "ls-files", "-z"],
            cwd=repo,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        result = None
    if result is not None and result.returncode == 0:
        return [value for value in result.stdout.split("\0") if value]
    return [
        str(path.relative_to(repo))
        for path in repo.rglob("*")
        if path.is_file() and ".git" not in path.parts
    ]


def repository_hygiene_findings(repo: Path) -> list[str]:
    findings: list[str] = []
    paths = tracked_paths(repo)
    if ".gitignore" not in paths:
        findings.append("missing tracked .gitignore")
    for value in paths:
        path = Path(value)
        parts = {part.casefold() for part in path.parts}
        name = path.name.casefold()
        if parts.intersection({"bin", "obj", ".vs", ".idea", "__pycache__"}):
            findings.append(f"tracked generated directory: {value}")
        elif name == ".env" or (name.startswith(".env.") and name != ".env.example"):
            findings.append(f"tracked environment file: {value}")
        elif name == "week-1-public-report.json" or name.endswith(".log"):
            findings.append(f"tracked generated report or log: {value}")
        elif name in {".ds_store", "thumbs.db"}:
            findings.append(f"tracked OS metadata: {value}")
    return findings


def published_ports(service: Any) -> set[int]:
    if not isinstance(service, dict):
        return set()
    result: set[int] = set()
    for entry in service.get("ports", []) or []:
        value: Any = None
        if isinstance(entry, dict):
            value = entry.get("published")
        elif isinstance(entry, str):
            default_port = re.search(r":-(\d+)}(?=:)", entry)
            if default_port:
                value = default_port.group(1)
            else:
                parts = entry.rsplit(":", 2)
                if len(parts) >= 2:
                    value = parts[-2]
        if isinstance(value, str):
            default_port = re.fullmatch(r"\$\{[^}]+:-(\d+)}", value)
            if default_port:
                value = default_port.group(1)
        try:
            result.add(int(value))
        except (TypeError, ValueError):
            continue
    return result


def b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def sign_token(secret: str, payload: dict[str, Any]) -> str:
    header_part = b64url(
        json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode()
    )
    payload_part = b64url(json.dumps(payload, separators=(",", ":")).encode())
    signing_input = f"{header_part}.{payload_part}".encode("ascii")
    signature = hmac.new(secret.encode(), signing_input, hashlib.sha256).digest()
    return f"{header_part}.{payload_part}.{b64url(signature)}"


def issue_token(
    secret: str,
    subject: str,
    consumer: str,
    scopes: list[str],
    *,
    expired: bool = False,
) -> str:
    now = int(time.time())
    return sign_token(
        secret,
        {
            "iss": "moduledev-course",
            "aud": "moduledev-api",
            "sub": subject,
            "consumer": consumer,
            "scope": " ".join(scopes),
            "iat": now - 120 if expired else now,
            "exp": now - 60 if expired else now + 3600,
        },
    )


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


class Checker:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.repo = args.repo.expanduser().resolve()
        self.fixtures = args.fixtures.expanduser().resolve()
        self.fixture = self.load_fixture()
        self.module = self.fixture["module"]
        self.action = self.fixture["action"]
        self.target_schema = self.fixture["targetSchema"]
        self.canary_table = self.fixture["canaryTable"]
        self.mode_field = self.fixture["modeField"]
        self.value_field = self.fixture["valueField"]
        self.outcome = self.fixture["outcome"]
        self.forced_error_code = self.fixture["forcedErrorCode"]
        self.route_key = f"{self.module}.{self.action}"
        self.action_path = f"/api/{self.module}/{self.action}"
        self.compose_file = self.resolve_compose_file()
        self.started = dt.datetime.now(dt.UTC)
        self.checks: list[Check] = []
        self.commands: list[CommandResult] = []
        self.secret = secrets.token_urlsafe(48)
        self.temp = Path(tempfile.mkdtemp(prefix="moduledev-week1-public-"))
        self.override = self.temp / "autocheck.override.yaml"
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.tokens = {
            "worker": issue_token(
                self.secret,
                "workflow-worker",
                "internal",
                ["workflow:execute", "payment:internal"],
            ),
            "candidate": issue_token(
                self.secret,
                "candidate-client",
                "web",
                ["payment:write", "payment:read", "workflow:read"],
            ),
            "write": issue_token(self.secret, "write-client", "web", ["payment:write"]),
            "read": issue_token(self.secret, "read-client", "web", ["payment:read"]),
            "denied": issue_token(self.secret, "denied-client", "test", []),
            "expired": issue_token(
                self.secret, "candidate-client", "web", ["payment:write"], expired=True
            ),
            "malformed_claim": sign_token(
                self.secret,
                {
                    "iss": 42,
                    "aud": "moduledev-api",
                    "sub": "candidate-client",
                    "consumer": "web",
                    "scope": "payment:write",
                    "iat": int(time.time()),
                    "exp": int(time.time()) + 900,
                },
            ),
        }

    def load_fixture(self) -> dict[str, str]:
        path = self.fixtures / "fixture.json"
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise ValueError(f"Invalid fixture metadata {path}: {error}") from error
        required = {
            "module",
            "action",
            "targetSchema",
            "canaryTable",
            "modeField",
            "valueField",
            "outcome",
            "forcedErrorCode",
            "migrationFile",
            "manifestV1",
            "manifestV2",
        }
        if not isinstance(value, dict) or set(value) != required:
            raise ValueError(
                f"Fixture metadata fields must be exactly {sorted(required)}"
            )
        if any(not isinstance(item, str) or not item for item in value.values()):
            raise ValueError("Fixture metadata values must be non-empty strings")
        for field in (
            "module",
            "action",
            "targetSchema",
            "canaryTable",
            "modeField",
            "valueField",
        ):
            if not re.fullmatch(r"[a-z][a-z0-9_]{0,62}", value[field]):
                raise ValueError(f"Invalid SQL identifier in fixture metadata: {field}")
        if not re.fullmatch(r"[A-Z][A-Z0-9_]{0,62}", value["outcome"]):
            raise ValueError("Invalid fixture outcome")
        if not re.fullmatch(
            r"[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+", value["forcedErrorCode"]
        ):
            raise ValueError("Invalid fixture error code")
        for field in ("migrationFile", "manifestV1", "manifestV2"):
            if Path(value[field]).name != value[field]:
                raise ValueError(
                    f"Fixture filename must not contain a directory: {field}"
                )
        return value

    def probe_payload(self, mode: str, value: str, **extra: Any) -> dict[str, Any]:
        return {self.mode_field: mode, self.value_field: value, **extra}

    def resolve_compose_file(self) -> Path:
        if self.args.compose_file:
            path = self.args.compose_file.expanduser().resolve()
            if not path.is_file():
                raise ValueError(f"Compose file does not exist: {path}")
            return path
        for name in COMPOSE_NAMES:
            path = self.repo / name
            if path.is_file():
                return path
        raise ValueError("No root Compose file found")

    def check(
        self,
        name: str,
        criterion: str,
        condition: bool,
        expected: Any,
        actual: Any,
        *,
        required: bool = True,
    ) -> None:
        self.checks.append(
            Check(name, criterion, bool(condition), expected, actual, required)
        )

    def command(
        self,
        command: list[str],
        *,
        timeout: int = 180,
        cwd: Path | None = None,
    ) -> CommandResult:
        try:
            completed = subprocess.run(
                command,
                cwd=cwd or self.repo,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=timeout,
                check=False,
                env=os.environ.copy(),
            )
            result = CommandResult(
                command, completed.returncode, completed.stdout, completed.stderr
            )
        except subprocess.TimeoutExpired as error:
            result = CommandResult(
                command,
                124,
                error.stdout if isinstance(error.stdout, str) else "",
                (error.stderr if isinstance(error.stderr, str) else "")
                + f"\nTimed out after {timeout}s",
            )
        except OSError as error:
            result = CommandResult(command, 127, "", str(error))
        self.commands.append(result)
        return result

    def compose(self, *arguments: str, timeout: int = 180) -> CommandResult:
        prefix = (
            ["bash", str(self.args.compose_wrapper.expanduser().resolve())]
            if self.args.compose_wrapper
            else ["docker", "compose"]
        )
        return self.command(
            [
                *prefix,
                "--project-name",
                self.args.project_name,
                "-f",
                str(self.compose_file),
                "-f",
                str(self.override),
                *arguments,
            ],
            timeout=timeout,
        )

    def write_override(self) -> None:
        content = f"""services:
  api:
    environment:
      COURSE_JWT_ISSUER: moduledev-course
      COURSE_JWT_AUDIENCE: moduledev-api
      COURSE_JWT_SIGNING_KEY: {json.dumps(self.secret)}
"""
        self.override.write_text(content, encoding="utf-8")
        os.chmod(self.override, 0o600)

    def cli(
        self, *arguments: str, mount: Path | None = None
    ) -> tuple[CommandResult, Any]:
        source = (mount or self.fixtures).expanduser().resolve()
        result = self.compose(
            "run",
            "--rm",
            "-T",
            "--no-deps",
            "-v",
            f"{source}:/autocheck/input:ro",
            "cli",
            *arguments,
            timeout=180,
        )
        try:
            parsed = (
                json.loads(result.stdout.strip()) if result.stdout.strip() else None
            )
        except json.JSONDecodeError:
            parsed = None
        return result, parsed

    def psql(self, sql: str) -> CommandResult:
        return self.compose(
            "exec",
            "-T",
            "postgres",
            "psql",
            "-X",
            "-A",
            "-t",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            "postgres",
            "-d",
            "course",
            "-c",
            sql,
            timeout=60,
        )

    def psql_json(self, sql: str) -> tuple[CommandResult, Any]:
        result = self.psql(sql)
        try:
            parsed = (
                json.loads(result.stdout.strip()) if result.stdout.strip() else None
            )
        except json.JSONDecodeError:
            parsed = None
        return result, parsed

    def request(
        self,
        method: str,
        path: str,
        *,
        payload: Any | None = None,
        token: str | None = None,
        key: str | None = None,
        version: int | str | None = None,
        timeout: float = 20.0,
    ) -> Response:
        headers: dict[str, str] = {}
        data = None
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if key:
            headers["Idempotency-Key"] = key
        if version is not None:
            headers["X-Action-Version"] = str(version)
        request = urllib.request.Request(
            self.args.api_url.rstrip("/") + path,
            data=data,
            method=method,
            headers=headers,
        )
        try:
            with self.opener.open(request, timeout=timeout) as response:
                raw = response.read(self.args.max_response_bytes + 1)
                status = response.status
        except urllib.error.HTTPError as error:
            raw = error.read(self.args.max_response_bytes + 1)
            status = error.code
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            return Response(0, None, "", f"{type(error).__name__}: {error}")
        if len(raw) > self.args.max_response_bytes:
            return Response(status, None, "", "response body exceeded limit")
        text = raw.decode("utf-8", errors="replace")
        try:
            body = json.loads(text) if text else None
        except json.JSONDecodeError:
            body = None
        return Response(status, body, text)

    def wait_ready(self, timeout: float | None = None) -> tuple[Response, Response]:
        deadline = time.monotonic() + (timeout or self.args.ready_timeout)
        live = Response(0, None, "", "not requested")
        ready = Response(0, None, "", "not requested")
        while time.monotonic() < deadline:
            live = self.request("GET", "/health/live", timeout=2)
            ready = self.request("GET", "/health/ready", timeout=2)
            if live.status == 200 and ready.status == 200:
                return live, ready
            time.sleep(0.25)
        return live, ready

    def wait_dependency_unready(self, timeout: float = 10.0) -> tuple[Response, Response]:
        deadline = time.monotonic() + timeout
        live = Response(0, None, "", "not requested")
        ready = Response(0, None, "", "not requested")
        while time.monotonic() < deadline:
            live = self.request("GET", "/health/live", timeout=2)
            ready = self.request("GET", "/health/ready", timeout=2)
            if live.status == 200 and ready.status != 200:
                return live, ready
            time.sleep(0.25)
        return live, ready

    @staticmethod
    def error_code(response: Response) -> Any:
        return response.body.get("code") if isinstance(response.body, dict) else None

    @staticmethod
    def cli_envelope(body: Any, status: str) -> bool:
        return (
            isinstance(body, dict)
            and body.get("status") == status
            and isinstance(body.get("meta"), dict)
            and body["meta"].get("contractVersion") == "course-1"
        )

    @staticmethod
    def result(response: Response) -> dict[str, Any]:
        if not isinstance(response.body, dict) or not isinstance(
            response.body.get("result"), dict
        ):
            return {}
        return response.body["result"]

    def canary_count(self, marker: str) -> int | None:
        result = self.psql(
            f"SELECT count(*) FROM {self.target_schema}.{self.canary_table} "
            f"WHERE marker = {sql_literal(marker)};"
        )
        if result.exitCode != 0:
            return None
        try:
            return int(result.stdout.strip())
        except ValueError:
            return None

    def start_stack(self) -> None:
        readme_issues = readme_findings(self.repo)
        self.check(
            "submission-readme",
            "infrastructure",
            not readme_issues,
            "complete README section Решение",
            readme_issues,
        )
        hygiene_issues = repository_hygiene_findings(self.repo)
        self.check(
            "repository-hygiene",
            "infrastructure",
            not hygiene_issues,
            "no tracked generated, environment or IDE files",
            hygiene_issues,
        )
        self.write_override()
        config = self.compose("config", "--format", "json", timeout=60)
        services: set[str] = set()
        service_config: dict[str, Any] = {}
        try:
            parsed = json.loads(config.stdout)
            service_config = parsed.get("services", {})
            services = set(service_config)
        except json.JSONDecodeError:
            pass
        self.check(
            "compose-services",
            "infrastructure",
            config.exitCode == 0 and REQUIRED_SERVICES.issubset(services),
            sorted(REQUIRED_SERVICES),
            {
                "exitCode": config.exitCode,
                "services": sorted(services),
                "stderr": config.stderr,
            },
        )
        if config.exitCode != 0:
            return
        gateway_ports = published_ports(service_config.get("gateway"))
        other_published_ports = {
            name: sorted(published_ports(service))
            for name, service in service_config.items()
            if name != "gateway" and published_ports(service)
        }
        self.check(
            "gateway-is-only-public-entrypoint",
            "infrastructure",
            gateway_ports == {8080} and not other_published_ports,
            {"gateway": [8080], "otherServices": {}},
            {
                "gateway": sorted(gateway_ports),
                "otherServices": other_published_ports,
            },
        )

        if not self.args.skip_build:
            build = self.compose("build", "--pull", timeout=self.args.build_timeout)
            self.check("compose-build", "infrastructure", build.exitCode == 0, 0, build.exitCode)
            if build.exitCode != 0:
                return
        up = self.compose("up", "-d", "--force-recreate", timeout=600)
        self.check("compose-up", "infrastructure", up.exitCode == 0, 0, up.exitCode)
        if up.exitCode != 0:
            return
        live, ready = self.wait_ready()
        self.check("health-live", "infrastructure", live.status == 200, 200, asdict(live))
        self.check("health-ready", "infrastructure", ready.status == 200, 200, asdict(ready))
        unknown_surface = self.request("GET", "/not-part-of-course-contract")
        self.check(
            "gateway-route-whitelist",
            "infrastructure",
            unknown_surface.status == 404,
            404,
            asdict(unknown_surface),
        )

    def check_migrations_and_publication(self) -> None:
        images_before = {
            service: self.compose("images", "-q", service).stdout.strip()
            for service in ("gateway", "api")
        }

        migration, migration_body = self.cli(
            "migration", "apply", "/autocheck/input/migrations"
        )
        self.check(
            "migration-apply",
            "publication",
            migration.exitCode == 0 and self.cli_envelope(migration_body, "ok"),
            "exit 0 and one status=ok JSON",
            {
                "exitCode": migration.exitCode,
                "stdout": migration.stdout,
                "stderr": migration.stderr,
            },
        )
        migration_repeat, repeat_body = self.cli(
            "migration", "apply", "/autocheck/input/migrations"
        )
        self.check(
            "migration-idempotent-repeat",
            "publication",
            migration_repeat.exitCode == 0 and self.cli_envelope(repeat_body, "ok"),
            "safe repeat",
            {"exitCode": migration_repeat.exitCode, "body": repeat_body},
        )

        changed_root = self.temp / "changed-fixture"
        changed_migrations = changed_root / "migrations"
        changed_migrations.mkdir(parents=True)
        original = self.fixtures / "migrations" / self.fixture["migrationFile"]
        changed = changed_migrations / original.name
        changed.write_text(
            original.read_text(encoding="utf-8") + "\n-- checksum conflict\n",
            encoding="utf-8",
        )
        changed_result, changed_body = self.cli(
            "migration", "apply", "/autocheck/input/migrations", mount=changed_root
        )
        self.check(
            "migration-checksum-conflict",
            "publication",
            changed_result.exitCode != 0 and self.cli_envelope(changed_body, "error"),
            "non-zero status=error",
            {"exitCode": changed_result.exitCode, "body": changed_body},
        )

        validate, validate_body = self.cli(
            "action",
            "validate",
            f"/autocheck/input/manifests/{self.fixture['manifestV1']}",
        )
        self.check(
            "manifest-validate",
            "publication",
            validate.exitCode == 0 and self.cli_envelope(validate_body, "ok"),
            "valid manifest",
            {"exitCode": validate.exitCode, "body": validate_body},
        )
        validate_count = self.psql(
            "SELECT count(*) FROM autocheck.action_definitions "
            f"WHERE module = {sql_literal(self.module)} AND action = {sql_literal(self.action)};"
        )
        self.check(
            "manifest-validate-has-no-side-effect",
            "publication",
            validate_count.exitCode == 0 and validate_count.stdout.strip() == "0",
            0,
            {
                "exitCode": validate_count.exitCode,
                "count": validate_count.stdout.strip(),
            },
        )

        publish_v1, publish_v1_body = self.cli(
            "action",
            "publish",
            f"/autocheck/input/manifests/{self.fixture['manifestV1']}",
        )
        self.check(
            "manifest-publish-v1",
            "publication",
            publish_v1.exitCode == 0 and self.cli_envelope(publish_v1_body, "ok"),
            "published v1",
            {"exitCode": publish_v1.exitCode, "body": publish_v1_body},
        )

        publish_repeat, repeat_publish_body = self.cli(
            "action",
            "publish",
            f"/autocheck/input/manifests/{self.fixture['manifestV1']}",
        )
        self.check(
            "manifest-publish-idempotent",
            "publication",
            publish_repeat.exitCode == 0
            and self.cli_envelope(repeat_publish_body, "ok"),
            "safe identical publish",
            {"exitCode": publish_repeat.exitCode, "body": repeat_publish_body},
        )

        conflict_root = self.temp / "manifest-conflict"
        conflict_root.mkdir()
        manifest = json.loads(
            (self.fixtures / "manifests" / self.fixture["manifestV1"]).read_text(
                encoding="utf-8"
            )
        )
        manifest["timeout_ms"] = 2001
        (conflict_root / "conflict.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        conflict, conflict_body = self.cli(
            "action", "publish", "/autocheck/input/conflict.json", mount=conflict_root
        )
        self.check(
            "manifest-immutable-conflict",
            "publication",
            conflict.exitCode != 0 and self.cli_envelope(conflict_body, "error"),
            "changed published version rejected",
            {"exitCode": conflict.exitCode, "body": conflict_body},
        )

        images_after = {
            service: self.compose("images", "-q", service).stdout.strip()
            for service in ("gateway", "api")
        }
        self.check(
            "csharp-images-unchanged-after-publish",
            "publication",
            all(images_before.values()) and images_before == images_after,
            images_before,
            images_after,
        )

    def check_auth_and_contract(self) -> None:
        path = self.action_path
        payload = self.probe_payload("ok", f"auth-{uuid.uuid4().hex}")
        no_auth = self.request("POST", path, payload=payload, key="auth-no-token")
        self.check(
            "missing-jwt",
            "authorization",
            no_auth.status == 401 and self.error_code(no_auth) == "auth.invalid",
            {"http": 401, "code": "auth.invalid"},
            asdict(no_auth),
        )
        expired = self.request(
            "POST",
            path,
            payload=payload,
            key="auth-expired",
            token=self.tokens["expired"],
        )
        self.check(
            "expired-jwt",
            "authorization",
            expired.status == 401 and self.error_code(expired) == "auth.invalid",
            {"http": 401, "code": "auth.invalid"},
            asdict(expired),
        )
        malformed_claim = self.request(
            "POST",
            path,
            payload=payload,
            key="auth-malformed-claim",
            token=self.tokens["malformed_claim"],
        )
        self.check(
            "malformed-jwt-claim",
            "authorization",
            malformed_claim.status == 401
            and self.error_code(malformed_claim) == "auth.invalid",
            {"http": 401, "code": "auth.invalid"},
            asdict(malformed_claim),
        )
        denied_marker = f"denied-{uuid.uuid4().hex}"
        denied = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", denied_marker),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["denied"],
        )
        self.check(
            "http-policy-denied",
            "authorization",
            denied.status == 403
            and self.error_code(denied) == "access.denied"
            and self.canary_count(denied_marker) == 0,
            {"http": 403, "code": "access.denied", "canary": 0},
            {"response": asdict(denied), "canary": self.canary_count(denied_marker)},
        )

        missing_key = self.request(
            "POST", path, payload=payload, token=self.tokens["worker"]
        )
        self.check(
            "required-idempotency-key",
            "contract-boundary",
            missing_key.status == 400
            and self.error_code(missing_key) == "idempotency.required",
            {"http": 400, "code": "idempotency.required"},
            asdict(missing_key),
        )

        injected_marker = f"inject-{uuid.uuid4().hex}"
        injected = self.request(
            "POST",
            path,
            payload=self.probe_payload(
                "ok",
                injected_marker,
                target_schema="pg_catalog",
                target_function="pg_sleep",
                sql="select 1",
            ),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
        )
        self.check(
            "payload-cannot-select-target",
            "target-boundary",
            injected.status == 422
            and self.error_code(injected) == "payload.invalid"
            and self.canary_count(injected_marker) == 0,
            {"http": 422, "code": "payload.invalid", "canary": 0},
            {
                "response": asdict(injected),
                "canary": self.canary_count(injected_marker),
            },
        )

        unknown = self.request(
            "POST",
            "/api/not_registered/not_registered",
            payload={},
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
        )
        self.check(
            "unknown-action",
            "target-boundary",
            unknown.status == 404 and self.error_code(unknown) == "action.not_found",
            {"http": 404, "code": "action.not_found"},
            asdict(unknown),
        )

        direct_marker = f"direct-policy-{uuid.uuid4().hex}"
        context = {
            "principal": "denied-client",
            "consumer": "test",
            "scopes": [],
            "correlationId": str(uuid.uuid4()),
            "requestId": f"direct-{uuid.uuid4().hex}",
            "deadline": (
                dt.datetime.now(dt.UTC) + dt.timedelta(seconds=10)
            ).isoformat(),
        }
        direct_sql = (
            f"SELECT api.invoke({sql_literal(self.module)}, {sql_literal(self.action)}, 1, "
            f"{sql_literal(json.dumps(context, separators=(',', ':')))}::jsonb, "
            f"{sql_literal(json.dumps(self.probe_payload('ok', direct_marker), separators=(',', ':')))}::jsonb)::text;"
        )
        direct_result, direct_body = self.psql_json(direct_sql)
        self.check(
            "database-policy-denied",
            "authorization",
            direct_result.exitCode == 0
            and isinstance(direct_body, dict)
            and direct_body.get("status") == "error"
            and direct_body.get("code") == "access.denied"
            and self.canary_count(direct_marker) == 0,
            {"status": "error", "code": "access.denied", "canary": 0},
            {
                "exitCode": direct_result.exitCode,
                "body": direct_body,
                "stderr": direct_result.stderr,
                "canary": self.canary_count(direct_marker),
            },
        )

    def check_versions_and_rollback(self) -> None:
        path = self.action_path
        marker = f"ok-{uuid.uuid4().hex}"
        key = f"key-{uuid.uuid4().hex}"
        payload = self.probe_payload("ok", marker)
        first = self.request(
            "POST", path, payload=payload, key=key, token=self.tokens["worker"]
        )
        first_result = self.result(first)
        first_body = first.body if isinstance(first.body, dict) else {}
        first_meta = (
            first_body.get("meta", {})
            if isinstance(first_body.get("meta"), dict)
            else {}
        )
        correlation = first_meta.get("correlationId")
        self.check(
            "generic-action-default-v1",
            "publication",
            first.status == 200
            and first_body.get("status") == "ok"
            and first_body.get("outcome") == self.outcome
            and first_result.get("revision") == 1
            and first_result.get("principal") == "workflow-worker"
            and first_meta.get("actionVersion") == 1
            and isinstance(correlation, str)
            and self.canary_count(marker) == 1,
            "valid v1 envelope, trusted principal and one effect",
            {"response": asdict(first), "canary": self.canary_count(marker)},
        )
        try:
            uuid.UUID(str(correlation))
            correlation_valid = True
        except ValueError:
            correlation_valid = False
        self.check(
            "server-correlation-uuid",
            "runtime",
            correlation_valid,
            "UUID meta.correlationId",
            correlation,
        )

        replay = self.request(
            "POST", path, payload=payload, key=key, token=self.tokens["worker"]
        )
        self.check(
            "generic-action-idempotent-replay",
            "contract-boundary",
            replay.status == 200
            and self.result(replay) == first_result
            and self.canary_count(marker) == 1,
            "same result and one effect",
            {"response": asdict(replay), "canary": self.canary_count(marker)},
        )
        conflict = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", f"changed-{uuid.uuid4().hex}"),
            key=key,
            token=self.tokens["worker"],
        )
        self.check(
            "generic-action-idempotency-conflict",
            "contract-boundary",
            conflict.status == 409
            and self.error_code(conflict) == "idempotency.conflict",
            {"http": 409, "code": "idempotency.conflict"},
            asdict(conflict),
        )

        for mode, expected_code in (
            ("error", self.forced_error_code),
            ("unknown_outcome", "action.contract_violation"),
            ("invalid_result", "action.contract_violation"),
        ):
            rollback_marker = f"{mode}-{uuid.uuid4().hex}"
            response = self.request(
                "POST",
                path,
                payload=self.probe_payload(mode, rollback_marker),
                key=f"key-{uuid.uuid4().hex}",
                token=self.tokens["worker"],
                version=1,
                timeout=20.0,
            )
            count = self.canary_count(rollback_marker)
            code_ok = self.error_code(response) == expected_code
            if mode == "error":
                code_ok = self.error_code(response) in {expected_code, "internal.error"}
            self.check(
                f"rollback-{mode}",
                "contract-boundary",
                response.status >= 400 and code_ok and count == 0,
                {"errorCode": expected_code, "canary": 0},
                {"response": asdict(response), "canary": count},
            )

        publish_v2, publish_v2_body = self.cli(
            "action",
            "publish",
            f"/autocheck/input/manifests/{self.fixture['manifestV2']}",
        )
        self.check(
            "manifest-publish-v2",
            "publication",
            publish_v2.exitCode == 0 and self.cli_envelope(publish_v2_body, "ok"),
            "published v2",
            {"exitCode": publish_v2.exitCode, "body": publish_v2_body},
        )
        listed, listed_body = self.cli("action", "list")
        listed_items = (
            listed_body.get("result", {}).get("items", [])
            if self.cli_envelope(listed_body, "ok")
            and isinstance(listed_body.get("result"), dict)
            else []
        )
        listed_versions = {
            (item.get("module"), item.get("action"), item.get("version"))
            for item in listed_items
            if isinstance(item, dict)
        }
        self.check(
            "action-list-machine-readable",
            "publication",
            listed.exitCode == 0
            and {(self.module, self.action, 1), (self.module, self.action, 2)}.issubset(
                listed_versions
            ),
            "both published action versions in result.items",
            {"exitCode": listed.exitCode, "body": listed_body},
        )
        explicit_marker = f"v2-{uuid.uuid4().hex}"
        explicit_v2 = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", explicit_marker),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
            version=2,
        )
        self.check(
            "explicit-version-v2",
            "contract-boundary",
            explicit_v2.status == 200 and self.result(explicit_v2).get("revision") == 2,
            "revision 2",
            asdict(explicit_v2),
        )
        unknown_version = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", f"unknown-{uuid.uuid4().hex}"),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
            version=999,
        )
        self.check(
            "unknown-version",
            "target-boundary",
            unknown_version.status == 404
            and self.error_code(unknown_version) == "action.not_found",
            {"http": 404, "code": "action.not_found"},
            asdict(unknown_version),
        )

        activate, activate_body = self.cli(
            "action", "activate", self.route_key, "--version", "2"
        )
        activated_default = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", f"activated-{uuid.uuid4().hex}"),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
        )
        self.check(
            "atomic-default-activation",
            "contract-boundary",
            activate.exitCode == 0
            and self.cli_envelope(activate_body, "ok")
            and self.result(activated_default).get("revision") == 2,
            "v2 becomes default",
            {"cli": activate_body, "response": asdict(activated_default)},
        )

        disable, disable_body = self.cli(
            "action",
            "disable",
            self.route_key,
            "--version",
            "2",
            "--replacement-version",
            "1",
        )
        disabled_v2 = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", f"disabled-{uuid.uuid4().hex}"),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
            version=2,
        )
        replacement_default = self.request(
            "POST",
            path,
            payload=self.probe_payload("ok", f"replacement-{uuid.uuid4().hex}"),
            key=f"key-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
        )
        self.check(
            "disabled-version-and-replacement",
            "target-boundary",
            disable.exitCode == 0
            and self.cli_envelope(disable_body, "ok")
            and disabled_v2.status == 404
            and self.error_code(disabled_v2) == "action.not_found"
            and self.result(replacement_default).get("revision") == 1,
            "v2 disabled and v1 default",
            {
                "cli": disable_body,
                "disabled": asdict(disabled_v2),
                "default": asdict(replacement_default),
            },
        )

    def check_openapi_and_dispatch(self) -> None:
        default_document = self.request("GET", "/openapi/default.json")
        versioned_document = self.request(
            "GET", f"/openapi/actions/{self.module}/{self.action}/1.json"
        )
        default_paths = (
            default_document.body.get("paths", {})
            if isinstance(default_document.body, dict)
            else {}
        )
        versioned_paths = (
            versioned_document.body.get("paths", {})
            if isinstance(versioned_document.body, dict)
            else {}
        )
        versioned_text = json.dumps(versioned_document.body, separators=(",", ":"))
        self.check(
            "manifest-driven-openapi",
            "runtime",
            default_document.status == 200
            and self.action_path in default_paths
            and versioned_document.status == 200
            and list(versioned_paths) == [self.action_path]
            and "post" in versioned_paths[self.action_path]
            and self.mode_field in versioned_text
            and self.value_field in versioned_text
            and all(
                field in versioned_text for field in ("stored", "revision", "principal")
            ),
            "default route and one exact versioned operation",
            {
                "defaultStatus": default_document.status,
                "defaultPaths": sorted(default_paths),
                "versionedStatus": versioned_document.status,
                "versionedPaths": sorted(versioned_paths),
            },
        )

        evidence_sql = f"""
SELECT jsonb_build_object(
  'contract', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.contract_info t),
  'definitions', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.action_definitions t),
  'dispatches', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.action_dispatches t WHERE module = {sql_literal(self.module)} AND action = {sql_literal(self.action)})
)::text;
"""
        evidence_result, evidence = self.psql_json(evidence_sql)
        contract = evidence.get("contract", []) if isinstance(evidence, dict) else []
        definitions = (
            evidence.get("definitions", []) if isinstance(evidence, dict) else []
        )
        dispatches = (
            evidence.get("dispatches", []) if isinstance(evidence, dict) else []
        )
        hashes_valid = bool(dispatches) and all(
            re.fullmatch(r"[0-9a-f]{64}", str(item.get("payload_hash", "")))
            for item in dispatches
        )
        correlations_valid = bool(dispatches)
        for item in dispatches:
            try:
                uuid.UUID(str(item.get("correlation_id")))
            except ValueError:
                correlations_valid = False
                break
        has_versions = {
            (item.get("module"), item.get("action"), item.get("version"))
            for item in definitions
        }
        self.check(
            "autocheck-action-evidence",
            "runtime",
            evidence_result.exitCode == 0
            and len(contract) == 1
            and contract[0].get("contract_version") == "course-1"
            and {(self.module, self.action, 1), (self.module, self.action, 2)}.issubset(
                has_versions
            )
            and hashes_valid
            and correlations_valid,
            "contract row, both definitions, safe dispatch evidence",
            evidence,
        )

    def concurrent_payment_requests(
        self, request_id: str, payload: dict[str, Any], workers: int
    ) -> list[Response]:
        barrier = threading.Barrier(workers)

        def invoke(_: int) -> Response:
            try:
                barrier.wait(timeout=10)
            except threading.BrokenBarrierError:
                return Response(0, None, "", "barrier failed")
            return self.request(
                "POST",
                "/api/payment/request",
                payload=payload,
                key=request_id,
                token=self.tokens["candidate"],
            )

        with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
            return list(executor.map(invoke, range(workers)))

    def check_payment_foundation(self) -> None:
        invalid_cases = [
            {"operationKind": "PAYMENT_EXECUTION", "amount": "0", "currency": "RUB"},
            {
                "operationKind": "PAYMENT_EXECUTION",
                "amount": "1.234",
                "currency": "RUB",
            },
            {"operationKind": "PAYMENT_EXECUTION", "amount": 1, "currency": "RUB"},
            {"operationKind": "PAYMENT_EXECUTION", "amount": "1.00", "currency": "USD"},
            {
                "operationKind": "PAYMENT_EXECUTION",
                "amount": "1.00",
                "currency": "RUB",
                "status": "COMPLETED",
            },
        ]
        invalid_results = []
        for payload in invalid_cases:
            response = self.request(
                "POST",
                "/api/payment/request",
                payload=payload,
                key=f"invalid-{uuid.uuid4().hex}",
                token=self.tokens["candidate"],
            )
            invalid_results.append(asdict(response))
        self.check(
            "payment-request-validation",
            "postgresql",
            all(
                item["status"] == 422
                and isinstance(item["body"], dict)
                and item["body"].get("code") == "payload.invalid"
                for item in invalid_results
            ),
            "all invalid payloads return 422 payload.invalid",
            invalid_results,
        )

        denied = self.request(
            "POST",
            "/api/payment/request",
            payload={
                "operationKind": "PAYMENT_EXECUTION",
                "amount": "10.00",
                "currency": "RUB",
            },
            key=f"denied-{uuid.uuid4().hex}",
            token=self.tokens["denied"],
        )
        self.check(
            "payment-write-policy",
            "authorization",
            denied.status == 403 and self.error_code(denied) == "access.denied",
            {"http": 403, "code": "access.denied"},
            asdict(denied),
        )

        request_id = f"request-{uuid.uuid4().hex}"
        payload = {
            "operationKind": "PAYMENT_EXECUTION",
            "amount": "734.21",
            "currency": "RUB",
        }
        responses = self.concurrent_payment_requests(
            request_id, payload, self.args.concurrent_requests
        )
        results = [self.result(response) for response in responses]
        operation_ids = {
            result.get("operationId") for result in results if result.get("operationId")
        }
        statuses_ok = all(
            response.status == 200
            and isinstance(response.body, dict)
            and response.body.get("status") == "ok"
            and response.body.get("outcome") == "CREATED"
            for response in responses
        )
        result_shape_ok = all(
            result.get("requestId") == request_id
            and result.get("operationKind") == payload["operationKind"]
            and result.get("amount") == payload["amount"]
            and result.get("currency") == "RUB"
            and result.get("status") == "CREATED"
            for result in results
        )
        self.check(
            "concurrent-payment-request",
            "postgresql",
            statuses_ok and result_shape_ok and len(operation_ids) == 1,
            {"successfulResponses": self.args.concurrent_requests, "operationIds": 1},
            {
                "statuses": [response.status for response in responses],
                "operationIds": sorted(str(value) for value in operation_ids),
                "results": results,
            },
        )
        operation_id = next(iter(operation_ids), None)

        replay = self.request(
            "POST",
            "/api/payment/request",
            payload=payload,
            key=request_id,
            token=self.tokens["candidate"],
        )
        self.check(
            "payment-request-replay",
            "postgresql",
            replay.status == 200
            and self.result(replay).get("operationId") == operation_id,
            {"sameOperationId": operation_id},
            asdict(replay),
        )
        changed_payload = dict(payload)
        changed_payload["amount"] = "734.22"
        conflict = self.request(
            "POST",
            "/api/payment/request",
            payload=changed_payload,
            key=request_id,
            token=self.tokens["candidate"],
        )
        self.check(
            "payment-request-conflict",
            "postgresql",
            conflict.status == 409
            and self.error_code(conflict) == "idempotency.conflict",
            {"http": 409, "code": "idempotency.conflict"},
            asdict(conflict),
        )

        get_response = self.request(
            "POST",
            "/api/operation/get",
            payload={"operationId": operation_id},
            token=self.tokens["read"],
        )
        self.check(
            "operation-get",
            "postgresql",
            get_response.status == 200
            and isinstance(get_response.body, dict)
            and get_response.body.get("outcome") == "FOUND"
            and self.result(get_response).get("operationId") == operation_id,
            "FOUND same operation",
            asdict(get_response),
        )
        write_only_get = self.request(
            "POST",
            "/api/operation/get",
            payload={"operationId": operation_id},
            token=self.tokens["write"],
        )
        self.check(
            "operation-read-policy",
            "authorization",
            write_only_get.status == 403
            and self.error_code(write_only_get) == "access.denied",
            {"http": 403, "code": "access.denied"},
            asdict(write_only_get),
        )

        if not operation_id:
            self.check(
                "payment-authoritative-evidence",
                "postgresql",
                False,
                "one operation, one initial event and dispatch evidence",
                "operationId was not returned",
            )
            return

        evidence_sql = f"""
SELECT jsonb_build_object(
  'operations', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.operations t WHERE request_id = {sql_literal(request_id)}),
  'events', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.operation_events t WHERE operation_id = {sql_literal(str(operation_id))}::uuid),
  'dispatches', (SELECT coalesce(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM autocheck.action_dispatches t WHERE request_id = {sql_literal(request_id)})
)::text;
"""
        evidence_result, evidence = self.psql_json(evidence_sql)
        operations = (
            evidence.get("operations", []) if isinstance(evidence, dict) else []
        )
        events = evidence.get("events", []) if isinstance(evidence, dict) else []
        dispatches = (
            evidence.get("dispatches", []) if isinstance(evidence, dict) else []
        )
        operation_row = operations[0] if len(operations) == 1 else {}
        event_row = events[0] if len(events) == 1 else {}
        try:
            uuid.UUID(str(event_row.get("event_id")))
            event_id_valid = True
        except ValueError:
            event_id_valid = False
        event_hash_valid = bool(
            re.fullmatch(r"[0-9a-f]{64}", str(event_row.get("payload_hash", "")))
        )
        self.check(
            "payment-authoritative-evidence",
            "postgresql",
            evidence_result.exitCode == 0
            and len(operations) == 1
            and len(events) == 1
            and operation_row.get("operation_id") == operation_id
            and operation_row.get("request_id") == request_id
            and operation_row.get("operation_kind") == "PAYMENT_EXECUTION"
            and operation_row.get("status") == "CREATED"
            and operation_row.get("process_id") is None
            and str(operation_row.get("amount")) in {"734.21", "734.2100"}
            and operation_row.get("currency") == "RUB"
            and event_row.get("operation_id") == operation_id
            and event_row.get("event_type") == "OPERATION_CREATED"
            and event_id_valid
            and event_hash_valid
            and bool(dispatches),
            "one operation, one initial event and dispatch evidence",
            evidence,
        )

        operation_mutation = self.psql(
            "SET ROLE course_runtime; "
            "UPDATE autocheck.operations SET status = 'COMPLETED' "
            f"WHERE operation_id = {sql_literal(str(operation_id))}::uuid;"
        )
        self.check(
            "runtime-operation-update-denied",
            "postgresql",
            operation_mutation.exitCode != 0,
            "course_runtime cannot update operation projections",
            {
                "exitCode": operation_mutation.exitCode,
                "stderr": operation_mutation.stderr[-512:],
            },
        )
        event_mutation = self.psql(
            "SET ROLE course_runtime; "
            "DELETE FROM autocheck.operation_events "
            f"WHERE operation_id = {sql_literal(str(operation_id))}::uuid;"
        )
        self.check(
            "runtime-event-delete-denied",
            "postgresql",
            event_mutation.exitCode != 0,
            "course_runtime cannot delete operation events",
            {
                "exitCode": event_mutation.exitCode,
                "stderr": event_mutation.stderr[-512:],
            },
        )

        if operation_id:
            killed = self.compose("kill", "-s", "SIGKILL", "api")
            degraded_live, degraded_ready = self.wait_dependency_unready()
            removed = self.compose("rm", "-f", "api")
            started = self.compose("up", "-d", "--no-deps", "api")
            _, ready = self.wait_ready()
            persisted = self.request(
                "POST",
                "/api/operation/get",
                payload={"operationId": operation_id},
                token=self.tokens["read"],
            )
            self.check(
                "operation-persists-after-api-recreate",
                "postgresql",
                killed.exitCode == 0
                and degraded_live.status == 200
                and degraded_ready.status != 200
                and removed.exitCode == 0
                and started.exitCode == 0
                and ready.status == 200
                and persisted.status == 200
                and self.result(persisted).get("operationId") == operation_id,
                "same operation after API container recreation",
                {
                    "kill": killed.exitCode,
                    "liveWhileApiDown": degraded_live.status,
                    "readyWhileApiDown": degraded_ready.status,
                    "rm": removed.exitCode,
                    "up": started.exitCode,
                    "ready": ready.status,
                    "response": asdict(persisted),
                },
            )

            gateway_killed = self.compose("kill", "-s", "SIGKILL", "gateway")
            gateway_removed = self.compose("rm", "-f", "gateway")
            gateway_started = self.compose("up", "-d", "--no-deps", "gateway")
            _, gateway_ready = self.wait_ready()
            after_gateway_recreate = self.request(
                "POST",
                "/api/operation/get",
                payload={"operationId": operation_id},
                token=self.tokens["read"],
            )
            self.check(
                "operation-persists-after-gateway-recreate",
                "postgresql",
                gateway_killed.exitCode == 0
                and gateway_removed.exitCode == 0
                and gateway_started.exitCode == 0
                and gateway_ready.status == 200
                and after_gateway_recreate.status == 200
                and self.result(after_gateway_recreate).get("operationId")
                == operation_id,
                "same operation after gateway container recreation",
                {
                    "kill": gateway_killed.exitCode,
                    "rm": gateway_removed.exitCode,
                    "up": gateway_started.exitCode,
                    "ready": gateway_ready.status,
                    "response": asdict(after_gateway_recreate),
                },
            )

    def check_runtime_logs(self) -> None:
        logs = self.compose("logs", "--no-color", "gateway", "api", timeout=60)
        content = logs.stdout + "\n" + logs.stderr
        jwt_leak = any(token in content for token in self.tokens.values())
        signing_key_leak = self.secret in content
        self.check(
            "runtime-logs-do-not-expose-credentials",
            "runtime",
            logs.exitCode == 0 and not jwt_leak and not signing_key_leak,
            {"exitCode": 0, "jwtLeak": False, "signingKeyLeak": False},
            {
                "exitCode": logs.exitCode,
                "jwtLeak": jwt_leak,
                "signingKeyLeak": signing_key_leak,
            },
        )
        logs.stdout = "[runtime logs inspected and redacted]"
        logs.stderr = "[runtime logs inspected and redacted]" if logs.stderr else ""

    def check_dependency_outage(self) -> None:
        stopped = self.compose("stop", "postgres", timeout=90)
        degraded_ready = self.request("GET", "/health/ready", timeout=15.0)
        degraded_action = self.request(
            "POST",
            self.action_path,
            payload=self.probe_payload("ok", f"dependency-{uuid.uuid4().hex}"),
            key=f"dependency-{uuid.uuid4().hex}",
            token=self.tokens["worker"],
            version=1,
            timeout=15.0,
        )
        self.check(
            "postgres-dependency-unavailable",
            "runtime",
            stopped.exitCode == 0
            and degraded_ready.status != 200
            and degraded_action.status == 503
            and self.error_code(degraded_action) == "dependency.unavailable",
            {"ready": "non-200", "http": 503, "code": "dependency.unavailable"},
            {
                "stopExitCode": stopped.exitCode,
                "ready": asdict(degraded_ready),
                "action": asdict(degraded_action),
            },
        )
        started = self.compose("start", "postgres", timeout=90)
        _, recovered_ready = self.wait_ready(timeout=max(180.0, self.args.ready_timeout))
        self.check(
            "postgres-dependency-recovery",
            "runtime",
            started.exitCode == 0 and recovered_ready.status == 200,
            {"startExitCode": 0, "ready": 200},
            {"startExitCode": started.exitCode, "ready": recovered_ready.status},
            required=False,
        )

    def result_report(self) -> dict[str, Any]:
        infrastructure = [
            check
            for check in self.checks
            if check.group == "infrastructure" and check.required
        ]
        infrastructure_passed = bool(infrastructure) and all(
            check.passed for check in infrastructure
        )
        required_failures = [
            check.name for check in self.checks if check.required and not check.passed
        ]
        return {
            "manifestVersion": "course-1",
            "toolVersion": "1.3-public",
            "startedAt": self.started.isoformat(),
            "finishedAt": dt.datetime.now(dt.UTC).isoformat(),
            "status": "passed"
            if infrastructure_passed and not required_failures
            else "failed",
            "infrastructurePassed": infrastructure_passed,
            "failedChecks": required_failures,
            "checks": [asdict(check) for check in self.checks],
            "commands": [
                {
                    "command": item.command,
                    "exitCode": item.exitCode,
                    "stdout": item.stdout[-4096:],
                    "stderr": item.stderr[-4096:],
                }
                for item in self.commands
            ],
        }

    def run(self) -> dict[str, Any]:
        try:
            self.start_stack()
            infra_failed = any(
                check.required and not check.passed
                for check in self.checks
                if check.group == "infrastructure"
            )
            if not infra_failed:
                self.check_migrations_and_publication()
                self.check_auth_and_contract()
                self.check_versions_and_rollback()
                self.check_openapi_and_dispatch()
                self.check_payment_foundation()
                self.check_dependency_outage()
                self.check_runtime_logs()
        except (
            Exception
        ) as error:  # The report must survive checker defects and environment errors.
            self.check(
                "checker-unhandled-error",
                "infrastructure",
                False,
                "no unhandled exception",
                f"{type(error).__name__}: {error}",
            )
        return self.result_report()

    def close(self) -> None:
        if not self.args.keep_stack and self.override.exists():
            self.compose("down", "-v", "--remove-orphans", timeout=180)
        shutil.rmtree(self.temp, ignore_errors=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--fixtures", required=True, type=Path)
    parser.add_argument("--compose-file", type=Path)
    parser.add_argument("--compose-wrapper", type=Path)
    parser.add_argument("--api-url", default="http://127.0.0.1:8080")
    parser.add_argument("--project-name", default=f"moduledev-week1-{os.getpid()}")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--keep-stack", action="store_true")
    parser.add_argument("--build-timeout", type=int, default=900)
    parser.add_argument("--ready-timeout", type=float, default=90.0)
    parser.add_argument("--concurrent-requests", type=int, default=20)
    parser.add_argument("--max-response-bytes", type=int, default=1024 * 1024)
    args = parser.parse_args()
    if not 2 <= args.concurrent_requests <= 100:
        parser.error("--concurrent-requests must be from 2 to 100")
    if not args.repo.is_dir():
        parser.error("--repo must be an existing directory")
    if not args.fixtures.is_dir():
        parser.error("--fixtures must be an existing directory")
    if args.compose_wrapper and not args.compose_wrapper.is_file():
        parser.error("--compose-wrapper must be an existing file")
    return args


def main() -> int:
    args = parse_args()
    checker: Checker | None = None
    try:
        checker = Checker(args)
        report = checker.run()
        output = args.output.expanduser().resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        rendered = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        output.write_text(rendered, encoding="utf-8")
        print(rendered, end="")
        return 0 if report["status"] == "passed" else 1
    except (OSError, ValueError) as error:
        print(f"Cannot run public checker: {error}", file=sys.stderr)
        return 2
    finally:
        if checker is not None:
            checker.close()


if __name__ == "__main__":
    raise SystemExit(main())
