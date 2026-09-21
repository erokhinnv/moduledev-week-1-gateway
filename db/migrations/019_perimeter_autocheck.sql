-- 019_perimeter_autocheck.sql
-- Неделя 3: стабильные проекции периметра и раскладка прав Python-ролей.
--
-- Проекции не раскрывают capability, JWT, HMAC secret или подпись, текст callback message,
-- полное тело квитанции и reason ручного решения — только отпечатки и состояния.

CREATE OR REPLACE VIEW autocheck.external_requests AS
SELECT
    e.external_request_id::text AS external_request_id,
    e.operation_id              AS operation_id,
    e.state::text               AS state,
    e.payload_hash::text        AS payload_hash,
    e.created_at                AS created_at
FROM payment.external_request e;

CREATE OR REPLACE VIEW autocheck.receipts AS
SELECT
    r.message_id::text          AS message_id,
    r.external_request_id::text AS external_request_id,
    r.message_version::integer  AS message_version,
    r.outcome::text             AS outcome,
    r.signature_valid           AS signature_valid,
    r.body_hash::text           AS body_hash,
    r.received_at               AS received_at,
    r.applied_at                AS applied_at
FROM payment.receipt r;

CREATE OR REPLACE VIEW autocheck.outbox AS
SELECT
    o.outbox_id                 AS outbox_id,
    o.external_request_id::text AS external_request_id,
    o.state::text               AS state,
    o.attempt_count::integer    AS attempt_count,
    o.next_attempt_at           AS next_attempt_at,
    o.last_error_code::text     AS last_error_code,
    o.created_at                AS created_at,
    o.delivered_at              AS delivered_at
FROM delivery.outbox o;

CREATE OR REPLACE VIEW autocheck.inbox AS
SELECT
    i.message_id::text AS message_id,
    i.body_hash::text  AS body_hash,
    i.state::text      AS state,
    i.received_at      AS received_at,
    i.applied_at       AS applied_at
FROM delivery.inbox i;

CREATE OR REPLACE VIEW autocheck.decisions AS
SELECT
    d.decision_id        AS decision_id,
    d.process_id         AS process_id,
    d.step_instance_id   AS step_instance_id,
    d.source::text       AS source,
    d.principal::text    AS principal,
    d.reason_hash::text  AS reason_hash,
    d.outcome::text      AS outcome,
    d.rule_version::text AS rule_version,
    d.created_at         AS created_at
FROM workflow.decision d;

GRANT SELECT ON autocheck.external_requests TO course_runtime;
GRANT SELECT ON autocheck.receipts          TO course_runtime;
GRANT SELECT ON autocheck.outbox            TO course_runtime;
GRANT SELECT ON autocheck.inbox             TO course_runtime;
GRANT SELECT ON autocheck.decisions         TO course_runtime;

-------------------------------------------------------------------------------
-- Права по умолчанию для PUBLIC отзываются явно во всех схемах с функциями — в том числе в
-- новых. Иначе функция, созданная без ALTER DEFAULT PRIVILEGES, молча стала бы доступна любой
-- роли, включая Python-роли, у которых по контракту ровно одна или три функции.
-------------------------------------------------------------------------------
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA api      FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA payment  FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA payment  FROM PUBLIC;

-- Workflow worker исполняет новые шаги через те же четыре границы: дополнительных прав ему
-- не нужно, а прямого доступа к новым таблицам у него быть не должно.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'workflow_worker') THEN
        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA delivery FROM workflow_worker';
    END IF;
END;
$$;

-------------------------------------------------------------------------------
-- outbox_dispatcher: ровно три функции delivery, ни одной таблицы, ни одной схемы на запись.
-- inbox_reconciler: ровно одна функция delivery.reconcile_inbox.
-------------------------------------------------------------------------------
DO $$
DECLARE
    v_role   text;
    v_schema text;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['outbox_dispatcher', 'inbox_reconciler']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_role) THEN
            RAISE NOTICE '% role is absent, integration grants skipped', v_role;
            CONTINUE;
        END IF;

        FOREACH v_schema IN ARRAY ARRAY['api', 'payment', 'workflow', 'delivery', 'autocheck']
        LOOP
            EXECUTE pg_catalog.format('REVOKE ALL ON ALL TABLES IN SCHEMA %I FROM %I', v_schema, v_role);
            EXECUTE pg_catalog.format('REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA %I FROM %I', v_schema, v_role);
            EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA %I FROM %I', v_schema, v_role);
        END LOOP;

        EXECUTE pg_catalog.format('GRANT USAGE ON SCHEMA delivery TO %I', v_role);
    END LOOP;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'outbox_dispatcher') THEN
        EXECUTE 'GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) TO outbox_dispatcher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) TO outbox_dispatcher';
    END IF;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'inbox_reconciler') THEN
        EXECUTE 'GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO inbox_reconciler';
    END IF;
END;
$$;
