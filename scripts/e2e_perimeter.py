#!/usr/bin/env python3
"""End-to-end scenarios of the weeks 3-4 perimeter against a running Compose stack.

    eval "$(python3 scripts/dev_env.py)"
    docker compose -p course-e2e up -d --build
    COMPOSE_PROJECT=course-e2e python3 scripts/e2e_perimeter.py

Behaviour is observed through HTTP actions, provider audit, internal health/OpenMetrics addresses
and the read-only ``autocheck`` views - the same seams the course checker uses. Two scenarios
deliberately go below them: the storage invariants attack physical tables inside rolled-back
transactions to prove the triggers hold, and the lock-order scenario holds row locks of a process
step to reproduce a race deterministically.

Scenarios that need a queue to stand still stop BOTH tenants of it, and every failpoint scenario
waits for the published acknowledgement instead of sleeping. Exit code 0 means every scenario
passed.
"""

from __future__ import annotations

import base64
import concurrent.futures
import hashlib
import hmac
import json
import os
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from typing import Any, Callable

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dev_env import issue_token  # noqa: E402

PROJECT = os.environ.get("COMPOSE_PROJECT", "moduledev-week-4")
GATEWAY = f"http://127.0.0.1:{os.environ.get('COURSE_GATEWAY_PORT', '8080')}"
KEY = os.environ["COURSE_JWT_SIGNING_KEY"]
HMAC_SECRET = os.environ["PROVIDER_HMAC_SECRET"]
CAPABILITY = os.environ["PROVIDER_CALLBACK_CAPABILITY"]
AUDIT_TOKEN = os.environ["PROVIDER_AUDIT_TOKEN"]
CALLBACK = f"http://receipt-adapter:8082/callbacks/provider-v02/{CAPABILITY}"

CLIENT = issue_token(KEY, "e2e-client", "web", ["payment:write", "payment:read", "workflow:read"], 3600)
REVIEWER = issue_token(KEY, "e2e-reviewer", "backoffice", ["workflow:manual", "payment:read"], 3600)
RECEIPT = issue_token(KEY, "receipt-provider", "integration", ["receipt:write"], 3600)
OTHER_REVIEWER = issue_token(KEY, "e2e-reviewer-2", "backoffice", ["workflow:manual"], 3600)
OBSERVER = issue_token(KEY, "e2e-observer", "ops", ["diagnostics:read"], 3600)

# Week 4 runs two of each queue tenant. A scenario that needs the queue to stand still has to stop
# BOTH instances: leaving the second one running means the delivery is simply picked up by it, and
# the scenario silently proves nothing.
DISPATCHERS = ("outbox-dispatcher", "outbox-dispatcher-b")
RECONCILERS = ("inbox-reconciler", "inbox-reconciler-b")
PERIMETER = (*DISPATCHERS, "receipt-adapter", *RECONCILERS)

# Internal observability addresses. Nothing here is published to the host.
OBSERVABILITY = {
    "api": "http://api:8080",
    "worker-a": "http://worker-a:8080",
    "worker-b": "http://worker-b:8080",
    "outbox-dispatcher": "http://outbox-dispatcher:8080",
    "outbox-dispatcher-b": "http://outbox-dispatcher-b:8080",
    "receipt-adapter": "http://receipt-adapter:8082",
    "inbox-reconciler": "http://inbox-reconciler:8080",
    "inbox-reconciler-b": "http://inbox-reconciler-b:8080",
}


class Failure(AssertionError):
    pass


class Skipped(Exception):
    """The scenario needs a stand configuration the current run does not have."""


def check(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)


