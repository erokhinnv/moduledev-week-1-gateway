-- 015_workflow_autocheck.sql
-- Стабильные проекции состояния workflow и итоговая раскладка прав ролей.
--
-- Физические таблицы workflow остаются приватными: наружу выходят только представления ниже.
-- Их набор колонок и типы — контракт, а порядок колонок и дополнительные диагностические поля
-- контрактом не являются.

CREATE OR REPLACE VIEW autocheck.flow_versions AS
SELECT
    f.flow_name::text     AS flow_name,
    v.version::integer    AS flow_version,
    v.status::text        AS status,
    v.is_active           AS is_active,
    v.published_at        AS published_at,
    v.start_step_key::text AS start_step_key
FROM workflow.flow_version v
JOIN workflow.flow_definition f ON f.flow_id = v.flow_id;

CREATE OR REPLACE VIEW autocheck.processes AS
SELECT
    p.process_id                AS process_id,
    p.business_key::text        AS business_key,
    f.flow_name::text           AS flow_name,
    v.version::integer          AS flow_version,
    p.state::text               AS state,
    p.current_step_key::text    AS current_step_key,
    p.created_at                AS created_at,
    p.updated_at                AS updated_at
FROM workflow.process_instance p
JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
JOIN workflow.flow_version v ON v.flow_version_id = p.flow_version_id;

CREATE OR REPLACE VIEW autocheck.steps AS
SELECT
    s.step_instance_id   AS step_instance_id,
    s.process_id         AS process_id,
    s.step_key::text     AS step_key,
    s.step_type::text    AS step_type,
    s.state::text        AS state,
    s.outcome::text      AS outcome,
    s.entered_at         AS entered_at,
    s.completed_at       AS completed_at
FROM workflow.step_instance s;

CREATE OR REPLACE VIEW autocheck.jobs AS
SELECT
    j.job_id                  AS job_id,
    j.process_id              AS process_id,
    j.step_instance_id        AS step_instance_id,
    j.execution_id            AS execution_id,
    j.state::text             AS state,
    j.lease_owner::text       AS lease_owner,
    j.lease_version::bigint   AS lease_version,
    j.lease_until             AS lease_until,
    j.attempt_count::integer  AS attempt_count,
    j.next_attempt_at         AS next_attempt_at
FROM workflow.workflow_job j;

CREATE OR REPLACE VIEW autocheck.attempts AS
SELECT
    a.attempt_id               AS attempt_id,
    a.job_id                   AS job_id,
    a.execution_id             AS execution_id,
    a.lease_version::bigint    AS lease_version,
    a.attempt_number::integer  AS attempt_number,
    a.status::text             AS status,
    a.outcome::text            AS outcome,
    a.error_code::text         AS error_code,
    a.started_at               AS started_at,
    a.finished_at              AS finished_at
FROM workflow.task_attempt a;

-- Наружу выходит только отпечаток тела сигнала, но не само тело.
CREATE OR REPLACE VIEW autocheck.signals AS
SELECT
    s.message_id::text  AS message_id,
    s.process_id        AS process_id,
    s.signal_type::text AS signal_type,
    s.body_hash::text   AS body_hash,
    s.status::text      AS status,
    s.received_at       AS received_at
FROM workflow.workflow_signal s;

CREATE OR REPLACE VIEW autocheck.workflow_events AS
SELECT
    e.event_id          AS event_id,
    e.process_id        AS process_id,
    e.step_instance_id  AS step_instance_id,
    e.event_type::text  AS event_type,
    e.occurred_at       AS occurred_at
FROM workflow.workflow_event e;

-------------------------------------------------------------------------------
-- Права ролей
--
-- Отдельная роль workflow_worker существует ровно для того, чтобы «сменный» исполнитель имел
-- минимально возможные права: четыре функции-границы и ни одного прямого DML. Даже полностью
-- скомпрометированный worker не может ни продвинуть процесс мимо fencing, ни переписать историю,
-- ни тронуть предметные таблицы.
-------------------------------------------------------------------------------

-- Права по умолчанию для PUBLIC на функции обеих технических схем отзываются явно: полагаться
-- на то, что ALTER DEFAULT PRIVILEGES был выставлен когда-то при инициализации кластера, для
-- границы доверия недостаточно.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA api FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA workflow FROM PUBLIC;

-- USAGE на схему workflow runtime-роли НЕ выдаётся: представления autocheck исполняются от
-- имени своего владельца, поэтому для чтения проекций доступ к нижележащей схеме не нужен.
GRANT SELECT ON autocheck.flow_versions   TO course_runtime;
GRANT SELECT ON autocheck.processes       TO course_runtime;
GRANT SELECT ON autocheck.steps           TO course_runtime;
GRANT SELECT ON autocheck.jobs            TO course_runtime;
GRANT SELECT ON autocheck.attempts        TO course_runtime;
GRANT SELECT ON autocheck.signals         TO course_runtime;
GRANT SELECT ON autocheck.workflow_events TO course_runtime;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'workflow_worker') THEN
        -- Ровно четыре границы: захват задания, вызов действия, успешное и неуспешное завершение.
        EXECUTE 'GRANT USAGE ON SCHEMA api TO workflow_worker';
        EXECUTE 'GRANT USAGE ON SCHEMA workflow TO workflow_worker';
        EXECUTE 'GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO workflow_worker';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer, integer) TO workflow_worker';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb, jsonb) TO workflow_worker';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean, jsonb) TO workflow_worker';
        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA api FROM workflow_worker';
        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA workflow FROM workflow_worker';
        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA autocheck FROM workflow_worker';
        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA payment FROM workflow_worker';
    ELSE
        RAISE NOTICE 'workflow_worker role is absent, worker grants skipped';
    END IF;
END;
$$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_publisher') THEN
        -- Роль ПУБЛИКАЦИИ управляет только определениями: каталогом действий и картами.
        -- Ни запуска процессов, ни сигналов, ни завершения заданий она не может — иначе
        -- компрометация учётных данных публикации давала бы управление исполнением.
        EXECUTE 'GRANT USAGE ON SCHEMA workflow TO course_publisher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.publication_facts(jsonb) TO course_publisher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.publish_flow(jsonb) TO course_publisher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.activate_flow(text, integer) TO course_publisher';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.list_flows() TO course_publisher';
    END IF;
END;
$$;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_operator') THEN
        -- Роль ОПЕРАЦИЙ двигает процессы, но не меняет определения: опубликовать или
        -- активировать карту она не может. Обратное разделение к роли публикации.
        EXECUTE 'GRANT USAGE ON SCHEMA workflow TO course_operator';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.start_process(text, text, jsonb) TO course_operator';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.accept_signal(uuid, text, text, jsonb) TO course_operator';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.describe_process_envelope(uuid) TO course_operator';
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.list_flows() TO course_operator';
        -- Проба fencing вызывает ту же границу завершения, что и worker, но сама доступна
        -- только при серверном признаке test profile (см. workflow.test_finish_job).
        EXECUTE 'GRANT EXECUTE ON FUNCTION workflow.test_finish_job(uuid, text, bigint, text, jsonb) TO course_operator';
    ELSE
        RAISE NOTICE 'course_operator role is absent, operator grants skipped';
    END IF;
END;
$$;
