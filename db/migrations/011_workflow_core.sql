-- 011_workflow_core.sql
-- Схема workflow: определения версионированных карт и персистентное состояние их выполнения.
--
--   Определение (immutable)        Состояние выполнения
--   -----------------------        --------------------------------------------------------
--   flow_definition                process_instance
--   flow_version                   step_instance
--   step_definition                workflow_job
--   transition_definition          task_attempt
--   task_definition                workflow_signal, workflow_event
--
-- Ни одна таблица ниже не доступна worker'у напрямую: единственная граница — три функции
-- workflow.claim_jobs / workflow.finish_job / workflow.fail_job плюс api.invoke (см. 014).

CREATE SCHEMA IF NOT EXISTS workflow;

COMMENT ON SCHEMA workflow IS 'Versioned workflow definitions and persistent process state';

-------------------------------------------------------------------------------
-- Серверный (доверенный) principal исполнителя.
--
-- Worker никогда не берёт scopes из карты или из внешнего запроса: он предъявляет api.invoke
-- ровно тот набор прав, который зарегистрирован здесь. Валидатор карты сверяется с этой же
-- строкой — то есть недостаточность прав обнаруживается при публикации, а не во время сбоя.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.worker_principal (
    principal text PRIMARY KEY,
    consumer  text NOT NULL,
    scopes    jsonb NOT NULL CHECK (jsonb_typeof(scopes) = 'array')
);

INSERT INTO workflow.worker_principal (principal, consumer, scopes)
VALUES ('workflow-worker', 'workflow-engine', '["workflow:execute", "workflow:read"]'::jsonb)
ON CONFLICT (principal) DO NOTHING;

