#!/usr/bin/env python3
"""End-to-end scenarios of the week-1/2 core against a running Compose stack.

    eval "$(python3 scripts/dev_env.py)"
    docker compose -p course-e2e up -d --build
    COMPOSE_PROJECT=course-e2e python3 scripts/e2e_core.py

The perimeter scenarios (e2e_perimeter.py) drive the week-3 flows; these pin the invariants of the
action runtime and the workflow core that the hidden checker varies: contract edges of the public
route, JWT and policy refusals, catalog versions, the limit rule boundary, gateway whitelist and
limits, privilege hygiene, readiness after a PostgreSQL restart and the CLI signal/start semantics.
Every scenario observes the system through HTTP, the CLI and the read-only ``autocheck`` views.
Exit code 0 means every scenario passed.
"""

from __future__ import annotations

import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from typing import Any

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import e2e_perimeter as e  # noqa: E402
from dev_env import issue_token  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIXTURES = os.path.join(REPO, "src", "Course.IntegrationTests", "fixtures")
REQUEST = {"operationKind": "PAYMENT_EXECUTION", "amount": "10.00", "currency": "RUB"}
MAX_REQUEST_BYTES = 1024 * 1024


def request(payload: Any, token: str = e.CLIENT, key: str | None = None, extra: dict[str, str] | None = None,
            body: bytes | None = None, version: str = "1") -> tuple[int, dict[str, Any] | None]:
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json", "X-Action-Version": version}
    if key is not None:
        headers["Idempotency-Key"] = key
    headers.update(extra or {})
    return e.http("/api/payment/request", e.compact(payload) if body is None else body, headers)


def code(body: dict[str, Any] | None) -> str | None:
    return (body or {}).get("code")


