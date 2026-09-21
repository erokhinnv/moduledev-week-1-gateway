#!/usr/bin/env python3
"""End-to-end scenarios of the week-3 perimeter against a running Compose stack.

    eval "$(python3 scripts/dev_env.py)"
    docker compose -p course-e2e up -d --build
    COMPOSE_PROJECT=course-e2e python3 scripts/e2e_perimeter.py

Evidence is read only through HTTP actions, provider audit and the read-only ``autocheck``
views - the same seams the course checker uses. Exit code 0 means every scenario passed.
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

PROJECT = os.environ.get("COMPOSE_PROJECT", "moduledev-week-3")
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


class Failure(AssertionError):
    pass


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
    check(compose("stop", "outbox-dispatcher").returncode == 0, "cannot stop dispatcher")
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
        check(compose("up", "-d", "--no-build", "outbox-dispatcher").returncode == 0, "cannot start dispatcher")

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
    check(compose("stop", "outbox-dispatcher", "inbox-reconciler").returncode == 0, "cannot stop services")
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

    receipt = {"externalRequestId": external_id, "messageId": legacy["providerPaymentId"], "occurredAt": legacy["occurredAt"],
               "outcome": "REJECTED", "providerPaymentId": legacy["providerPaymentId"], "version": 1}
    rows = poll(f"SELECT state, body_hash FROM autocheck.inbox WHERE message_id = {sql_text(legacy['providerPaymentId'])}",
                lambda rows: len(rows) == 1)
    check(rows[0]["state"] == "RECEIVED" and rows[0]["body_hash"] == hashlib.sha256(compact(receipt)).hexdigest(),
          f"inbox must wait for the reconciler with exact body hash: {rows}")

    check(compose("up", "-d", "--no-build", "--force-recreate", "outbox-dispatcher", "receipt-adapter",
                  "inbox-reconciler").returncode == 0, "cannot recreate python services")
    wait_status(operation_id, "REJECTED")
    poll(f"SELECT state FROM autocheck.inbox WHERE message_id = {sql_text(legacy['providerPaymentId'])}",
         lambda rows: rows[0]["state"] == "APPLIED")
    time.sleep(1.5)
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


SCENARIOS = [scenario_provider_path, scenario_adapter_and_recreate, scenario_review, scenario_concurrent_submit,
             scenario_least_privilege]


def main() -> int:
    failed = 0
    for scenario in SCENARIOS:
        started = time.monotonic()
        try:
            scenario()
            print(f"PASS {scenario.__name__} ({time.monotonic() - started:.1f}s)")
        except Exception as error:  # noqa: BLE001 - report every scenario
            failed += 1
            print(f"FAIL {scenario.__name__}: {error}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