-------------------------------------------------------------------------------
-- Признак test profile, известный САМОЙ базе.
--
-- Проба fencing (flow test-finish) обходит исполнителя и завершает чужое задание. Ограничивать
-- её переменной окружения клиента бессмысленно: граница доверия проходит по базе, а не по тому,
-- какой процесс запустил psql. Флаг проставляется командой bootstrap из COURSE_TEST_PROFILE и
-- проверяется внутри самой функции-пробы.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.runtime_setting (
    singleton    boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    test_profile boolean NOT NULL DEFAULT false,
    -- Абсолютный потолок попыток одного задания: max(max_attempts * factor, minimum). Бюджет
    -- max_attempts тратят только отказы действия, поэтому сбой, не доходящий до fail_job,
    -- иначе перезахватывался бы вечно. Значения вынесены сюда, а не зашиты в функцию: это
    -- настройка эксплуатации, и её должно быть видно, не читая plpgsql.
    attempt_ceiling_factor  integer NOT NULL DEFAULT 3 CHECK (attempt_ceiling_factor >= 1),
    attempt_ceiling_minimum integer NOT NULL DEFAULT 10 CHECK (attempt_ceiling_minimum >= 1),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

INSERT INTO workflow.runtime_setting (singleton, test_profile)
VALUES (true, false)
ON CONFLICT (singleton) DO NOTHING;

-------------------------------------------------------------------------------
-- Определение процесса
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.flow_definition (
    flow_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_name  text NOT NULL UNIQUE CHECK (flow_name ~ '^[a-z][a-z0-9_-]{0,62}$'),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Опубликованная версия карты. Контрактные колонки неизменяемы (триггер ниже); операционным
-- состоянием является только is_active, и «ровно одна активная версия маршрута» держится
-- частичным уникальным индексом PostgreSQL, а не проверкой в C#.
CREATE TABLE IF NOT EXISTS workflow.flow_version (
    flow_version_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_id         uuid NOT NULL REFERENCES workflow.flow_definition (flow_id),
    version         integer NOT NULL CHECK (version >= 1),
    status          text NOT NULL DEFAULT 'PUBLISHED' CHECK (status = 'PUBLISHED'),
    start_step_key  text NOT NULL,
    map             jsonb NOT NULL,
    map_hash        text NOT NULL CHECK (map_hash ~ '^[0-9a-f]{64}$'),
    is_active       boolean NOT NULL DEFAULT false,
    published_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT flow_version_unique UNIQUE (flow_id, version)
);

CREATE UNIQUE INDEX IF NOT EXISTS flow_version_single_active
    ON workflow.flow_version (flow_id)
    WHERE is_active;

CREATE OR REPLACE FUNCTION workflow.flow_version_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF (NEW.flow_version_id, NEW.flow_id, NEW.version, NEW.status, NEW.start_step_key,
        NEW.map, NEW.map_hash, NEW.published_at)
       IS DISTINCT FROM
       (OLD.flow_version_id, OLD.flow_id, OLD.version, OLD.status, OLD.start_step_key,
        OLD.map, OLD.map_hash, OLD.published_at)
    THEN
        RAISE EXCEPTION 'published flow version is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS flow_version_immutable ON workflow.flow_version;
CREATE TRIGGER flow_version_immutable
    BEFORE UPDATE ON workflow.flow_version
    FOR EACH ROW EXECUTE FUNCTION workflow.flow_version_immutable();

CREATE OR REPLACE FUNCTION workflow.definition_no_delete()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    RAISE EXCEPTION 'published workflow definitions cannot be deleted'
        USING ERRCODE = 'restrict_violation';
END;
$fn$;

DROP TRIGGER IF EXISTS flow_version_no_delete ON workflow.flow_version;
CREATE TRIGGER flow_version_no_delete
    BEFORE DELETE ON workflow.flow_version
    FOR EACH ROW EXECUTE FUNCTION workflow.definition_no_delete();

CREATE TABLE IF NOT EXISTS workflow.step_definition (
    step_definition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_version_id    uuid NOT NULL REFERENCES workflow.flow_version (flow_version_id),
    step_key           text NOT NULL,
    step_type          text NOT NULL CHECK (step_type IN ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')),
    signal_type        text,
    outcome            text,
    allowed_outcomes   jsonb NOT NULL DEFAULT '[]'::jsonb,
    CONSTRAINT step_definition_unique UNIQUE (flow_version_id, step_key)
);

DROP TRIGGER IF EXISTS step_definition_no_delete ON workflow.step_definition;
CREATE TRIGGER step_definition_no_delete
    BEFORE DELETE ON workflow.step_definition
    FOR EACH ROW EXECUTE FUNCTION workflow.definition_no_delete();

-- Переход задаётся конечным outcome. Уникальность (версия, шаг, outcome) физически запрещает
-- две ветки для одного исхода — даже если бы валидатор такую карту пропустил.
CREATE TABLE IF NOT EXISTS workflow.transition_definition (
    transition_id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_version_id uuid NOT NULL REFERENCES workflow.flow_version (flow_version_id),
    from_step_key   text NOT NULL,
    outcome         text NOT NULL,
    to_step_key     text NOT NULL,
    CONSTRAINT transition_definition_unique UNIQUE (flow_version_id, from_step_key, outcome)
);

DROP TRIGGER IF EXISTS transition_definition_no_delete ON workflow.transition_definition;
CREATE TRIGGER transition_definition_no_delete
    BEFORE DELETE ON workflow.transition_definition
    FOR EACH ROW EXECUTE FUNCTION workflow.definition_no_delete();

-- Закреплённый контракт вызова действия: версия, policy, бюджет времени, retry и mapping.
CREATE TABLE IF NOT EXISTS workflow.task_definition (
    task_definition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    step_definition_id uuid NOT NULL UNIQUE REFERENCES workflow.step_definition (step_definition_id),
    service            text NOT NULL CHECK (service = 'postgres'),
    module             text NOT NULL CHECK (module ~ '^[a-z][a-z0-9_]{0,62}$'),
    action             text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_]{0,62}$'),
    action_version     integer NOT NULL CHECK (action_version >= 1),
    required_policy    jsonb NOT NULL CHECK (jsonb_typeof(required_policy) = 'array'),
    timeout_ms         integer NOT NULL CHECK (timeout_ms BETWEEN 1 AND 30000),
    max_attempts       integer NOT NULL CHECK (max_attempts BETWEEN 1 AND 10),
    delays_ms          jsonb NOT NULL CHECK (jsonb_typeof(delays_ms) = 'array'),
    input_mapping      jsonb NOT NULL CHECK (jsonb_typeof(input_mapping) = 'object'),
    input_constants    jsonb NOT NULL CHECK (jsonb_typeof(input_constants) = 'object'),
    CONSTRAINT task_definition_retry_budget CHECK (jsonb_array_length(delays_ms) = max_attempts - 1)
);

DROP TRIGGER IF EXISTS task_definition_no_delete ON workflow.task_definition;
CREATE TRIGGER task_definition_no_delete
    BEFORE DELETE ON workflow.task_definition
    FOR EACH ROW EXECUTE FUNCTION workflow.definition_no_delete();

-------------------------------------------------------------------------------
-- Состояние выполнения
-------------------------------------------------------------------------------

-- Экземпляр процесса НАВСЕГДА закреплён за flow_version_id: последующий activate другой версии
-- меняет только выбор для новых экземпляров и не может «перевести» уже запущенный процесс.
CREATE TABLE IF NOT EXISTS workflow.process_instance (
    process_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_id          uuid NOT NULL REFERENCES workflow.flow_definition (flow_id),
    flow_version_id  uuid NOT NULL REFERENCES workflow.flow_version (flow_version_id),
    business_key     text NOT NULL CHECK (length(business_key) BETWEEN 1 AND 200),
    data             jsonb NOT NULL CHECK (jsonb_typeof(data) = 'object'),
    data_hash        text NOT NULL CHECK (data_hash ~ '^[0-9a-f]{64}$'),
    state            text NOT NULL CHECK (state IN ('CREATED', 'RUNNING', 'WAITING_SIGNAL', 'WAITING_MANUAL', 'COMPLETED', 'FAILED')),
    current_step_key text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT process_business_key_unique UNIQUE (flow_id, business_key)
);

CREATE INDEX IF NOT EXISTS process_instance_state ON workflow.process_instance (state);

-- Карты ациклические, поэтому один шаг посещается не более одного раза за процесс: уникальность
-- (process_id, step_key) превращает это свойство графа в гарантию хранилища.
CREATE TABLE IF NOT EXISTS workflow.step_instance (
    step_instance_id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id         uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    step_definition_id uuid NOT NULL REFERENCES workflow.step_definition (step_definition_id),
    step_key           text NOT NULL,
    step_type          text NOT NULL CHECK (step_type IN ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')),
    state              text NOT NULL CHECK (state IN ('PENDING', 'READY', 'RUNNING', 'WAITING', 'COMPLETED', 'FAILED')),
    outcome            text,
    entered_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at       timestamptz,
    CONSTRAINT step_instance_unique UNIQUE (process_id, step_key)
);

CREATE INDEX IF NOT EXISTS step_instance_process ON workflow.step_instance (process_id, entered_at);

-- Одна логическая работа одного automatic-шага. execution_id — стабильный ключ идемпотентности
-- предметного эффекта: он не меняется ни при retry, ни при reclaim, поэтому повторный вызов
-- зарегистрированного действия не может создать второй эффект.
CREATE TABLE IF NOT EXISTS workflow.workflow_job (
    job_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id       uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    step_instance_id uuid NOT NULL UNIQUE REFERENCES workflow.step_instance (step_instance_id),
    execution_id     uuid NOT NULL DEFAULT gen_random_uuid(),
    state            text NOT NULL CHECK (state IN ('READY', 'LEASED', 'RETRY_WAIT', 'SUCCEEDED', 'DEAD')),
    lease_owner      text,
    lease_version    bigint NOT NULL DEFAULT 0 CHECK (lease_version >= 0),
    lease_until      timestamptz,
    attempt_count    integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    failure_count    integer NOT NULL DEFAULT 0 CHECK (failure_count >= 0),
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    -- Почему задание умерло. Две причины смерти наблюдаемо разные: исчерпанный бюджет отказов
    -- действия и абсолютный потолок попыток. Смешивать их в одном DEAD означало бы прятать
    -- разницу между «действие стабильно отказывает» и «задание вообще не доходит до отчёта».
    dead_reason      text CHECK (dead_reason IN ('failure_budget', 'attempt_ceiling')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workflow_job_dead_reason CHECK (state <> 'DEAD' OR dead_reason IS NOT NULL)
);

-- Индексы очереди: ровно те предикаты, по которым читает короткая транзакция claim.
CREATE INDEX IF NOT EXISTS workflow_job_queue
    ON workflow.workflow_job (next_attempt_at)
    WHERE state IN ('READY', 'RETRY_WAIT');

CREATE INDEX IF NOT EXISTS workflow_job_lease
    ON workflow.workflow_job (lease_until)
    WHERE state = 'LEASED';

CREATE INDEX IF NOT EXISTS workflow_job_process ON workflow.workflow_job (process_id);

-- Попытка выполнения. Номер попытки растёт вместе с attempt_count, lease_version — вместе с
-- правом на завершение. Строка попытки только дополняется: изменить её можно ровно один раз,
-- переведя из RUNNING в конечный статус, а удалить нельзя вообще.
CREATE TABLE IF NOT EXISTS workflow.task_attempt (
    attempt_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id         uuid NOT NULL REFERENCES workflow.workflow_job (job_id),
    execution_id   uuid NOT NULL,
    lease_version  bigint NOT NULL,
    attempt_number integer NOT NULL CHECK (attempt_number >= 1),
    status         text NOT NULL CHECK (status IN ('RUNNING', 'SUCCEEDED', 'FAILED', 'STALE')),
    outcome        text,
    error_code     text,
    started_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    finished_at    timestamptz,
    CONSTRAINT task_attempt_unique UNIQUE (job_id, attempt_number),
    CONSTRAINT task_attempt_lease_unique UNIQUE (job_id, lease_version)
);

CREATE INDEX IF NOT EXISTS task_attempt_job ON workflow.task_attempt (job_id, attempt_number);

CREATE OR REPLACE FUNCTION workflow.task_attempt_append_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'task attempts are append only'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF OLD.status <> 'RUNNING' THEN
        RAISE EXCEPTION 'a finished task attempt is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.attempt_id, NEW.job_id, NEW.execution_id, NEW.lease_version, NEW.attempt_number, NEW.started_at)
       IS DISTINCT FROM
       (OLD.attempt_id, OLD.job_id, OLD.execution_id, OLD.lease_version, OLD.attempt_number, OLD.started_at)
    THEN
        RAISE EXCEPTION 'task attempt identity is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS task_attempt_append_only ON workflow.task_attempt;
CREATE TRIGGER task_attempt_append_only
    BEFORE UPDATE OR DELETE ON workflow.task_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow.task_attempt_append_only();

-- message_id глобально уникален по контракту недели: он и есть первичный ключ.
CREATE TABLE IF NOT EXISTS workflow.workflow_signal (
    message_id  text PRIMARY KEY CHECK (length(message_id) BETWEEN 1 AND 200),
    process_id  uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    signal_type text NOT NULL,
    body        jsonb NOT NULL,
    body_hash   text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    status      text NOT NULL CHECK (status IN ('ACCEPTED', 'APPLIED')),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at  timestamptz
);

CREATE INDEX IF NOT EXISTS workflow_signal_pending
    ON workflow.workflow_signal (process_id, signal_type, received_at)
    WHERE status = 'ACCEPTED';

-- История переходов только дополняется: ни UPDATE, ни DELETE не разрешены никому, включая владельца.
CREATE TABLE IF NOT EXISTS workflow.workflow_event (
    event_id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id       uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    step_instance_id uuid REFERENCES workflow.step_instance (step_instance_id),
    event_type       text NOT NULL CHECK (event_type ~ '^[A-Z][A-Za-z0-9]{0,62}$'),
    payload          jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    sequence_no      bigint GENERATED ALWAYS AS IDENTITY
);

CREATE INDEX IF NOT EXISTS workflow_event_process ON workflow.workflow_event (process_id, sequence_no);

CREATE OR REPLACE FUNCTION workflow.workflow_event_append_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    RAISE EXCEPTION 'workflow history is append only'
        USING ERRCODE = 'restrict_violation';
END;
$fn$;

DROP TRIGGER IF EXISTS workflow_event_append_only ON workflow.workflow_event;
CREATE TRIGGER workflow_event_append_only
    BEFORE UPDATE OR DELETE ON workflow.workflow_event
    FOR EACH ROW EXECUTE FUNCTION workflow.workflow_event_append_only();