def raw(method: str, path: str, body: bytes | None = None, headers: dict[str, str] | None = None) -> tuple[int, bytes]:
    req = urllib.request.Request(e.GATEWAY + path, data=body, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def cli(*args: str, mount: str = FIXTURES) -> tuple[int, dict[str, Any] | None]:
    """One course CLI command in a one-off container with the integration fixtures mounted read-only."""
    result = e.compose("run", "--rm", "-T", "--no-deps", "-v", f"{mount}:/itest:ro", "cli", *args)
    try:
        return result.returncode, json.loads(result.stdout.strip() or "null")
    except json.JSONDecodeError:
        return result.returncode, None


def expect(status: int, body: dict[str, Any] | None, wanted_status: int, wanted_code: str, what: str) -> None:
    e.check(status == wanted_status and code(body) == wanted_code, f"{what}: expected {wanted_status} {wanted_code}, got {status} {code(body)}")


def scenario_request_contract_edges() -> None:
    """The public route refuses every malformed, unauthenticated or unauthorised shape by the matrix."""
    # Money: the schema pattern, not the database, decides what a valid amount looks like.
    for bad in ("0", "0.00", "-1.00", "1e3", "100.000", " 1.00", "1,00", "01.00", ""):
        status, body = request({**REQUEST, "amount": bad}, key=f"e2e-bad-{uuid.uuid4()}")
        expect(status, body, 422, "payload.invalid", f"amount {bad!r}")
    status, body = request({**REQUEST, "amount": "10"}, key=f"e2e-int-{uuid.uuid4()}")
    e.check(status == 200 and body is not None and body["status"] == "ok", f"an integer amount is valid: {status} {code(body)}")
    status, body = request({**REQUEST, "currency": "USD"}, key=f"e2e-cur-{uuid.uuid4()}")
    expect(status, body, 422, "payload.invalid", "currency USD")
    status, body = request({**REQUEST, "targetFunction": "pg_sleep"}, key=f"e2e-target-{uuid.uuid4()}")
    expect(status, body, 422, "payload.invalid", "payload selecting a target")

    # Idempotency of the public command: one effect per key, other body is a conflict.
    key = f"e2e-idem-{uuid.uuid4()}"
    s1, b1 = request(REQUEST, key=key)
    s2, b2 = request(REQUEST, key=key)
    e.check(s1 == 200 and s2 == 200 and b1 and b2 and b1["result"]["operationId"] == b2["result"]["operationId"],
            f"same key and body must return the same operation: {s1}/{s2}")
    s3, b3 = request({**REQUEST, "amount": "11.00"}, key=key)
    expect(s3, b3, 409, "idempotency.conflict", "same key with another body")
    operations = e.sql_rows(f"SELECT count(*) AS n FROM autocheck.operations WHERE request_id = '{key}'")[0]["n"]
    e.check(operations == 1, f"exactly one operation for the key, got {operations}")
    status, body = request(REQUEST)
    expect(status, body, 400, "idempotency.required", "missing Idempotency-Key")

    # Route and version.
    status, body = e.http("/api/nope/nothing", e.compact(REQUEST),
                          {"Authorization": f"Bearer {e.CLIENT}", "Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4())})
    expect(status, body, 404, "action.not_found", "unknown action")
    status, body = request(REQUEST, key=str(uuid.uuid4()), version="99")
    expect(status, body, 404, "action.not_found", "unknown version")
    status, body = request(REQUEST, key=str(uuid.uuid4()), version="abc")
    expect(status, body, 400, "request.invalid", "malformed version header")
    status, body = request(None, key=str(uuid.uuid4()), body=b"{not json")
    expect(status, body, 400, "request.invalid", "malformed JSON")
    status, body = request(["not", "an", "object"], key=str(uuid.uuid4()))
    expect(status, body, 400, "request.invalid", "non-object body")

    # Trust boundary: every token defect is 401, a missing scope is 403.
    status, body = e.http("/api/payment/request", e.compact(REQUEST), {"Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4())})
    expect(status, body, 401, "auth.invalid", "no token")
    expired = issue_token(e.KEY, "e2e-expired", "web", ["payment:write"], -60)
    status, body = request(REQUEST, token=expired, key=str(uuid.uuid4()))
    expect(status, body, 401, "auth.invalid", "expired token")
    foreign = issue_token("another-signing-key-that-is-long-enough-1234567890", "e2e", "web", ["payment:write"], 3600)
    status, body = request(REQUEST, token=foreign, key=str(uuid.uuid4()))
    expect(status, body, 401, "auth.invalid", "token signed by another key")
    encode = lambda value: base64.urlsafe_b64encode(value).rstrip(b"=").decode()  # noqa: E731
    claims = {"iss": "moduledev-course", "aud": "moduledev-api", "sub": "x", "consumer": "web", "scope": "payment:write",
              "iat": int(time.time()) - 5, "exp": int(time.time()) + 3600}
    none_token = encode(b'{"alg":"none","typ":"JWT"}') + "." + encode(json.dumps(claims).encode()) + "."
    status, body = request(REQUEST, token=none_token, key=str(uuid.uuid4()))
    expect(status, body, 401, "auth.invalid", "alg=none token")
    reader = issue_token(e.KEY, "e2e-reader", "web", ["payment:read"], 3600)
    status, body = request(REQUEST, token=reader, key=str(uuid.uuid4()))
    expect(status, body, 403, "access.denied", "token without payment:write")

    # Correlation metadata is server-side: a client header is ignored and never recorded.
    key = f"e2e-corr-{uuid.uuid4()}"
    status, body = request(REQUEST, key=key, extra={"X-Correlation-ID": "attacker-correlation"})
    meta = (body or {}).get("meta", {})
    dispatch = e.sql_rows(f"SELECT correlation_id::text AS c FROM autocheck.action_dispatches WHERE request_id = '{key}'")
    e.check(status == 200 and meta.get("correlationId") not in (None, "attacker-correlation") and dispatch
            and dispatch[0]["c"] == meta.get("correlationId"),
            f"the server correlation id must be used and recorded: meta={meta.get('correlationId')} dispatch={dispatch}")


def scenario_gateway_whitelist_and_limits() -> None:
    """The gateway accepts only actions, OpenAPI and health, and names an oversized body as a client error."""
    status, _ = raw("POST", "/admin", b"{}", {"Content-Type": "application/json"})
    e.check(status == 404, f"unknown path must be 404, got {status}")
    status, _ = raw("GET", "/api/payment/request")
    e.check(status == 401, f"an action route without a token is 401 before anything else, got {status}")
    status, body = raw("GET", "/api/payment/request", None, {"Authorization": f"Bearer {e.CLIENT}"})
    e.check(status == 400 and b"request.invalid" in body, f"GET on an action with a token is 400 request.invalid, got {status}")

    status, document = raw("GET", "/openapi/default.json")
    paths = json.loads(document).get("paths", {})
    e.check(status == 200 and "/api/payment/request" in paths and "/api/workflow/manual" in paths, f"openapi default: {status} {sorted(paths)[:5]}")
    e.check(raw("GET", "/openapi/actions/payment/request/1.json")[0] == 200, "the exact version document must exist")
    e.check(raw("GET", "/openapi/actions/payment/request/99.json")[0] == 404, "an unknown version document must be 404")

    oversized = e.compact({**REQUEST, "note": "x" * (MAX_REQUEST_BYTES + 64 * 1024)})
    status, body = send_until_answered(oversized)
    e.check(status == 413 and code(body) == "request.invalid" and (body or {}).get("retryable") is False,
            f"an oversized body is a non-retryable client error: {status} {code(body)} retryable={(body or {}).get('retryable')}")


def send_until_answered(body: bytes, attempts: int = 5) -> tuple[int, dict[str, Any] | None]:
    """Sends an oversized body until the gateway's answer is actually observed.

    The gateway answers 413 and closes the connection as soon as the declared size is over the
    limit - while the client may still be writing. A Linux client then reads the answer that is
    already in its receive buffer; a Windows client reports the aborted send (WinError 10053/10054)
    and loses the answer with it. That is a property of the client socket, not of the gateway, so
    the request is repeated until the answer is seen.
    """
    for _ in range(attempts):
        try:
            return request(None, key=str(uuid.uuid4()), body=body)
        except OSError:  # urllib.error.URLError is an OSError as well
            time.sleep(0.2)
    raise e.Failure("the answer to an oversized body was never observed")


def scenario_catalog_versions_and_policy() -> None:
    """A disabled version is unknown to the route, an enabled one answers, and policy is enforced per action."""
    for step in (("migration", "apply", "/itest/migrations"),
                 ("action", "publish", "/itest/manifests/itest-probe-v1.action.json"),
                 ("action", "publish", "/itest/manifests/itest-probe-v2.action.json"),
                 ("action", "activate", "itest.probe", "--version", "1")):
        rc, envelope = cli(*step)
        e.check(rc == 0, f"cli {' '.join(step)} failed: {envelope}")
    rc, envelope = cli("action", "disable", "itest.probe", "--version", "2", "--replacement-version", "1")
    e.check(rc == 0 and envelope and envelope["result"]["operation"] == "disabled", f"disable v2: {rc} {envelope}")
    try:
        executor = issue_token(e.KEY, "e2e-executor", "web", ["workflow:execute"], 3600)
        headers = {"Authorization": f"Bearer {executor}", "Content-Type": "application/json"}
        status, body = e.http("/api/itest/probe", e.compact({"marker": "e2e"}), {**headers, "Idempotency-Key": str(uuid.uuid4()), "X-Action-Version": "2"})
        expect(status, body, 404, "action.not_found", "disabled version")
        status, body = e.http("/api/itest/probe", e.compact({"marker": "e2e"}), {**headers, "Idempotency-Key": str(uuid.uuid4()), "X-Action-Version": "1"})
        e.check(status == 200 and body and body["meta"]["actionVersion"] == 1, f"the enabled version answers: {status} {code(body)}")
        status, body = e.http("/api/itest/probe", e.compact({"marker": "e2e"}),
                              {"Authorization": f"Bearer {e.CLIENT}", "Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4()), "X-Action-Version": "1"})
        expect(status, body, 403, "access.denied", "policy of the probe")
    finally:
        cli("action", "activate", "itest.probe", "--version", "2")
        cli("action", "activate", "itest.probe", "--version", "1")


def scenario_limit_rule_boundary() -> None:
    """course-limit-v1 is inclusive: 100000.00 is approved by the rule, one kopeck more waits for a person."""
    operation_id, submitted = e.create_and_submit("PAYMENT_APPROVAL", "100000.00")
    e.wait_status(operation_id, "COMPLETED")
    decisions = e.sql_rows(f"SELECT source, outcome, rule_version FROM autocheck.decisions WHERE process_id = '{submitted['processId']}'::uuid")
    e.check(decisions == [{"source": "LIMIT_RULE", "outcome": "APPROVED", "rule_version": "course-limit-v1"}], f"boundary decision: {decisions}")

    operation_id, submitted = e.create_and_submit("PAYMENT_APPROVAL", "100000.01")
    e.poll(f"SELECT state FROM autocheck.processes WHERE process_id = '{submitted['processId']}'::uuid",
           lambda rows: rows and rows[0]["state"] == "WAITING_MANUAL", timeout=30)
    decisions = e.sql_rows(f"SELECT count(*) AS n FROM autocheck.decisions WHERE process_id = '{submitted['processId']}'::uuid")
    e.check(decisions[0]["n"] == 0, f"no decision may exist before the manual one: {decisions}")


def scenario_privilege_hygiene() -> None:
    """PUBLIC reaches no application function or relation, and only the migrator inherits the owner."""
    schemas = "('api','payment','workflow','delivery','diagnostics','training','itest','course_meta','autocheck')"
    functions = e.sql_rows(
        "SELECT n.nspname || '.' || p.proname AS f FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace "
        f"WHERE n.nspname IN {schemas} AND has_function_privilege('public', p.oid, 'EXECUTE') AND p.prorettype <> 'trigger'::regtype")
    e.check(functions == [], f"functions executable by PUBLIC: {functions}")
    relations = e.sql_rows(
        "SELECT n.nspname || '.' || c.relname AS t FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        f"WHERE c.relkind IN ('r','v','p') AND n.nspname IN {schemas} AND has_table_privilege('public', c.oid, 'SELECT')")
    e.check(relations == [], f"relations readable by PUBLIC: {relations}")
    memberships = e.sql_rows(
        "SELECT r.rolname || '->' || g.rolname AS m FROM pg_auth_members am JOIN pg_roles r ON r.oid = am.member "
        "JOIN pg_roles g ON g.oid = am.roleid WHERE r.rolname LIKE 'course_%' "
        "OR r.rolname IN ('workflow_worker','outbox_dispatcher','inbox_reconciler','autocheck_reader') ORDER BY 1")
    e.check([m["m"] for m in memberships] == ["course_migrator->course_owner"], f"role memberships: {memberships}")


def scenario_readiness_survives_postgres_restart() -> None:
    """Readiness reports the outage and comes back on its own; api is not recreated."""
    before = e.compose("ps", "-q", "api").stdout.strip()
    e.check(e.compose("restart", "postgres").returncode == 0, "cannot restart postgres")
    unready_seen, ready_again = False, False
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        status, _ = raw("GET", "/health/ready")
        if status != 200:
            unready_seen = True
        elif unready_seen or time.monotonic() > deadline - 60:
            ready_again = True
            break
        time.sleep(0.5)
    after = e.compose("ps", "-q", "api").stdout.strip()
    e.check(ready_again and before == after, f"readiness must return without recreating api: ready={ready_again} same_container={before == after}")


def scenario_signal_and_start_semantics() -> None:
    """The CLI contract of week 2: signals the map does not declare, late and early signals, idempotent start."""
    e.check(cli("flow", "activate", "workflow-smoke", "--version", "1")[0] == 0, "cannot activate workflow-smoke v1")
    rc, started = cli("flow", "start", "workflow-smoke", "--business-key", f"e2e-signal-{uuid.uuid4().hex[:8]}", "--data", "/itest/data/smoke-process.json")
    e.check(rc == 0 and started and started["status"] == "ok", f"flow start: {rc} {started}")
    process_id = started["result"]["processId"]
    e.poll(f"SELECT state FROM autocheck.processes WHERE process_id = '{process_id}'::uuid", lambda rows: rows and rows[0]["state"] == "WAITING_SIGNAL", timeout=30)

    rc, envelope = cli("flow", "signal", process_id, "--type", "bogus.type", "--message-id", f"e2e-bogus-{uuid.uuid4().hex[:8]}", "--payload", "/itest/data/smoke-signal.json")
    stored = e.sql_rows(f"SELECT count(*) AS n FROM autocheck.signals WHERE process_id = '{process_id}'::uuid")[0]["n"]
    e.check(rc != 0 and envelope and envelope["status"] == "error" and stored == 0, f"an undeclared signal type is refused without a row: {rc} {code(envelope)} rows={stored}")
    rc, envelope = cli("flow", "signal", process_id, "--type", "training.completed", "--message-id", f"e2e-ok-{uuid.uuid4().hex[:8]}", "--payload", "/itest/data/smoke-signal.json")
    e.check(rc == 0 and envelope and envelope["result"]["status"] == "accepted", f"a declared signal is accepted: {rc} {code(envelope)}")
    e.poll(f"SELECT state FROM autocheck.processes WHERE process_id = '{process_id}'::uuid", lambda rows: rows and rows[0]["state"] == "COMPLETED", timeout=30)
    late = f"e2e-late-{uuid.uuid4().hex[:8]}"
    rc, envelope = cli("flow", "signal", process_id, "--type", "training.completed", "--message-id", late, "--payload", "/itest/data/smoke-signal.json")
    state = e.sql_rows(f"SELECT p.state, s.status FROM autocheck.processes p JOIN autocheck.signals s ON s.process_id = p.process_id WHERE s.message_id = '{late}'")
    e.check(rc == 0 and envelope and envelope["result"]["status"] == "accepted" and state == [{"state": "COMPLETED", "status": "ACCEPTED"}],
            f"a signal for a completed process is stored but does not move it: {rc} {state}")
    completed = e.sql_rows(f"SELECT count(*) AS n FROM autocheck.workflow_events WHERE process_id = '{process_id}'::uuid AND event_type = 'ProcessCompleted'")[0]["n"]
    e.check(completed == 1, f"completion is recorded exactly once, got {completed}")

    # Idempotent start: same data returns the same process, other data is a conflict.
    key = f"e2e-start-{uuid.uuid4().hex[:8]}"
    rc1, first = cli("flow", "start", "workflow-smoke", "--business-key", key, "--data", "/itest/data/smoke-process.json")
    rc2, second = cli("flow", "start", "workflow-smoke", "--business-key", key, "--data", "/itest/data/smoke-process.json")
    e.check(rc1 == 0 and rc2 == 0 and first and second and first["result"]["processId"] == second["result"]["processId"], "same business key and data must return the same process")
    rc3, third = cli("flow", "start", "workflow-smoke", "--business-key", key, "--data", "/itest/data/smoke-signal.json")
    e.check(rc3 != 0 and third and third["status"] == "error" and "conflict" in (code(third) or ""), f"same business key with other data is a conflict: {rc3} {code(third)}")

    # A published but never activated flow cannot be started.
    e.check(cli("migration", "apply", "/itest/migrations")[0] == 0 and cli("action", "publish", "/itest/manifests/itest-slow-v1.action.json")[0] == 0,
            "the slow fixture must publish before its inactive map")
    rc, envelope = cli("flow", "publish", "/itest/maps/itest-inactive-v1.flow.json")
    e.check(rc == 0 and envelope and envelope["status"] == "ok", f"publish without activation: {rc} {code(envelope)}")
    rc, envelope = cli("flow", "start", "itest-inactive", "--business-key", f"e2e-{uuid.uuid4().hex[:6]}")
    processes = e.sql_rows("SELECT count(*) AS n FROM autocheck.processes WHERE flow_name = 'itest-inactive'")[0]["n"]
    e.check(rc != 0 and envelope and envelope["status"] == "error" and envelope.get("meta", {}).get("contractVersion") == "course-1" and processes == 0,
            f"start without an active version is a controlled error without a process: {rc} {code(envelope)} processes={processes}")

    # An early signal is kept ACCEPTED and applied on entry into the wait step.
    e.check(e.compose("stop", "worker-a", "worker-b").returncode == 0, "cannot stop the workers")
    try:
        rc, started = cli("flow", "start", "workflow-smoke", "--business-key", f"e2e-early-{uuid.uuid4().hex[:8]}", "--data", "/itest/data/smoke-process.json")
        early_process = started["result"]["processId"]
        early = f"e2e-early-msg-{uuid.uuid4().hex[:8]}"
        rc, envelope = cli("flow", "signal", early_process, "--type", "training.completed", "--message-id", early, "--payload", "/itest/data/smoke-signal.json")
        before = e.sql_rows(f"SELECT p.state, s.status FROM autocheck.processes p JOIN autocheck.signals s ON s.process_id = p.process_id WHERE s.message_id = '{early}'")
        e.check(rc == 0 and before == [{"state": "RUNNING", "status": "ACCEPTED"}], f"an early signal is accepted and kept: {rc} {before}")
    finally:
        e.check(e.compose("start", "worker-a", "worker-b").returncode == 0, "cannot start the workers")
    final = e.poll(f"SELECT p.state, s.status FROM autocheck.processes p JOIN autocheck.signals s ON s.process_id = p.process_id WHERE s.message_id = '{early}'",
                   lambda rows: rows and rows[0]["state"] in ("COMPLETED", "FAILED"), timeout=40)
    e.check(final == [{"state": "COMPLETED", "status": "APPLIED"}], f"the early signal is applied on entry into the wait step: {final}")


SCENARIOS = [scenario_request_contract_edges, scenario_gateway_whitelist_and_limits, scenario_catalog_versions_and_policy,
             scenario_limit_rule_boundary, scenario_privilege_hygiene, scenario_readiness_survives_postgres_restart,
             scenario_signal_and_start_semantics]


def main() -> int:
    failed = 0
    for scenario in SCENARIOS:
        started = time.monotonic()
        try:
            scenario()
            print(f"PASS {scenario.__name__} ({time.monotonic() - started:.1f}s)")
        except e.Skipped as reason:
            print(f"SKIP {scenario.__name__}: {reason}")
        except Exception as error:  # noqa: BLE001 - report every scenario
            failed += 1
            print(f"FAIL {scenario.__name__}: {error}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