def compact(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")


def compose(*args: str, timeout: float = 120) -> subprocess.CompletedProcess[str]:
    env = {**os.environ, "MSYS2_ARG_CONV_EXCL": "*"}
    return subprocess.run(
        ["docker", "compose", "-p", PROJECT, *args],
        capture_output=True, text=True, timeout=timeout, env=env, check=False,
    )


def stop(*services: str) -> None:
    check(compose("stop", *services).returncode == 0, f"cannot stop {', '.join(services)}")


def start(*services: str, recreate: bool = False, environment: dict[str, str] | None = None) -> None:
    arguments = ["up", "-d", "--no-build", "--no-deps"]
    if recreate:
        arguments.append("--force-recreate")
    if environment is None:
        check(compose(*arguments, *services).returncode == 0, f"cannot start {', '.join(services)}")
        return
    # A failpoint is read by the process at start-up, so it has to be part of the environment of
    # the launch itself rather than set afterwards.
    merged = {**os.environ, "MSYS2_ARG_CONV_EXCL": "*", **environment}
    result = subprocess.run(
        ["docker", "compose", "-p", PROJECT, *arguments, *services],
        capture_output=True, text=True, timeout=180, env=merged, check=False,
    )
    check(result.returncode == 0, f"cannot start {', '.join(services)}: {result.stderr[-300:]}")


def wait_gateway(timeout: float = 120) -> None:
    """Poll gateway readiness. A recreated API answers a moment later, not instantly."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(GATEWAY + "/health/ready", timeout=2) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, urllib.error.HTTPError, OSError):
            pass
        time.sleep(0.3)
    raise Failure("gateway readiness did not become 200")


def wait_for_log(service: str, needle: str, timeout: float = 30) -> None:
    deadline = time.monotonic() + timeout
    while needle not in compose("logs", "--no-color", service).stdout:
        check(time.monotonic() < deadline, f"{service} did not log {needle}")
        time.sleep(0.2)


def sql_rows(query: str) -> list[dict[str, Any]]:
    wrapped = f"SELECT COALESCE(jsonb_agg(to_jsonb(q)), '[]'::jsonb)::text FROM ({query}) AS q"
    result = compose("exec", "-T", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1",
                     "-U", "postgres", "-d", "course", "-At", "-c", wrapped)
    check(result.returncode == 0, f"psql failed: {result.stderr[-300:]}")
    return json.loads(result.stdout.strip())


def sql_text(value: str) -> str:
    return f"convert_from(decode('{value.encode('utf-8').hex()}', 'hex'), 'UTF8')"


def poll(query: str, predicate: Callable[[list[dict[str, Any]]], bool], timeout: float = 60) -> list[dict[str, Any]]:
    deadline = time.monotonic() + timeout
    rows: list[dict[str, Any]] = []
    while time.monotonic() < deadline:
        rows = sql_rows(query)
        if predicate(rows):
            return rows
        time.sleep(0.2)
    raise Failure(f"timed out waiting for: {query[:120]} last={rows}")


def http(path: str, body: bytes, headers: dict[str, str]) -> tuple[int, dict[str, Any] | None]:
    request = urllib.request.Request(GATEWAY + path, data=body, method="POST", headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, None


def action(module: str, name: str, payload: dict[str, Any], token: str, key: str | None = None,
           extra: dict[str, str] | None = None, body: bytes | None = None) -> tuple[int, dict[str, Any] | None]:
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json", "X-Action-Version": "1"}
    if key is not None:
        headers["Idempotency-Key"] = key
    headers.update(extra or {})
    return http(f"/api/{module}/{name}", compact(payload) if body is None else body, headers)


def internal(url: str, body: bytes | None = None, headers: dict[str, str] | None = None, method: str = "POST") -> tuple[int, bytes]:
    script = (
        "import base64,json,sys,urllib.error,urllib.request\n"
        "data=base64.b64decode(sys.argv[3]) if sys.argv[3] else None\n"
        "req=urllib.request.Request(sys.argv[2],data=data,method=sys.argv[1],headers=json.loads(sys.argv[4]))\n"
        "try:\n r=urllib.request.urlopen(req,timeout=15);s=r.status;o=r.read()\n"
        "except urllib.error.HTTPError as e:\n s=e.code;o=e.read()\n"
        "print(json.dumps({'status':s,'data':base64.b64encode(o).decode()}))\n"
    )
    encoded = base64.b64encode(body).decode("ascii") if body is not None else ""
    result = compose("exec", "-T", "receipt-adapter", "python", "-c", script, method, url, encoded,
                     json.dumps(headers or {}))
    check(result.returncode == 0, f"internal probe failed: {result.stderr[-300:]}")
    value = json.loads(result.stdout.strip())
    return int(value["status"]), base64.b64decode(value["data"])


def observe(service: str, path: str, source: str = "receipt-adapter") -> tuple[int, bytes, str]:
    """GET an internal observability address from inside the Compose network.

    The vantage point is a Python service of the perimeter, because the addresses are reachable
    only there - no host port publishes them. The default is the adapter: it is the one perimeter
    service no scenario stops, and a probe launched from a stopped container fails for reasons
    that have nothing to do with the service being probed.
    """
    script = (
        "import base64,json,sys,urllib.error,urllib.request\n"
        "req=urllib.request.Request(sys.argv[1],method='GET')\n"
        "try:\n r=urllib.request.urlopen(req,timeout=10);s=r.status;o=r.read();t=r.headers.get('Content-Type','')\n"
        "except urllib.error.HTTPError as e:\n s=e.code;o=e.read();t=e.headers.get('Content-Type','')\n"
        "print(json.dumps({'status':s,'data':base64.b64encode(o).decode(),'type':t}))\n"
    )
    result = compose("exec", "-T", source, "python", "-c", script, OBSERVABILITY[service] + path)
    check(result.returncode == 0, f"observability probe failed for {service}: {result.stderr[-300:]}")
    value = json.loads(result.stdout.strip())
    return int(value["status"]), base64.b64decode(value["data"]), value["type"]


def openmetrics(document: bytes) -> dict[str, float]:
    """Parse the scalar samples of a document and reject anything the contract forbids."""
    text = document.decode("utf-8")
    check("\r" not in text and text.endswith("# EOF\n"), "document must be LF-only and end with # EOF")
    samples: dict[str, float] = {}
    for line in text.splitlines():
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        check(len(parts) >= 2, f"invalid OpenMetrics sample: {line}")
        check("{" not in parts[0], f"published series must carry no labels: {line}")
        samples[parts[0]] = float(parts[1])
    return samples


def audit(external_id: str) -> tuple[int, dict[str, Any] | None]:
    status, raw = internal(
        f"http://provider-simulator:8081/internal/audit/{urllib.parse.quote(external_id, safe='')}",
        headers={"X-Audit-Token": AUDIT_TOKEN}, method="GET")
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, None


def create_and_submit(kind: str, amount: str) -> tuple[str, dict[str, Any]]:
    status, created = action("payment", "request", {"operationKind": kind, "amount": amount, "currency": "RUB"},
                             CLIENT, f"e2e-request-{uuid.uuid4()}")
    check(status == 200 and created and created["status"] == "ok", f"payment.request failed: {status} {created}")
    operation_id = created["result"]["operationId"]
    status, submitted = action("payment", "submit", {"operationId": operation_id}, CLIENT, f"e2e-submit-{uuid.uuid4()}")
    check(status == 200 and submitted and submitted["outcome"] == "SUBMITTED", f"payment.submit failed: {status} {submitted}")
    return operation_id, submitted["result"]


def wait_status(operation_id: str, status: str) -> None:
    poll(f"SELECT status FROM autocheck.operations WHERE operation_id = '{operation_id}'::uuid",
         lambda rows: len(rows) == 1 and rows[0]["status"] == status)


def external_request(operation_id: str) -> str:
    rows = poll(f"SELECT external_request_id FROM autocheck.external_requests WHERE operation_id = '{operation_id}'::uuid",
                lambda rows: len(rows) == 1)
    return rows[0]["external_request_id"]


def signed_receipt(external_id: str, message_id: str, outcome: str = "COMPLETED", version: int = 1) -> tuple[dict[str, Any], dict[str, str]]:
    receipt = {"externalRequestId": external_id, "messageId": message_id, "occurredAt": "2026-09-04T12:00:00Z",
               "outcome": outcome, "providerPaymentId": message_id, "version": version}
    digest = hmac.new(HMAC_SECRET.encode("utf-8"), compact(receipt), hashlib.sha256).hexdigest()
    return receipt, {"X-Provider-Signature": "v1=" + digest}


def scenario_provider_path() -> None:
    stop(*DISPATCHERS)
    try:
        operation_id, submitted = create_and_submit("PAYMENT_EXECUTION", "1000.00")
        check(submitted["flowName"] == "payment-processing", "server-side binding chose a wrong flow")
        external_id = external_request(operation_id)
        outbox = sql_rows(f"SELECT state FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
        check(len(outbox) == 1 and outbox[0]["state"] in ("PENDING", "RETRY_WAIT"), f"outbox not durable: {outbox}")
        check(audit(external_id)[0] == 404, "provider was called before dispatch")

        receipt, signature = signed_receipt(external_id, f"e2e-{secrets.token_hex(4)}")
        status, body = action("receipt", "accept", receipt, RECEIPT, receipt["messageId"])
        check(status == 403 and body and body["code"] == "receipt.signature_required", f"missing signature: {status} {body}")
        status, body = action("receipt", "accept", receipt, RECEIPT, receipt["messageId"],
                              {"X-Provider-Signature": "v1=" + "0" * 64})
        check(status == 401 and body and body["code"] == "signature.invalid", f"invalid signature: {status} {body}")
        wrong_version, wrong_signature = signed_receipt(external_id, receipt["messageId"], version=2)
        status, body = action("receipt", "accept", wrong_version, RECEIPT, receipt["messageId"], wrong_signature)
        check(status == 422 and body and body["code"] == "payload.invalid", f"wrong version: {status} {body}")
        unknown, unknown_signature = signed_receipt("ext-unknown-" + secrets.token_hex(4), receipt["messageId"])
        status, body = action("receipt", "accept", unknown, RECEIPT, receipt["messageId"], unknown_signature)
        check(status == 422 and body and body["code"] == "receipt.external_request_not_found", f"unknown external: {status} {body}")
        spoofed = compact(receipt).replace(b'"COMPLETED"', b'"REJECTED"')
        status, body = action("receipt", "accept", {}, RECEIPT, receipt["messageId"], signature, body=spoofed)
        check(status == 401, f"re-used signature over changed bytes must fail: {status} {body}")
        check(sql_rows(f"SELECT 1 FROM autocheck.inbox WHERE message_id = {sql_text(receipt['messageId'])}") == [],
              "invalid receipts mutated the inbox")
    finally:
        start(*DISPATCHERS)

    wait_status(operation_id, "COMPLETED")
    status, payment = audit(external_id)
    check(status == 200 and payment and payment["paymentCount"] == 1 and payment["idempotencyKey"] == external_id,
          f"provider audit: {status} {payment}")
    evidence = sql_rows(
        "SELECT r.message_id, r.signature_valid, r.body_hash = i.body_hash AS same_hash, i.state "
        f"FROM autocheck.receipts r JOIN autocheck.inbox i USING (message_id) WHERE r.external_request_id = {sql_text(external_id)}")
    check(len(evidence) == 1 and evidence[0]["message_id"] == payment["providerPaymentId"]
          and evidence[0]["signature_valid"] and evidence[0]["same_hash"] and evidence[0]["state"] == "APPLIED",
          f"receipt evidence: {evidence}")
    outbox = sql_rows(f"SELECT state FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
    check(outbox[0]["state"] == "CONFIRMED", f"outbox not confirmed: {outbox}")

    status, repeated = action("payment", "submit", {"operationId": operation_id}, CLIENT, f"e2e-resubmit-{uuid.uuid4()}")
    check(status == 200 and repeated and repeated["result"] == submitted, "repeat submit changed the subject result")
    status, events = action("operation", "events", {"operationId": operation_id}, CLIENT)
    types = [event["eventType"] for event in (events or {}).get("result", {}).get("events", [])]
    check(status == 200 and types[0] == "OPERATION_CREATED" and types[-1] == "OPERATION_COMPLETED", f"events: {types}")


def scenario_adapter_and_recreate() -> None:
    stop(*DISPATCHERS, *RECONCILERS)
    try:
        adapter_and_recreate_steps()
    finally:
        # A failed step must not leave the perimeter stopped for the scenarios that follow.
        compose("up", "-d", "--no-build", "--no-deps", *PERIMETER)


def adapter_and_recreate_steps() -> None:
    operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1010.00")
    external_id = external_request(operation_id)
    legacy = {"providerPaymentId": f"e2e-provider-{secrets.token_hex(5)}", "operationId": external_id,
              "result": "REJECTED", "message": "e2e rejection", "occurredAt": "2026-09-04T12:00:00.123456789Z"}
    raw = compact(legacy)
    check(internal(CALLBACK[:-len(CAPABILITY)] + "wrong", raw, {"Content-Type": "application/json"})[0] == 404, "wrong capability")
    check(internal(CALLBACK, b"{broken", {"Content-Type": "application/json"})[0] == 400, "malformed JSON")
    check(internal(CALLBACK, compact({**legacy, "extra": 1}), {"Content-Type": "application/json"})[0] == 400, "unknown field")
    first = internal(CALLBACK, raw, {"Content-Type": "application/json"})
    duplicate = internal(CALLBACK, raw, {"Content-Type": "application/json"})
    first_json, duplicate_json = json.loads(first[1]), json.loads(duplicate[1])
    check(first[0] == 200 and duplicate[0] == 200
          and all(first_json[key] == duplicate_json[key] for key in ("status", "outcome", "result")),
          f"duplicate callback: {first} {duplicate}")
    conflict = internal(CALLBACK, compact({**legacy, "result": "COMPLETED"}), {"Content-Type": "application/json"})
    check(conflict[0] == 409 and json.loads(conflict[1])["code"] == "idempotency.conflict", f"conflict: {conflict}")

    # Same Idempotency-Key, semantically equal JSON, different exact bytes with a valid signature:
    # the command body is not the same, so this is a conflict, not a replay.
    receipt_for_key = {"externalRequestId": external_id, "messageId": legacy["providerPaymentId"],
                       "occurredAt": legacy["occurredAt"], "outcome": "REJECTED",
                       "providerPaymentId": legacy["providerPaymentId"], "version": 1}
    spaced = json.dumps(receipt_for_key, sort_keys=True, indent=1).encode("utf-8")
    spaced_signature = "v1=" + hmac.new(HMAC_SECRET.encode("utf-8"), spaced, hashlib.sha256).hexdigest()
    status, body = action("receipt", "accept", {}, RECEIPT, legacy["providerPaymentId"],
                          {"X-Provider-Signature": spaced_signature}, body=spaced)
    check(status == 409 and body and body["code"] == "idempotency.conflict",
          f"re-serialized signed body under the same key must conflict: {status} {body}")

    receipt = {"externalRequestId": external_id, "messageId": legacy["providerPaymentId"], "occurredAt": legacy["occurredAt"],
               "outcome": "REJECTED", "providerPaymentId": legacy["providerPaymentId"], "version": 1}
    rows = poll(f"SELECT state, body_hash FROM autocheck.inbox WHERE message_id = {sql_text(legacy['providerPaymentId'])}",
                lambda rows: len(rows) == 1)
    check(rows[0]["state"] == "RECEIVED" and rows[0]["body_hash"] == hashlib.sha256(compact(receipt)).hexdigest(),
          f"inbox must wait for the reconciler with exact body hash: {rows}")

    start(*PERIMETER, recreate=True)
    wait_status(operation_id, "REJECTED")
    poll(f"SELECT state FROM autocheck.inbox WHERE message_id = {sql_text(legacy['providerPaymentId'])}",
         lambda rows: rows[0]["state"] == "APPLIED")
    # Barrier instead of a sleep: a sentinel payment created after the restart is delivered, so the
    # dispatcher has demonstrably worked the queue; the confirmed delivery must still be untouched.
    sentinel, _ = create_and_submit("PAYMENT_EXECUTION", "1011.00")
    wait_status(sentinel, "COMPLETED")
    check(audit(external_request(sentinel))[0] == 200, "dispatcher did not resume after recreate")
    check(audit(external_id)[0] == 404, "confirmed outbox must not be delivered after the dispatcher restarts")


def scenario_review() -> None:
    auto_operation, auto = create_and_submit("PAYMENT_APPROVAL", "100000.00")
    check(auto["flowName"] == "payment-review", "server-side binding chose a wrong flow")
    wait_status(auto_operation, "COMPLETED")
    decisions = sql_rows(f"SELECT source, outcome, rule_version FROM autocheck.decisions WHERE process_id = '{auto['processId']}'::uuid")
    check(decisions == [{"source": "LIMIT_RULE", "outcome": "APPROVED", "rule_version": "course-limit-v1"}], f"limit decision: {decisions}")

    for decision, final in (("APPROVED", "COMPLETED"), ("REJECTED", "REJECTED")):
        operation_id, submitted = create_and_submit("PAYMENT_APPROVAL", "100000.01")
        process_id = submitted["processId"]
        poll(f"SELECT state FROM autocheck.processes WHERE process_id = '{process_id}'::uuid",
             lambda rows: rows[0]["state"] == "WAITING_MANUAL")
        step_id = sql_rows(f"SELECT step_instance_id FROM autocheck.steps WHERE process_id = '{process_id}'::uuid "
                           "AND step_type = 'MANUAL' AND state = 'WAITING'")[0]["step_instance_id"]
        payload = {"processId": process_id, "stepInstanceId": step_id, "decision": decision, "reason": "e2e secret reason"}
        opposite = {**payload, "decision": "REJECTED" if decision == "APPROVED" else "APPROVED"}

        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            racers = [pool.submit(action, "workflow", "manual", payload if index % 2 == 0 else opposite,
                                  REVIEWER if index % 2 == 0 else OTHER_REVIEWER, f"e2e-race-{index}-{uuid.uuid4()}")
                      for index in range(8)]
            results = [future.result() for future in racers]
        winners = [body for status, body in results if status == 200]
        check(len(winners) == 1, f"concurrent manual decisions must accept exactly one: {[s for s, _ in results]}")
        check(all(status == 409 and body["code"] == "workflow.decision_conflict" for status, body in results if status != 200),
              "losing decisions must be conflicts")
        winner = winners[0]["result"]
        expected_final = final if winner["decision"] == decision else ("REJECTED" if final == "COMPLETED" else "COMPLETED")
        wait_status(operation_id, expected_final)
        audit_rows = sql_rows(f"SELECT source, principal, outcome, reason_hash FROM autocheck.decisions WHERE process_id = '{process_id}'::uuid")
        check(len(audit_rows) == 1 and audit_rows[0]["source"] == "MANUAL" and audit_rows[0]["outcome"] == winner["decision"]
              and audit_rows[0]["reason_hash"] == hashlib.sha256(b"e2e secret reason").hexdigest(), f"manual audit: {audit_rows}")

    operation_id, submitted = create_and_submit("PAYMENT_APPROVAL", "250000.00")
    process_id = submitted["processId"]
    poll(f"SELECT state FROM autocheck.processes WHERE process_id = '{process_id}'::uuid", lambda rows: rows[0]["state"] == "WAITING_MANUAL")
    step_id = sql_rows(f"SELECT step_instance_id FROM autocheck.steps WHERE process_id = '{process_id}'::uuid AND step_type = 'MANUAL'")[0]["step_instance_id"]
    payload = {"processId": process_id, "stepInstanceId": step_id, "decision": "APPROVED", "reason": "ok"}
    key = f"e2e-manual-{uuid.uuid4()}"
    first, repeat, changed = (action("workflow", "manual", payload, REVIEWER, key),
                              action("workflow", "manual", payload, REVIEWER, key),
                              action("workflow", "manual", {**payload, "decision": "REJECTED"}, REVIEWER, key))
    check(first[0] == 200 and repeat[0] == 200 and all(first[1][k] == repeat[1][k] for k in ("status", "outcome", "result")),
          "identical manual repeat must return the same result")
    check(changed[0] == 409 and changed[1]["code"] == "idempotency.conflict", f"changed manual repeat: {changed}")
    check(action("workflow", "manual", payload, CLIENT, f"e2e-{uuid.uuid4()}")[0] == 403, "client token must not decide")
    wait_status(operation_id, "COMPLETED")


def scenario_concurrent_submit() -> None:
    status, created = action("payment", "request", {"operationKind": "PAYMENT_APPROVAL", "amount": "10.00", "currency": "RUB"},
                             CLIENT, f"e2e-request-{uuid.uuid4()}")
    operation_id = created["result"]["operationId"]
    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
        results = list(pool.map(lambda index: action("payment", "submit", {"operationId": operation_id}, CLIENT,
                                                     f"e2e-concurrent-{index}-{uuid.uuid4()}"), range(20)))
    process_ids = {body["result"]["processId"] for status, body in results if status == 200}
    check(len(process_ids) == 1 and all(status == 200 for status, _ in results), f"concurrent submit: {[s for s, _ in results]}")
    processes = sql_rows(f"SELECT process_id FROM autocheck.processes WHERE business_key = '{operation_id}'")
    check(len(processes) == 1, f"one pinned process expected: {processes}")
    events = sql_rows(f"SELECT count(*) AS n FROM autocheck.operation_events WHERE operation_id = '{operation_id}'::uuid "
                      "AND event_type = 'OPERATION_SUBMITTED'")
    check(events[0]["n"] == 1, f"one OPERATION_SUBMITTED expected: {events}")


def scenario_least_privilege() -> None:
    for role, statement in (("outbox_dispatcher", "SELECT count(*) FROM delivery.outbox"),
                            ("outbox_dispatcher", "SELECT api.invoke('payment','complete',1,'{}','{}')"),
                            ("inbox_reconciler", "UPDATE delivery.inbox SET state = 'APPLIED'"),
                            ("inbox_reconciler", "SELECT delivery.claim_outbox('x', 1)")):
        result = compose("exec", "-T", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "course",
                         "-c", f"SET ROLE {role}", "-c", statement)
        check(result.returncode != 0 and "permission denied" in result.stderr, f"{role} must not run: {statement}")


def scenario_failpoint_and_lease_expiry() -> None:
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        # Failpoints and the short outbox lease exist only in the test profile of the stand.
        raise Skipped("requires a stand started with COURSE_TEST_PROFILE=1")
    # The competing tenant is stopped BEFORE the work exists, so the target instance is the one
    # that claims it. Otherwise the second dispatcher delivers the payment and the barrier proves
    # nothing about the boundary it was supposed to hold.
    stop("outbox-dispatcher-b")
    start("outbox-dispatcher", recreate=True, environment={"COURSE_FAILPOINT": "after_outbox_claim"})
    try:
        operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1012.00")
        external_id = external_request(operation_id)
        wait_for_log("outbox-dispatcher", '"event":"failpoint.reached"')
        stop("outbox-dispatcher")
        leased = sql_rows("SELECT state, attempt_count, lease_owner FROM autocheck.outbox "
                          f"WHERE external_request_id = {sql_text(external_id)}")
        check(leased == [{"state": "LEASED", "attempt_count": 1, "lease_owner": "outbox-dispatcher"}],
              f"claim must be durable at the barrier and owned: {leased}")
        check(audit(external_id)[0] == 404, "a stopped failpoint must never reach the provider")
    finally:
        start(*DISPATCHERS, recreate=True)
    wait_status(operation_id, "COMPLETED")
    outbox = sql_rows("SELECT state, attempt_count, last_error_code FROM autocheck.outbox "
                      f"WHERE external_request_id = {sql_text(external_id)}")
    check(len(outbox) == 1 and outbox[0]["state"] == "CONFIRMED" and outbox[0]["attempt_count"] == 2
          and outbox[0]["last_error_code"] == "lease.expired.retryable",
          f"expired lease must be reclaimed with a visible trace: {outbox}")
    status, payment = audit(external_id)
    check(status == 200 and payment and payment["paymentCount"] == 1, f"one payment expected: {payment}")


def scenario_failpoint_after_provider_response() -> None:
    """The provider accepted the payment and the dispatcher died before reporting: one payment.

    This is the unknown outcome seen from inside: PostgreSQL has no report, the provider has the
    payment. Whatever continues the delivery - the receipt confirming it, or another dispatcher
    repeating the request under the same key - the provider must end up with exactly one payment.
    """
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        raise Skipped("requires a stand started with COURSE_TEST_PROFILE=1")
    stop("outbox-dispatcher-b")
    start("outbox-dispatcher", recreate=True, environment={"COURSE_FAILPOINT": "after_provider_response"})
    try:
        operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1081.00")
        external_id = external_request(operation_id)
        wait_for_log("outbox-dispatcher", '"name":"after_provider_response"')
        check(compose("kill", "outbox-dispatcher").returncode == 0, "cannot kill the dispatcher at the barrier")
        status, payment = audit(external_id)
        check(status == 200 and payment and payment["paymentCount"] == 1,
              f"the provider must have accepted the payment before the barrier: {status} {payment}")
        unreported = sql_rows("SELECT state, attempt_count, lease_owner FROM autocheck.outbox "
                              f"WHERE external_request_id = {sql_text(external_id)}")
        # The dead dispatcher never reported. The delivery is still its lease - unless the
        # provider's own callback has already confirmed it, which is the receipt taking priority.
        check(len(unreported) == 1 and unreported[0]["attempt_count"] == 1
              and (unreported[0]["state"] == "CONFIRMED"
                   or (unreported[0]["state"], unreported[0]["lease_owner"]) == ("LEASED", "outbox-dispatcher")),
              f"the attempt must stay unreported at the barrier: {unreported}")
    finally:
        start(*DISPATCHERS, recreate=True)
    wait_status(operation_id, "COMPLETED")
    outbox = sql_rows(f"SELECT state, attempt_count FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
    check(len(outbox) == 1 and outbox[0]["state"] == "CONFIRMED" and outbox[0]["attempt_count"] in (1, 2),
          f"the delivery must be confirmed without burning the budget: {outbox}")
    status, payment = audit(external_id)
    check(status == 200 and payment and payment["paymentCount"] == 1 and payment["idempotencyKey"] == external_id,
          f"an unknown outcome must not become a second payment: {payment}")


def scenario_failpoint_after_job_claim() -> None:
    """A worker dies right after the claim: the job is reclaimed under a new lease version, once."""
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        raise Skipped("requires a stand started with COURSE_TEST_PROFILE=1")
    # The competing tenant is stopped BEFORE the work exists, so the target worker claims it.
    stop("worker-b")
    start("worker-a", recreate=True, environment={"COURSE_FAILPOINT": "after_job_claim"})
    try:
        operation_id, submitted = create_and_submit("PAYMENT_APPROVAL", "11.00")
        wait_for_log("worker-a", '"name":"after_job_claim"')
        check(compose("kill", "worker-a").returncode == 0, "cannot kill the worker at the barrier")
        claimed = sql_rows("SELECT job_id, execution_id, state, lease_owner, lease_version, attempt_count "
                           f"FROM autocheck.jobs WHERE process_id = '{submitted['processId']}'::uuid")
        check(len(claimed) == 1 and (claimed[0]["state"], claimed[0]["lease_owner"], claimed[0]["lease_version"],
                                     claimed[0]["attempt_count"]) == ("LEASED", "worker-a", 1, 1),
              f"the claim must be durable at the barrier: {claimed}")
        untouched = sql_rows("SELECT o.status, (SELECT count(*) FROM autocheck.decisions d WHERE d.process_id = o.process_id) AS decisions "
                             f"FROM autocheck.operations o WHERE o.operation_id = '{operation_id}'::uuid")
        check(untouched == [{"status": "PROCESSING", "decisions": 0}],
              f"a worker stopped before the action must leave no effect: {untouched}")
    finally:
        start("worker-a", "worker-b", recreate=True)
    wait_status(operation_id, "COMPLETED")
    job_id = claimed[0]["job_id"]
    reclaimed = sql_rows(f"SELECT execution_id, lease_version FROM autocheck.jobs WHERE job_id = '{job_id}'::uuid")
    check(reclaimed == [{"execution_id": claimed[0]["execution_id"], "lease_version": 2}],
          f"the reclaimed job keeps its executionId and gets a new lease version: {reclaimed}")
    attempts = sql_rows("SELECT attempt_id, status, lease_version, execution_id FROM autocheck.attempts "
                        f"WHERE job_id = '{job_id}'::uuid ORDER BY attempt_number")
    check([(a["status"], a["lease_version"]) for a in attempts] == [("STALE", 1), ("SUCCEEDED", 2)]
          and len({a["attempt_id"] for a in attempts}) == 2
          and {a["execution_id"] for a in attempts} == {claimed[0]["execution_id"]},
          f"one stale and one successful attempt of the same execution are expected: {attempts}")
    decisions = sql_rows(f"SELECT count(*) AS n FROM autocheck.decisions WHERE process_id = '{submitted['processId']}'::uuid")
    check(decisions[0]["n"] == 1, f"the limit rule must decide exactly once: {decisions}")


def scenario_delivery_fencing() -> None:
    """A report of a previous tenant cannot overwrite the delivery of the new one.

    The boundary functions are called directly, as two tenants of the same delivery: that is the
    only way to hold a stale lease still and present it after the delivery has moved on.
    """
    stop(*DISPATCHERS)
    try:
        operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1082.00")
        external_id = external_request(operation_id)
        mine = f"WHERE external_request_id = {sql_text(external_id)}"
        first = sql_rows(f"SELECT outbox_id, lease_version FROM delivery.claim_outbox('e2e-tenant-a', 16) {mine}")
        check(len(first) == 1 and first[0]["lease_version"] == 1, f"the first tenant must get the delivery: {first}")
        outbox_id = first[0]["outbox_id"]
        poll(f"SELECT 1 FROM autocheck.outbox {mine} AND state = 'LEASED' AND lease_until <= clock_timestamp()",
             lambda rows: len(rows) == 1, timeout=30)
        second = sql_rows(f"SELECT outbox_id, lease_version FROM delivery.claim_outbox('e2e-tenant-b', 16) {mine}")
        check(second == [{"outbox_id": outbox_id, "lease_version": 2}], f"the expired delivery must be reclaimed: {second}")
        reclaimed = [{"state": "LEASED", "lease_owner": "e2e-tenant-b", "lease_version": 2, "attempt_count": 2,
                      "last_error_code": "lease.expired.retryable"}]
        snapshot = f"SELECT state, lease_owner, lease_version, attempt_count, last_error_code FROM autocheck.outbox {mine}"
        check(sql_rows(snapshot) == reclaimed, f"the reclaim must leave a visible trace: {sql_rows(snapshot)}")

        # Every combination that is not "the current owner with the current version" is stale -
        # for a success and for a failure alike.
        for owner, version in (("e2e-tenant-a", 1), ("e2e-tenant-a", 2), ("e2e-tenant-b", 1)):
            for report in (f"delivery.succeed_outbox('{outbox_id}'::uuid, '{owner}', {version}, 'provider-from-the-past')",
                           f"delivery.fail_outbox('{outbox_id}'::uuid, '{owner}', {version}, 'http.400.terminal')"):
                refused = sql_rows(f"SELECT {report} AS report")[0]["report"]
                check(refused.get("status") == "error" and refused.get("code") == "delivery.lease_stale",
                      f"a stale report must be refused: {owner}/{version} -> {refused}")
        check(sql_rows(snapshot) == reclaimed, f"refused reports must change nothing: {sql_rows(snapshot)}")

        # The current owner reports the second failure: the second delay of the published ladder.
        accepted = sql_rows(f"SELECT delivery.fail_outbox('{outbox_id}'::uuid, 'e2e-tenant-b', 2, "
                            "'transport.error.retryable') AS report")[0]["report"]
        check(accepted.get("status") == "ok" and accepted.get("state") == "RETRY_WAIT", f"the owner's report: {accepted}")
        if os.environ.get("COURSE_TEST_PROFILE") == "1":
            check(400 <= accepted.get("nextAttemptInMs", -1) <= 500,
                  f"the second delay of the published profile is 400 ms plus jitter up to 100 ms: {accepted}")
        # The lease was spent by that report: repeating it is stale as well.
        repeated = sql_rows(f"SELECT delivery.fail_outbox('{outbox_id}'::uuid, 'e2e-tenant-b', 2, "
                            "'transport.error.retryable') AS report")[0]["report"]
        check(repeated.get("code") == "delivery.lease_stale", f"a spent lease cannot report twice: {repeated}")
    finally:
        start(*DISPATCHERS)
    wait_status(operation_id, "COMPLETED")
    outbox = sql_rows(f"SELECT state, attempt_count FROM autocheck.outbox {mine}")
    check(outbox == [{"state": "CONFIRMED", "attempt_count": 3}], f"an ordinary dispatcher must finish the delivery: {outbox}")
    status, payment = audit(external_id)
    check(status == 200 and payment and payment["paymentCount"] == 1 and payment["requestCount"] == 1,
          f"the provider must have seen exactly one request: {payment}")


def scenario_receipt_lock_order() -> None:
    """A receipt racing a process step must queue behind it, not deadlock with it.

    Session A reproduces the lock pattern of a worker step (operation FOR UPDATE, then the process
    FOR UPDATE inside finish_job). Advisory locks and pg_locks/pg_stat_activity serve as barriers,
    so the interleaving is deterministic rather than timed.
    """
    barrier = 733001
    stop(*DISPATCHERS)
    holder = None
    try:
        operation_id, submitted = create_and_submit("PAYMENT_EXECUTION", "1013.00")
        external_id = external_request(operation_id)
        psql = ["docker", "compose", "-p", PROJECT, "exec", "-T", "postgres", "psql", "-X", "-U", "postgres", "-d", "course"]
        env = {**os.environ, "MSYS2_ARG_CONV_EXCL": "*"}

        holder = subprocess.Popen(psql, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, env=env)
        holder.stdin.write(f"SELECT pg_advisory_lock({barrier});" + chr(10))
        holder.stdin.flush()
        poll(f"SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND objid = {barrier} AND granted",
             lambda rows: len(rows) == 1, timeout=30)

        step = subprocess.Popen(
            psql + ["-v", "ON_ERROR_STOP=1", "-c", "BEGIN",
                    "-c", f"SELECT 1 FROM payment.operation WHERE operation_id = '{operation_id}' FOR UPDATE",
                    "-c", f"SELECT pg_advisory_xact_lock({barrier})",
                    "-c", f"SELECT 1 FROM workflow.process_instance WHERE process_id = '{submitted['processId']}' FOR UPDATE",
                    "-c", "COMMIT"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
        poll(f"SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND objid = {barrier} AND NOT granted",
             lambda rows: len(rows) == 1, timeout=30)

        receipt, signature = signed_receipt(external_id, f"e2e-lock-{secrets.token_hex(5)}", outcome="COMPLETED")
        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
            accepted = pool.submit(action, "receipt", "accept", receipt, RECEIPT, receipt["messageId"], signature)
            poll("SELECT 1 FROM pg_stat_activity WHERE application_name = 'course-api' AND wait_event_type = 'Lock'",
                 lambda rows: len(rows) >= 1 or accepted.done(), timeout=30)
            check(not accepted.done(), "receipt.accept must wait behind the step holding the operation")

            holder.stdin.write(f"SELECT pg_advisory_unlock({barrier});" + chr(10) + "\\q" + chr(10))
            holder.stdin.close()
            holder.wait(timeout=30)
            _, step_errors = step.communicate(timeout=60)
            status, body = accepted.result(timeout=60)

        check(step.returncode == 0 and "deadlock" not in step_errors,
              f"the process step must not be chosen as a deadlock victim: {step_errors[-300:]}")
        check(status == 200 and body and body["outcome"] == "RECEIVED", f"receipt must be accepted after the step: {status} {body}")
        wait_status(operation_id, "COMPLETED")
    finally:
        if holder is not None and holder.poll() is None:
            holder.kill()
        compose("up", "-d", "--no-build", "--no-deps", *DISPATCHERS)


def scenario_submit_rejects_foreign_process() -> None:
    status, created = action("payment", "request", {"operationKind": "PAYMENT_EXECUTION", "amount": "1014.00", "currency": "RUB"},
                             CLIENT, f"e2e-request-{uuid.uuid4()}")
    check(status == 200 and created, f"payment.request failed: {status} {created}")
    operation_id = created["result"]["operationId"]
    # A trusted operator starts the flow with the operation's business key, bypassing submit.
    foreign = subprocess.run(
        ["docker", "compose", "-p", PROJECT, "run", "--rm", "-T", "--no-deps", "cli",
         "flow", "start", "payment-processing", "--business-key", operation_id, "--data", "/dev/stdin"],
        input=json.dumps({"operationId": operation_id}), capture_output=True, text=True, timeout=180,
        env={**os.environ, "MSYS2_ARG_CONV_EXCL": "*"}, check=False)
    check(foreign.returncode == 0, f"cannot start the foreign process: {foreign.stdout[-300:]}")
    status, body = action("payment", "submit", {"operationId": operation_id}, CLIENT, f"e2e-submit-{uuid.uuid4()}")
    check(status == 422 and body and body["code"] == "payment.process_conflict",
          f"submit must not bind the operation to a process it did not start: {status} {body}")
    rows = sql_rows(f"SELECT status, process_id FROM autocheck.operations WHERE operation_id = '{operation_id}'::uuid")
    check(rows == [{"status": "CREATED", "process_id": None}], f"operation must stay untouched: {rows}")


def scenario_storage_invariants() -> None:
    # Triggers bind every role, the superuser included. Each probe runs inside a transaction that
    # is rolled back, so a missing guard is reported without corrupting the stand.
    confirmed = sql_rows("SELECT outbox_id FROM autocheck.outbox WHERE state = 'CONFIRMED' LIMIT 1")
    applied = sql_rows("SELECT message_id FROM autocheck.inbox WHERE state = 'APPLIED' LIMIT 1")
    check(confirmed and applied, "storage probes need a confirmed delivery and an applied message")
    probes = (
        (f"UPDATE delivery.outbox SET last_error_code = 'http.500.retryable' WHERE outbox_id = '{confirmed[0]['outbox_id']}'",
         "confirmed delivery is final"),
        (f"UPDATE delivery.inbox SET applied_at = now() - interval '1 day' WHERE message_id = {sql_text(applied[0]['message_id'])}",
         "a settled inbox message is immutable"),
        ("UPDATE payment.flow_binding SET flow_name = 'workflow-smoke'", "server-side flow binding is immutable"),
        ("DELETE FROM workflow.decision", "decision audit is append only"),
        ("UPDATE payment.receipt SET outcome = 'COMPLETED'", "receipt evidence is immutable"),
        ("UPDATE payment.operation SET status = 'PROCESSING' WHERE status IN ('COMPLETED', 'REJECTED')",
         "operation status transition"),
        ("UPDATE payment.operation SET process_id = gen_random_uuid() WHERE status = 'COMPLETED'",
         "operation process binding is write-once"),
    )
    for statement, expected in probes:
        result = compose("exec", "-T", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "course",
                         "-c", "BEGIN", "-c", statement, "-c", "ROLLBACK")
        check(result.returncode != 0 and expected in result.stderr, f"storage guard missing: {statement[:60]}")


def docker(*args: str, timeout: float = 60) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, timeout=timeout, check=False)


def run_dispatcher(**environment: str) -> str:
    """Starts a one-off dispatcher with its own environment and returns the container id.

    The regular service is stopped for the duration: two dispatchers with the same owner would
    compete for the very deliveries the scenario watches.
    """
    call = ["run", "--detach", "--no-deps"]
    for name, value in environment.items():
        call += ["-e", f"{name}={value}"]
    result = compose(*call, "outbox-dispatcher")
    container = (result.stdout.strip().splitlines() or [""])[-1].strip()
    check(result.returncode == 0 and bool(container), f"one-off dispatcher did not start: {result.stderr[-300:]}")
    return container


def remove_container(container: str) -> None:
    """Instant removal: unlike a graceful stop, a kill cannot let one more attempt slip through."""
    docker("kill", container)
    docker("rm", "--force", container)


def attempt_log(container: str, external_id: str) -> list[dict[str, Any]]:
    """Structured ``outbox.attempt`` records of one delivery, in the order they were written."""
    records = []
    for line in docker("logs", container).stdout.splitlines():
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        if record.get("event") == "outbox.attempt" and record.get("externalRequestId") == external_id:
            records.append(record)
    return records


def scenario_outbox_retry_budget() -> None:
    """Four attempts including the first, delays 200/400/800 ms plus jitter, and what exhaustion means.

    The provider is made unreachable by pointing a one-off dispatcher at a closed port. A stopped
    container would leave its name unresolvable, so the provider timeout no longer bounds an
    attempt; a paused one still accepts connections into the kernel backlog and processes every
    queued request afterwards. Neither models a provider that is simply not there.
    """
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        raise Skipped("requires the published test profile of a stand started with COURSE_TEST_PROFILE=1")
    policy = sql_rows("SELECT max_attempts, backoff_base_ms, backoff_max_ms, jitter_max_ms FROM delivery.active_policy()")
    check(policy == [{"max_attempts": 4, "backoff_base_ms": 200, "backoff_max_ms": 800, "jitter_max_ms": 100}],
          f"published test profile expected: {policy}")

    # Both regular tenants stand still: a one-off dispatcher must be the only one attempting.
    stop(*DISPATCHERS)
    try:
        # 1. Unreachable through the whole budget: every attempt, every delay, then a terminal state
        #    that does not pretend to know the subject result.
        container = run_dispatcher(PROVIDER_URL="http://127.0.0.1:9")
        try:
            operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1050.00")
            external_id = external_request(operation_id)
            dead = poll("SELECT state, attempt_count, last_error_code FROM autocheck.outbox "
                        f"WHERE external_request_id = {sql_text(external_id)}",
                        lambda rows: len(rows) == 1 and rows[0]["state"] in ("DEAD", "CONFIRMED"), timeout=30)
            check(dead == [{"state": "DEAD", "attempt_count": 4, "last_error_code": "transport.error.retryable"}],
                  f"exhaustion must take exactly four attempts: {dead}")
            # The record is written after the report is committed, so the last line may trail the
            # DEAD row by a moment: wait for the log to catch up instead of reading it once.
            deadline = time.monotonic() + 5
            attempts = attempt_log(container, external_id)
            while len(attempts) < 4 and time.monotonic() < deadline:
                time.sleep(0.1)
                attempts = attempt_log(container, external_id)
            check([a["state"] for a in attempts] == ["RETRY_WAIT", "RETRY_WAIT", "RETRY_WAIT", "DEAD"],
                  f"attempt states: {attempts}")
            # The exponent is exact, the jitter is uniform over 0..100 ms and added to every delay.
            delays = [a.get("nextAttemptInMs") for a in attempts]
            check(delays[3] is None and all(isinstance(d, int) and base <= d <= base + 100
                                            for d, base in zip(delays[:3], (200, 400, 800))),
                  f"delays decided by PostgreSQL: {attempts}")
            check(all(a["errorCode"] == "transport.error.retryable" and a["httpStatus"] is None for a in attempts),
                  f"an unreachable provider is a retryable transport error: {attempts}")
            check(len({a["correlationId"] for a in attempts}) == 1, f"the correlation identifier must not change: {attempts}")
        finally:
            remove_container(container)

        state = sql_rows("SELECT o.status AS operation, p.state AS process FROM autocheck.operations o "
                         f"JOIN autocheck.processes p USING (process_id) WHERE o.operation_id = '{operation_id}'::uuid")
        check(state == [{"operation": "PROCESSING", "process": "WAITING_SIGNAL"}],
              f"DEAD is a delivery fact, not a subject result: {state}")
        check(audit(external_id)[0] == 404, "an unreachable provider must not have seen the payment")

        # A valid receipt after exhaustion still confirms the delivery and settles the operation.
        message_id = f"e2e-late-{secrets.token_hex(5)}"
        receipt, signature = signed_receipt(external_id, message_id)
        status, body = action("receipt", "accept", receipt, RECEIPT, message_id, signature)
        check(status == 200 and body is not None and body["outcome"] == "RECEIVED", f"late receipt: {status} {body}")
        wait_status(operation_id, "COMPLETED")
        outbox = sql_rows(f"SELECT state, attempt_count FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
        check(outbox == [{"state": "CONFIRMED", "attempt_count": 4}], f"a late receipt confirms without erasing the attempts: {outbox}")

        # 2. Unreachable for three attempts, back before the fourth: the last attempt is still there.
        #    Attempts are gated by the dispatcher poll: with a four-second poll the wait before the
        #    fourth attempt lasts seconds, and the one-off dispatcher is killed inside that window.
        #    The test profile clamps the poll to 100 ms, so this one dispatcher runs outside it; the
        #    attempt budget and the delays still come from PostgreSQL, not from its profile.
        container = run_dispatcher(PROVIDER_URL="http://127.0.0.1:9", COURSE_OUTBOX_POLL_MS="4000",
                                   COURSE_TEST_PROFILE="0")
        try:
            operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1060.00")
            external_id = external_request(operation_id)
            poll(f"SELECT state, attempt_count FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}",
                 lambda rows: rows == [{"state": "RETRY_WAIT", "attempt_count": 3}], timeout=40)
        finally:
            remove_container(container)
    finally:
        start(*DISPATCHERS)

    wait_status(operation_id, "COMPLETED")
    outbox = sql_rows(f"SELECT state, attempt_count FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
    check(outbox == [{"state": "CONFIRMED", "attempt_count": 4}], f"the fourth attempt must complete the delivery: {outbox}")
    status, payment = audit(external_id)
    check(status == 200 and payment is not None and payment["paymentCount"] == 1 and payment["requestCount"] == 1
          and payment["idempotencyKey"] == external_id,
          f"the restored provider must see one request with the original key: {payment}")

def scenario_health_and_metrics() -> None:
    """Live, ready and metrics of all eight processes, and what a provider outage may not break."""
    for service in OBSERVABILITY:
        status, body, content_type = observe(service, "/health/live")
        check(status == 200 and json.loads(body) == {"status": "live"}
              and content_type.startswith("application/json"),
              f"{service} liveness: {status} {body!r} {content_type}")
        status, body, _ = observe(service, "/health/ready")
        check(status == 200 and json.loads(body) == {"status": "ready"},
              f"{service} readiness: {status} {body!r}")
        status, document, content_type = observe(service, "/metrics")
        check(status == 200 and content_type == "application/openmetrics-text; version=1.0.0; charset=utf-8",
              f"{service} metrics media type: {status} {content_type}")
        openmetrics(document)

    required = {"workflow_jobs_ready", "workflow_job_oldest_age_seconds", "workflow_processes_waiting",
                "outbox_pending", "outbox_oldest_age_seconds", "workflow_failures_total"}
    samples = openmetrics(observe("api", "/metrics")[1])
    check(required <= set(samples), f"api must publish every required series: {sorted(set(samples))}")

    # The metric is a projection of the authoritative state, so it has to agree with the queue it
    # describes. Both are read with the dispatchers stopped, so nothing moves between the reads.
    stop(*DISPATCHERS)
    try:
        operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1030.00")
        external_id = external_request(operation_id)
        poll(f"SELECT state FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}",
             lambda rows: len(rows) == 1 and rows[0]["state"] in ("PENDING", "RETRY_WAIT"))
        pending = sql_rows("SELECT count(*) AS value FROM autocheck.outbox "
                           "WHERE state IN ('PENDING', 'LEASED', 'RETRY_WAIT')")[0]["value"]
        samples = openmetrics(observe("api", "/metrics")[1])
        check(samples["outbox_pending"] == float(pending) and samples["outbox_pending"] >= 1,
              f"outbox_pending {samples['outbox_pending']} must equal the queue {pending}")
        check(samples["outbox_oldest_age_seconds"] > 0, "an undelivered message must have an age")

        # A provider outage is a designed mode: it shows up in delivery state and metrics, never in
        # readiness of the runtime. Otherwise the orchestrator is asked to drain the very processes
        # that are supposed to work the backlog off.
        stop("provider-simulator")
        try:
            start("outbox-dispatcher")
            for service in ("api", "worker-a", "worker-b", "outbox-dispatcher"):
                status, body, _ = observe(service, "/health/ready", source="inbox-reconciler")
                check(status == 200 and json.loads(body) == {"status": "ready"},
                      f"{service} must stay ready while the provider is away: {status} {body!r}")
            retry = poll("SELECT state, attempt_count, last_error_code FROM autocheck.outbox "
                         f"WHERE external_request_id = {sql_text(external_id)}",
                         lambda rows: len(rows) == 1 and rows[0]["state"] in ("RETRY_WAIT", "DEAD"),
                         timeout=20)
            check(str(retry[0]["last_error_code"]).endswith(".retryable"),
                  f"provider outage must be classified as retryable: {retry}")
        finally:
            start("provider-simulator")
    finally:
        start(*DISPATCHERS)
    wait_status(operation_id, "COMPLETED")
    status, payment = audit(external_id)
    check(status == 200 and payment and payment["paymentCount"] == 1,
          f"recovery after the outage must produce exactly one payment: {payment}")


CLAIMABLE_JOBS = ("SELECT count(*) AS value FROM autocheck.jobs "
                  "WHERE (state IN ('READY', 'RETRY_WAIT') AND next_attempt_at <= clock_timestamp()) "
                  "OR (state = 'LEASED' AND lease_until <= clock_timestamp())")


def jobs_ready_sample() -> tuple[dict[str, float], int]:
    """The metrics of ``api`` and the job queue they describe, with nothing moving between the reads.

    Availability of a job depends on the clock (a retry becomes due, a lease expires), so the queue
    is counted before and after the scrape and the sample is kept only when both counts agree.
    """
    deadline = time.monotonic() + 20
    while True:
        before = sql_rows(CLAIMABLE_JOBS)[0]["value"]
        samples = openmetrics(observe("api", "/metrics")[1])
        if sql_rows(CLAIMABLE_JOBS)[0]["value"] == before:
            return samples, before
        check(time.monotonic() < deadline, "the job queue keeps moving while both workers are stopped")


def scenario_job_queue_metrics() -> None:
    """``workflow_jobs_ready`` is what a worker could claim right now - an abandoned job included.

    The published meaning of the series is "jobs available for claim now", and that is the very
    predicate ``workflow.claim_jobs`` selects its candidates by: due READY/RETRY_WAIT jobs and
    LEASED jobs whose lease has expired. A job left behind by a dead worker is not running and is
    waiting to be reclaimed; a queue metric that does not count it reads zero while work stands.
    """
    workers = ("worker-a", "worker-b")
    stop(*workers)
    try:
        operation_id, submitted = create_and_submit("PAYMENT_APPROVAL", "10.00")
        job = poll(f"SELECT job_id, state FROM autocheck.jobs WHERE process_id = '{submitted['processId']}'::uuid",
                   lambda rows: len(rows) == 1 and rows[0]["state"] == "READY")
        job_id = job[0]["job_id"]
        samples, ready = jobs_ready_sample()
        check(ready >= 1 and samples["workflow_jobs_ready"] == float(ready),
              f"workflow_jobs_ready {samples['workflow_jobs_ready']} must equal the claimable queue {ready}")

        # A tenant claims everything claimable and disappears - what a killed worker leaves behind.
        claimed = sql_rows("SELECT jsonb_array_length(workflow.claim_jobs('e2e-abandoned-worker', 16, 250)) AS claimed")
        check(claimed[0]["claimed"] >= 1, f"the abandoned tenant claimed nothing: {claimed}")
        leased = sql_rows(f"SELECT state, lease_owner FROM autocheck.jobs WHERE job_id = '{job_id}'::uuid")
        check(leased == [{"state": "LEASED", "lease_owner": "e2e-abandoned-worker"}],
              f"the job must be leased by the abandoned tenant: {leased}")

        # The lease is never shorter than the declared budget of the step, so its expiry is awaited
        # through the projection rather than slept through.
        poll(f"SELECT 1 FROM autocheck.jobs WHERE job_id = '{job_id}'::uuid "
             "AND state = 'LEASED' AND lease_until <= clock_timestamp()",
             lambda rows: len(rows) == 1, timeout=60)
        samples, ready = jobs_ready_sample()
        check(ready >= 1 and samples["workflow_jobs_ready"] == float(ready),
              f"an abandoned job is claimable: workflow_jobs_ready {samples['workflow_jobs_ready']}, queue {ready}")
        check(samples["workflow_job_oldest_age_seconds"] > 0,
              "a job waiting to be reclaimed must have an age")
    finally:
        start(*workers)

    # Recovery is the ordinary loop: a real worker reclaims the job and the process completes.
    wait_status(operation_id, "COMPLETED")
    attempts = sql_rows(f"SELECT status, lease_version FROM autocheck.attempts WHERE job_id = '{job_id}'::uuid "
                        "ORDER BY attempt_number")
    check(attempts == [{"status": "STALE", "lease_version": 1}, {"status": "SUCCEEDED", "lease_version": 2}],
          f"the reclaimed job must keep its history: {attempts}")


def scenario_dead_letter_and_late_receipt() -> None:
    """Exhausted delivery is a technical outcome, and a late receipt still wins."""
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        raise Skipped("requires the short retry profile of a stand started with COURSE_TEST_PROFILE=1")
    stop("provider-simulator")
    try:
        operation_id, submitted = create_and_submit("PAYMENT_EXECUTION", "1040.00")
        external_id = external_request(operation_id)
        dead = poll("SELECT state, attempt_count, last_error_code, dead_at IS NOT NULL AS has_dead_at "
                    f"FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}",
                    lambda rows: len(rows) == 1 and rows[0]["state"] == "DEAD", timeout=40)
        check(dead[0]["attempt_count"] == 4 and dead[0]["has_dead_at"]
              and str(dead[0]["last_error_code"]).endswith(".retryable"),
              f"exhaustion must keep the attempt count, the moment and the last code: {dead}")

        # The provider may have accepted the request and lost the response, so DEAD answers only
        # the question about delivery attempts. A false subject failure here would be a lie.
        state = sql_rows(
            "SELECT o.status AS operation_status, p.state AS process_state "
            "FROM autocheck.operations o JOIN autocheck.processes p USING (process_id) "
            f"WHERE o.operation_id = '{operation_id}'::uuid")
        check(state == [{"operation_status": "PROCESSING", "process_state": "WAITING_SIGNAL"}],
              f"DEAD delivery must not fail the operation or the process: {state}")

        status, stalled = action("diagnostics", "stalled", {}, OBSERVER)
        items = (stalled or {}).get("result", {}).get("items", [])
        check(status == 200 and stalled and stalled["outcome"] == "FOUND", f"stalled: {status} {stalled}")
        check(any(item["operationId"] == operation_id and item["externalRequestId"] == external_id
                  and item["processId"] == submitted["processId"] for item in items),
              f"an exhausted delivery must be reported as stalled: {items}")
        identifiers = [item["operationId"] for item in items]
        check(identifiers == sorted(set(identifiers)), f"stalled items must be unique and ordered: {identifiers}")

        # A valid receipt arriving after exhaustion confirms the delivery and continues the process.
        message_id = f"e2e-late-{secrets.token_hex(5)}"
        receipt, signature = signed_receipt(external_id, message_id)
        status, body = action("receipt", "accept", receipt, RECEIPT, message_id, signature)
        check(status == 200 and body and body["outcome"] == "RECEIVED", f"late receipt: {status} {body}")
        wait_status(operation_id, "COMPLETED")
        outbox = sql_rows("SELECT state, dead_at IS NOT NULL AS has_dead_at, attempt_count "
                          f"FROM autocheck.outbox WHERE external_request_id = {sql_text(external_id)}")
        check(outbox == [{"state": "CONFIRMED", "has_dead_at": True, "attempt_count": 4}],
              f"late receipt must confirm without erasing the exhaustion evidence: {outbox}")

        status, stalled = action("diagnostics", "stalled", {}, OBSERVER)
        items = (stalled or {}).get("result", {}).get("items", [])
        check(status == 200 and all(item["operationId"] != operation_id for item in items),
              f"a settled operation must leave the stalled selection: {items}")
    finally:
        start("provider-simulator")


TRACE_RESULT_SCHEMA = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir,
                                   "contracts", "course-1", "diagnostics-trace.result.schema.json")
TRACE_SECTIONS = {"query", "dispatches", "operation", "operationEvents", "process", "steps", "jobs",
                  "attempts", "outbox", "inbox", "receipts", "decisions"}


def wait_dispatches(request_ids: set[str]) -> dict[str, str]:
    """Correlation id of the recorded call for every request key, once all of them have landed.

    The API writes its technical trail off the request path, so a call answered a moment ago may
    not be in ``autocheck.action_dispatches`` yet. A trace read before that would differ from a
    trace read after it for a reason that has nothing to do with the identifier asked.
    """
    keys = ", ".join(sql_text(value) for value in sorted(request_ids))
    rows = poll(f"SELECT request_id, correlation_id FROM autocheck.action_dispatches WHERE request_id IN ({keys})",
                lambda rows: {row["request_id"] for row in rows} >= request_ids, timeout=30)
    return {row["request_id"]: row["correlation_id"] for row in rows}


def trace_chain(label: str, identifiers: list[tuple[str, str]], operation_id: str, process_id: str,
                evidence: str) -> dict[str, Any]:
    """Traces one chain by every identifier given and requires the same subject facts each time."""
    baseline: dict[str, Any] | None = None
    for kind, identifier in identifiers:
        status, body = action("diagnostics", "trace", {"identifier": identifier}, OBSERVER)
        result = (body or {}).get("result")
        check(status == 200 and body and body["outcome"] == "FOUND" and isinstance(result, dict),
              f"{label}: trace by {kind}: {status} {body}")
        check(TRACE_SECTIONS <= set(result), f"{label}: trace by {kind} is incomplete: {sorted(set(result))}")
        check(kind in result["query"]["matchedBy"] and result["query"]["identifier"] == identifier,
              f"{label}: trace by {kind} reported {result['query']}")
        check(isinstance(result["operation"], dict) and result["operation"]["operationId"] == operation_id
              and isinstance(result["process"], dict) and result["process"]["processId"] == process_id
              and bool(result[evidence]),
              f"{label}: trace by {kind} ({identifier}) lost the chain: {result['operation']} {result['process']}")
        subject = {key: value for key, value in result.items() if key != "query"}
        if baseline is None:
            baseline = subject
        else:
            check(subject == baseline, f"{label}: trace by {kind} ({identifier}) returned different subject facts")
    check(baseline is not None, f"{label}: nothing was traced")
    return baseline


def scenario_diagnostics_trace() -> None:
    """Every published identifier kind reaches its chain, and every one returns the same facts.

    The kinds are taken from the published result schema, so a kind the contract names and this
    scenario does not trace is a failure rather than a silent gap. ``correlationId`` and
    ``requestId`` are asked for every kind of call a chain consists of - the command that created
    the operation, the submit, the manual decision, the receipt and an action called by a worker -
    because each of them is linked to the chain by a different saved fact.
    """
    request_key, submit_key = f"e2e-trace-request-{uuid.uuid4()}", f"e2e-trace-submit-{uuid.uuid4()}"
    status, created = action("payment", "request",
                             {"operationKind": "PAYMENT_EXECUTION", "amount": "1051.00", "currency": "RUB"},
                             CLIENT, request_key)
    check(status == 200 and created and created["status"] == "ok", f"payment.request failed: {status} {created}")
    operation_id = created["result"]["operationId"]
    status, submitted = action("payment", "submit", {"operationId": operation_id}, CLIENT, submit_key)
    check(status == 200 and submitted and submitted["outcome"] == "SUBMITTED", f"payment.submit failed: {status} {submitted}")
    process_id = submitted["result"]["processId"]
    wait_status(operation_id, "COMPLETED")
    external_id = external_request(operation_id)
    job = sql_rows("SELECT job_id, execution_id, step_instance_id FROM autocheck.jobs "
                   f"WHERE process_id = '{process_id}'::uuid ORDER BY job_id LIMIT 1")
    check(len(job) == 1, f"trace sources missing: {job}")
    attempt = sql_rows(f"SELECT attempt_id FROM autocheck.attempts WHERE job_id = '{job[0]['job_id']}'::uuid "
                       "ORDER BY attempt_id LIMIT 1")
    message = sql_rows(f"SELECT message_id FROM autocheck.receipts WHERE external_request_id = {sql_text(external_id)}")
    check(len(attempt) == 1 and len(message) == 1, f"trace sources missing: {attempt} {message}")
    execution_id, message_id = job[0]["execution_id"], message[0]["message_id"]
    correlation = wait_dispatches({request_key, submit_key, message_id, execution_id})

    identifiers = [
        ("operationId", operation_id),
        ("processId", process_id),
        ("externalRequestId", external_id),
        ("stepInstanceId", job[0]["step_instance_id"]),
        ("jobId", job[0]["job_id"]),
        ("executionId", execution_id),
        ("attemptId", attempt[0]["attempt_id"]),
        ("messageId", message_id),
        ("requestId", request_key),                                  # the command that created the operation
        ("requestId", submit_key),                                   # a command whose key no subject row keeps
        ("correlationId", created["meta"]["correlationId"]),
        ("correlationId", submitted["meta"]["correlationId"]),
        ("correlationId", correlation[message_id]),                  # receipt.accept called by the adapter
        ("correlationId", correlation[execution_id]),                # a registered action called by a worker
    ]
    baseline = trace_chain("provider chain", identifiers, operation_id, process_id, "receipts")
    check(bool(baseline["outbox"]), f"provider chain lost its delivery: {baseline['outbox']}")
    routes = {(call["module"], call["action"]) for call in baseline["dispatches"]}
    expected_routes = {("payment", "request"), ("payment", "submit"), ("payment", "prepare_external"),
                       ("receipt", "accept")}
    check(expected_routes <= routes, f"the trail of calls is incomplete: {sorted(routes)}")

    # The second process is settled by an audited decision instead of a receipt.
    manual_key = f"e2e-trace-manual-{uuid.uuid4()}"
    manual_operation, manual = create_and_submit("PAYMENT_APPROVAL", "100000.01")
    step = poll("SELECT step_instance_id FROM autocheck.steps "
                f"WHERE process_id = '{manual['processId']}'::uuid AND step_type = 'MANUAL' AND state = 'WAITING'",
                lambda rows: len(rows) == 1)
    status, decided = action("workflow", "manual",
                             {"processId": manual["processId"], "stepInstanceId": step[0]["step_instance_id"],
                              "decision": "APPROVED", "reason": "e2e trace reason"}, REVIEWER, manual_key)
    check(status == 200 and decided and decided["outcome"] == "APPROVED", f"manual decision: {status} {decided}")
    wait_status(manual_operation, "COMPLETED")
    wait_dispatches({manual_key})
    decision_id = decided["result"]["decisionId"]
    manual_identifiers = [
        ("decisionId", decision_id),
        ("stepInstanceId", step[0]["step_instance_id"]),
        ("operationId", manual_operation),
        ("requestId", manual_key),
        ("correlationId", decided["meta"]["correlationId"]),
    ]
    manual_baseline = trace_chain("review chain", manual_identifiers, manual_operation, manual["processId"], "decisions")
    check(any(decision["decisionId"] == decision_id and decision["source"] == "MANUAL"
              for decision in manual_baseline["decisions"]),
          f"the audited decision is missing from its own trace: {manual_baseline['decisions']}")
    check(("workflow", "manual") in {(call["module"], call["action"]) for call in manual_baseline["dispatches"]},
          f"the manual command is missing from the trail of calls: {manual_baseline['dispatches']}")

    with open(TRACE_RESULT_SCHEMA, encoding="utf-8") as schema:
        published = set(json.load(schema)["properties"]["query"]["properties"]["matchedBy"]["items"]["enum"])
    traced = {kind for kind, _ in identifiers + manual_identifiers}
    check(traced == published, f"identifier kinds not traced: {sorted(published - traced)}, unknown: {sorted(traced - published)}")

    # Nothing sensitive may travel in a diagnostic projection.
    serialized = json.dumps([baseline, manual_baseline])
    for forbidden in (HMAC_SECRET, CAPABILITY, AUDIT_TOKEN, RECEIPT, "e2e rejection", "e2e trace reason"):
        check(forbidden not in serialized, "trace leaked a secret or a message body")

    status, body = action("diagnostics", "trace", {"identifier": f"unknown-{secrets.token_hex(8)}"}, OBSERVER)
    check(status == 404 and body and body["code"] == "diagnostics.trace_not_found", f"unknown identifier: {status} {body}")
    status, body = action("diagnostics", "trace", {"identifier": operation_id}, CLIENT)
    check(status == 403 and body and body["code"] == "access.denied", f"trace without policy: {status} {body}")
    status, body = action("diagnostics", "stalled", {"unexpected": True}, OBSERVER)
    check(status == 422 and body and body["code"] == "payload.invalid", f"stalled payload: {status} {body}")


def scenario_api_failpoints() -> None:
    """The two commit boundaries of the API: a durable Inbox and a rolled-back manual decision."""
    if os.environ.get("COURSE_TEST_PROFILE") != "1":
        raise Skipped("requires a stand started with COURSE_TEST_PROFILE=1")

    # after_inbox_saved: the receipt is committed, the API dies before answering, and the signal is
    # applied later by an ordinary reconciler - not by the API.
    #
    # The dispatchers are stopped too, and not out of caution: a delivered payment brings the
    # provider's OWN callback, and then the synthetic receipt below is a second message for the
    # same waiting step. The reconciler would settle it as CONFLICT - correctly - and the scenario
    # would be measuring the wrong thing.
    stop(*DISPATCHERS, *RECONCILERS)
    try:
        operation_id, _ = create_and_submit("PAYMENT_EXECUTION", "1060.00")
        external_id = external_request(operation_id)
        poll("SELECT p.state FROM autocheck.processes p JOIN autocheck.operations o USING (process_id) "
             f"WHERE o.operation_id = '{operation_id}'::uuid",
             lambda rows: len(rows) == 1 and rows[0]["state"] == "WAITING_SIGNAL")
        message_id = f"e2e-inbox-{secrets.token_hex(5)}"
        start("api", recreate=True, environment={"COURSE_FAILPOINT": "after_inbox_saved"})
        wait_gateway()
        receipt, signature = signed_receipt(external_id, message_id)
        pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)
        pool.submit(action, "receipt", "accept", receipt, RECEIPT, message_id, signature)
        wait_for_log("api", '"name":"after_inbox_saved"')
        check(compose("kill", "api").returncode == 0, "cannot kill the API at the barrier")
        pool.shutdown(wait=False)
        saved = sql_rows(f"SELECT state FROM autocheck.inbox WHERE message_id = {sql_text(message_id)}")
        check(saved == [{"state": "RECEIVED"}], f"the Inbox commit must survive the API: {saved}")
        check(sql_rows(f"SELECT 1 FROM autocheck.signals WHERE message_id = {sql_text(message_id)}") == [],
              "the API must not do the reconciler's work")
    finally:
        start("api", recreate=True)
        start(*DISPATCHERS, *RECONCILERS)
        wait_gateway()
    wait_status(operation_id, "COMPLETED")
    poll(f"SELECT state FROM autocheck.inbox WHERE message_id = {sql_text(message_id)}",
         lambda rows: rows == [{"state": "APPLIED"}])

    # after_manual_decision: the decision, the transition and the successful idempotency result are
    # rolled back together, and the repeat under the same key creates exactly one decision.
    manual_operation, manual = create_and_submit("PAYMENT_APPROVAL", "100000.01")
    step = poll("SELECT step_instance_id FROM autocheck.steps "
                f"WHERE process_id = '{manual['processId']}'::uuid AND step_type = 'MANUAL' AND state = 'WAITING'",
                lambda rows: len(rows) == 1)
    payload = {"processId": manual["processId"], "stepInstanceId": step[0]["step_instance_id"],
               "decision": "APPROVED", "reason": "e2e failpoint repeat"}
    key = f"e2e-manual-{secrets.token_hex(5)}"
    try:
        start("api", recreate=True, environment={"COURSE_FAILPOINT": "after_manual_decision"})
        wait_gateway()
        pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)
        pool.submit(action, "workflow", "manual", payload, REVIEWER, key)
        wait_for_log("api", '"name":"after_manual_decision"')
        check(compose("kill", "api").returncode == 0, "cannot kill the API at the barrier")
        pool.shutdown(wait=False)
        rolled_back = sql_rows(f"SELECT 1 FROM autocheck.decisions WHERE step_instance_id = '{step[0]['step_instance_id']}'::uuid")
        check(rolled_back == [], f"an uncommitted decision must leave nothing behind: {rolled_back}")
    finally:
        start("api", recreate=True)
        wait_gateway()
    status, body = action("workflow", "manual", payload, REVIEWER, key)
    check(status == 200 and body and body["outcome"] == "APPROVED", f"manual repeat: {status} {body}")
    decisions = sql_rows("SELECT source, outcome FROM autocheck.decisions "
                         f"WHERE step_instance_id = '{step[0]['step_instance_id']}'::uuid")
    check(decisions == [{"source": "MANUAL", "outcome": "APPROVED"}],
          f"the repeat must create exactly one decision: {decisions}")
    wait_status(manual_operation, "COMPLETED")


def provider_mode(mode: str) -> None:
    """Recreates the issued simulator in another mode and waits until it answers.

    The simulator keeps its payments in memory, so an audit of anything delivered before this call
    is gone afterwards - every scenario audits its own operations before it returns.
    """
    start("provider-simulator", recreate=True, environment={"PROVIDER_SIMULATOR_MODE": mode})
    deadline = time.monotonic() + 30
    while True:
        try:
            if audit(f"ext-e2e-probe-{secrets.token_hex(4)}")[0] in (200, 404):
                return
        except Failure:
            pass
        check(time.monotonic() < deadline, f"the provider did not come back in mode {mode}")
        time.sleep(0.3)


def scenario_provider_unknown_outcome() -> None:
    """A lost response and a transient error of the provider: retried under one key, one payment.

    ``lost-response`` is the case the whole delivery design exists for: the provider has accepted
    the payment and the dispatcher cannot know it. ``transient-error`` is its harmless twin. In
    both, the number of HTTP attempts may exceed one, the number of payments may not.
    """
    try:
        for mode, amount in (("lost-response", "1083.00"), ("transient-error", "1084.00")):
            provider_mode(mode)
            operation_id, _ = create_and_submit("PAYMENT_EXECUTION", amount)
            external_id = external_request(operation_id)
            wait_status(operation_id, "COMPLETED")
            status, payment = audit(external_id)
            check(status == 200 and payment and payment["paymentCount"] == 1 and payment["idempotencyKey"] == external_id,
                  f"{mode}: exactly one payment under the original key is expected: {status} {payment}")
            outbox = sql_rows("SELECT state, attempt_count FROM autocheck.outbox "
                              f"WHERE external_request_id = {sql_text(external_id)}")
            check(len(outbox) == 1 and outbox[0]["state"] == "CONFIRMED" and 1 <= outbox[0]["attempt_count"] <= 4,
                  f"{mode}: the delivery must be confirmed within the attempt budget: {outbox}")
            receipts = sql_rows(f"SELECT count(*) AS n FROM autocheck.receipts WHERE external_request_id = {sql_text(external_id)}")
            check(receipts[0]["n"] == 1, f"{mode}: one receipt must settle the operation: {receipts}")
            events = sql_rows("SELECT count(*) AS n FROM autocheck.operation_events "
                              f"WHERE operation_id = '{operation_id}'::uuid AND event_type = 'OPERATION_COMPLETED'")
            check(events[0]["n"] == 1, f"{mode}: the operation must complete exactly once: {events}")
    finally:
        provider_mode("success")


def scenario_observer_role() -> None:
    """The observer reads the published projections and nothing else."""
    # Tracked Compose has no default for the observer password. The probes below run inside the
    # postgres container, where the local socket is trusted, so they hold with or without it; the
    # password itself is exercised by the course checker, which connects over TCP from outside.
    password = os.environ.get("COURSE_AUTOCHECK_PASSWORD", "")
    readable = compose("exec", "-T", "-e", f"PGPASSWORD={password}", "postgres", "psql", "-X",
                       "-v", "ON_ERROR_STOP=1", "-U", "autocheck_reader", "-d", "course", "-At",
                       "-c", "SELECT count(*) FROM autocheck.outbox")
    check(readable.returncode == 0, f"the observer must read autocheck views: {readable.stderr[-300:]}")

    for statement in ("TABLE delivery.outbox",
                      "SELECT api.invoke('{}'::jsonb, '{}'::jsonb)",
                      "SELECT delivery.reconcile_inbox(1)",
                      "CREATE TABLE public.observer_probe (value integer)"):
        denied = compose("exec", "-T", "-e", f"PGPASSWORD={password}", "postgres", "psql", "-X",
                         "-v", "ON_ERROR_STOP=1", "-U", "autocheck_reader", "-d", "course", "-c", statement)
        check(denied.returncode != 0, f"the observer must not run: {statement}")

    elevated = sql_rows("SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls "
                        "FROM pg_catalog.pg_roles WHERE rolname = 'autocheck_reader'")
    check(elevated == [{"rolsuper": False, "rolcreaterole": False, "rolcreatedb": False,
                        "rolreplication": False, "rolbypassrls": False}], f"observer attributes: {elevated}")
    memberships = sql_rows("SELECT granted.rolname AS granted_role FROM pg_catalog.pg_auth_members m "
                           "JOIN pg_catalog.pg_roles member ON member.oid = m.member "
                           "JOIN pg_catalog.pg_roles granted ON granted.oid = m.roleid "
                           "WHERE member.rolname = 'autocheck_reader'")
    check(memberships == [], f"the observer must not be a member of any role: {memberships}")


SCENARIOS = [scenario_provider_path, scenario_adapter_and_recreate, scenario_review, scenario_concurrent_submit,
             scenario_least_privilege, scenario_failpoint_and_lease_expiry,
             scenario_failpoint_after_provider_response, scenario_failpoint_after_job_claim,
             scenario_delivery_fencing, scenario_receipt_lock_order,
             scenario_submit_rejects_foreign_process, scenario_storage_invariants,
             scenario_health_and_metrics, scenario_job_queue_metrics,
             scenario_dead_letter_and_late_receipt, scenario_diagnostics_trace,
             scenario_api_failpoints, scenario_observer_role, scenario_provider_unknown_outcome,
             scenario_outbox_retry_budget]


def main() -> int:
    failed = 0
    for scenario in SCENARIOS:
        started = time.monotonic()
        try:
            scenario()
            print(f"PASS {scenario.__name__} ({time.monotonic() - started:.1f}s)")
        except Skipped as reason:
            print(f"SKIP {scenario.__name__}: {reason}")
        except Exception as error:  # noqa: BLE001 - report every scenario
            failed += 1
            print(f"FAIL {scenario.__name__}: {error}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
