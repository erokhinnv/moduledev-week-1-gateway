-- 002_catalog.sql
-- Неизменяемый каталог действий, транзакционное хранилище идемпотентности и технический след
-- вызовов (dispatch evidence).

-- Таблица опубликованных версий действий. Это и есть "неизменяемый каталог" — единственное
-- место, откуда runtime узнаёт, какую именно SQL-функцию вызывать для конкретной пары
-- module+action+version, с какими правами и по какой схеме проверять запрос/ответ.
CREATE TABLE IF NOT EXISTS api.action_version (
    action_version_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    module            text NOT NULL CHECK (module ~ '^[a-z][a-z0-9_]{0,62}$'),
    action            text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_]{0,62}$'),
    version           integer NOT NULL CHECK (version >= 1),
    http_method       text NOT NULL CHECK (http_method = 'POST'),
    target_schema     text NOT NULL CHECK (target_schema ~ '^[a-z][a-z0-9_]{0,62}$'),
    target_function   text NOT NULL CHECK (target_function ~ '^[a-z][a-z0-9_]{0,62}$'),
    request_schema    jsonb NOT NULL,
    response_schema   jsonb NOT NULL,
    outcomes          jsonb NOT NULL CHECK (jsonb_typeof(outcomes) = 'array'),
    required_policy   jsonb NOT NULL CHECK (jsonb_typeof(required_policy) = 'array'),
    idempotency_mode  text NOT NULL CHECK (idempotency_mode IN ('none', 'optional', 'required')),
    idempotency_scope text NOT NULL CHECK (idempotency_scope IN ('none', 'principal_action', 'consumer_action', 'global_action')),
    timeout_ms        integer NOT NULL CHECK (timeout_ms BETWEEN 1 AND 30000),
    manifest          jsonb NOT NULL,
    manifest_hash     text NOT NULL,
    -- enabled и is_default — это ОПЕРАЦИОННОЕ состояние, единственное, что разрешено менять
    -- после публикации версии (через команды CLI activate/disable). Всё остальное в этой
    -- строке — контрактные поля, неизменяемые после публикации (см. триггер ниже).
    enabled           boolean NOT NULL DEFAULT false,
    is_default        boolean NOT NULL DEFAULT false,
    published_at      timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT action_version_unique UNIQUE (module, action, version),
    CONSTRAINT action_default_requires_enabled CHECK (NOT is_default OR enabled)
);

COMMENT ON TABLE api.action_version IS 'Published action manifests. Contract columns are immutable, enabled/is_default are operational state.';

-- Гарантия "ровно одна default-версия на включённый маршрут" обеспечивается САМОЙ PostgreSQL —
-- через частичный уникальный индекс (partial unique index), а не проверкой в коде приложения.
-- Индекс покрывает только строки с is_default = true, поэтому попытка сделать вторую версию
-- одного и того же маршрута версией по умолчанию физически упадёт с ошибкой нарушения уникальности.
CREATE UNIQUE INDEX IF NOT EXISTS action_version_single_default
    ON api.action_version (module, action)
    WHERE is_default;

CREATE INDEX IF NOT EXISTS action_version_route
    ON api.action_version (module, action, version);

-- Опубликованная версия НЕИЗМЕНЯЕМА: этот триггер разрешает менять только операционные колонки
-- (enabled, is_default, updated_at), а любая попытка изменить контрактные поля (например
-- request_schema или target_function уже опубликованной версии) приводит к ошибке.
CREATE OR REPLACE FUNCTION api.action_version_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (NEW.module, NEW.action, NEW.version, NEW.http_method, NEW.target_schema,
        NEW.target_function, NEW.request_schema, NEW.response_schema, NEW.outcomes,
        NEW.required_policy, NEW.idempotency_mode, NEW.idempotency_scope,
        NEW.timeout_ms, NEW.manifest_hash, NEW.published_at)
       IS DISTINCT FROM
       (OLD.module, OLD.action, OLD.version, OLD.http_method, OLD.target_schema,
        OLD.target_function, OLD.request_schema, OLD.response_schema, OLD.outcomes,
        OLD.required_policy, OLD.idempotency_mode, OLD.idempotency_scope,
        OLD.timeout_ms, OLD.manifest_hash, OLD.published_at)
    THEN
        RAISE EXCEPTION 'published action version is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS action_version_immutable ON api.action_version;
CREATE TRIGGER action_version_immutable
    BEFORE UPDATE ON api.action_version
    FOR EACH ROW EXECUTE FUNCTION api.action_version_immutable();

-- Опубликованную версию действия нельзя удалить вообще, ни при каких условиях — история
-- каталога сохраняется навсегда, версия может быть только выключена (enabled = false).
CREATE OR REPLACE FUNCTION api.action_version_no_delete()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'published action version cannot be deleted'
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS action_version_no_delete ON api.action_version;
CREATE TRIGGER action_version_no_delete
    BEFORE DELETE ON api.action_version
    FOR EACH ROW EXECUTE FUNCTION api.action_version_no_delete();

-- Транзакционное хранилище идемпотентности. Уникальное ограничение (constraint) на комбинацию
-- (scope, scope_key, module, action, request_id) — ЕДИНСТВЕННЫЙ примитив конкурентности здесь:
-- если два параллельных запроса с одинаковым ключом идемпотентности пытаются вставить строку
-- одновременно, PostgreSQL сама заставит одну из транзакций подождать другую (заблокироваться на
-- строке), вместо того чтобы полагаться на блокировку внутри процесса C# (которая не защитила бы
-- от параллельных запросов, обслуживаемых разными процессами/репликами api).
CREATE TABLE IF NOT EXISTS api.idempotency_record (
    idempotency_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    scope          text NOT NULL,
    scope_key      text NOT NULL,
    module         text NOT NULL,
    action         text NOT NULL,
    request_id     text NOT NULL,
    payload_hash   text NOT NULL,
    status         text NOT NULL CHECK (status IN ('IN_PROGRESS', 'OK')),
    response       jsonb,
    action_version integer,
    created_at     timestamptz NOT NULL DEFAULT now(),
    completed_at   timestamptz,
    CONSTRAINT idempotency_record_unique UNIQUE (scope, scope_key, module, action, request_id)
);

-- Технический след вызовов (dispatch evidence). Пишется на отдельном (автономном) соединении,
-- вне предметной транзакции — поэтому даже если предметный эффект был откачен (rollback), сам
-- факт вызова и его технический результат остаются наблюдаемыми для диагностики и проверки.
CREATE TABLE IF NOT EXISTS api.dispatch (
    dispatch_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    correlation_id uuid NOT NULL,
    request_id     text,
    module         text NOT NULL,
    action         text NOT NULL,
    version        integer,
    principal      text,
    consumer       text,
    payload_hash   text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    status         text NOT NULL CHECK (status IN ('OK', 'ERROR')),
    outcome        text,
    error_code     text,
    duration_ms    integer,
    occurred_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS dispatch_route ON api.dispatch (module, action, occurred_at DESC);
CREATE INDEX IF NOT EXISTS dispatch_request ON api.dispatch (request_id);

-- Технический след — это история "только на добавление" (append only): запись о вызове нельзя
-- ни изменить, ни удалить, даже владельцу объекта course_owner.
CREATE OR REPLACE FUNCTION api.dispatch_no_change()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'dispatch evidence is append only'
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS dispatch_append_only ON api.dispatch;
CREATE TRIGGER dispatch_append_only
    BEFORE UPDATE OR DELETE ON api.dispatch
    FOR EACH ROW EXECUTE FUNCTION api.dispatch_no_change();
