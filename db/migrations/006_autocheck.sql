-- 006_autocheck.sql
-- Read-only (только для чтения) проекции для чёрного ящика автопроверки и итоговая раскладка прав.
-- Физические таблицы остаются полностью приватными: устойчивой (стабильной) границей наружу
-- служат именно эти представления (views) плюс права ролей — а не таблицы или ORM-модели.

CREATE OR REPLACE VIEW autocheck.contract_info AS
SELECT
    'course-1'::text AS contract_version,
    now()            AS generated_at;

CREATE OR REPLACE VIEW autocheck.action_definitions AS
SELECT
    v.module::text          AS module,
    v.action::text          AS action,
    v.version::integer      AS version,
    v.http_method::text     AS http_method,
    v.target_schema::text   AS target_schema,
    v.target_function::text AS target_function,
    v.outcomes::jsonb       AS outcomes,
    v.enabled               AS enabled,
    v.is_default            AS is_default
FROM api.action_version v;

CREATE OR REPLACE VIEW autocheck.action_dispatches AS
SELECT
    d.correlation_id     AS correlation_id,
    d.request_id::text   AS request_id,
    d.module::text       AS module,
    d.action::text       AS action,
    d.version::integer   AS version,
    d.principal::text    AS principal,
    d.payload_hash::text AS payload_hash,
    d.status::text       AS status,
    d.outcome::text      AS outcome,
    d.occurred_at        AS occurred_at
FROM api.dispatch d;

CREATE OR REPLACE VIEW autocheck.operations AS
SELECT
    o.operation_id          AS operation_id,
    o.request_id::text      AS request_id,
    o.operation_kind::text  AS operation_kind,
    o.amount                AS amount,
    o.currency::text        AS currency,
    o.status::text          AS status,
    o.process_id            AS process_id,
    o.created_at            AS created_at,
    o.updated_at            AS updated_at
FROM payment.operation o;

CREATE OR REPLACE VIEW autocheck.operation_events AS
SELECT
    e.event_id           AS event_id,
    e.operation_id       AS operation_id,
    e.event_type::text   AS event_type,
    e.payload_hash::text AS payload_hash,
    e.occurred_at        AS occurred_at
FROM payment.operation_event e;

-- Доказательство готовности: сама функция запускается от лица runtime-роли (через SECURITY
-- DEFINER она получает право прочитать autocheck.contract_info), и её успешное выполнение
-- одновременно доказывает, что runtime-роль реально может достучаться до базы данных И что
-- миграции уже накатаны (иначе строка contract_info просто не существовала бы).
CREATE OR REPLACE FUNCTION api.readiness()
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'contractVersion', (SELECT c.contract_version FROM autocheck.contract_info c LIMIT 1),
        'publishedActions', (SELECT count(*) FROM api.action_version WHERE enabled)
    );
$$;

-------------------------------------------------------------------------------
-- Права ролей (privileges)
--
-- course_runtime может выполнять только границу invoke и читать read-only проекции. Она НЕ
-- получает никаких прав напрямую на физические таблицы (ни в схеме api, ни в схеме payment,
-- ни даже в самой autocheck) — весь доступ идёт только через функции с SECURITY DEFINER и через
-- явно перечисленные представления.
-------------------------------------------------------------------------------
GRANT USAGE ON SCHEMA api TO course_runtime;
GRANT USAGE ON SCHEMA autocheck TO course_runtime, course_migrator;

-- Явный отзыв прав на все таблицы, даже если какие-то права могли бы быть унаследованы иначе —
-- чтобы не полагаться на права "по умолчанию" и явно зафиксировать нулевой прямой доступ.
REVOKE ALL ON ALL TABLES IN SCHEMA api FROM course_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA payment FROM course_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA autocheck FROM course_runtime;

-- Единственный разрешённый прямой доступ runtime-роли к данным — это SELECT на пять
-- проверочных представлений (и то они сами не раскрывают ничего лишнего, см. их определения выше).
GRANT SELECT ON autocheck.contract_info      TO course_runtime;
GRANT SELECT ON autocheck.action_definitions TO course_runtime;
GRANT SELECT ON autocheck.action_dispatches  TO course_runtime;
GRANT SELECT ON autocheck.operations         TO course_runtime;
GRANT SELECT ON autocheck.operation_events   TO course_runtime;

GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.resolve_action(text, text, integer) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.describe_action(text, text, integer) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.default_actions() TO course_runtime;
GRANT EXECUTE ON FUNCTION api.record_dispatch(jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.readiness() TO course_runtime;

-- Роль публикации (course_migrator) управляет ТОЛЬКО каталогом действий — у неё нет и не может
-- быть прав на выполнение api.invoke, то есть она физически не может запустить ни одно
-- предметное действие, только публиковать/включать/выключать их описания.
GRANT EXECUTE ON FUNCTION api.publish_action(jsonb) TO course_migrator;
GRANT EXECUTE ON FUNCTION api.activate_action(text, text, integer) TO course_migrator;
GRANT EXECUTE ON FUNCTION api.disable_action(text, text, integer, integer) TO course_migrator;
GRANT EXECUTE ON FUNCTION api.list_actions() TO course_migrator;
GRANT EXECUTE ON FUNCTION api.resolve_action(text, text, integer) TO course_migrator;
