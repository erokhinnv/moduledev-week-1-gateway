-- 017_perimeter_core.sql
-- Неделя 3: авторитетное состояние интеграционного периметра.
--
--   payment.flow_binding       operationKind -> карта процесса (server-side, клиент её не выбирает)
--   payment.limit_rule         правило course-limit-v1
--   payment.external_request   один внешний запрос на операцию
--   delivery.outbox            очередь доставки во внешний provider
--   delivery.inbox             принятые внешние сообщения (глобальный messageId)
--   payment.receipt            квитанции receipt v1, связанные с operation/process
--   workflow.decision          аудит автоматических и ручных решений
--
-- Python-процессы не получают ни одного права на эти таблицы: единственная их граница —
-- функции delivery.* из 018 (см. раскладку прав в 019).

CREATE SCHEMA IF NOT EXISTS delivery;

COMMENT ON SCHEMA delivery IS 'Durable Outbox/Inbox of the integration perimeter';

-- Отзыв прав PUBLIC делается по месту — по той же причине, что и в 016.
REVOKE ALL ON SCHEMA delivery FROM PUBLIC;

-------------------------------------------------------------------------------
-- Доверенный principal исполнителя получает внутреннюю policy платёжных шагов. Клиентские
-- токены этой policy не имеют, поэтому шаги процесса нельзя вызвать через публичный маршрут.
-------------------------------------------------------------------------------
UPDATE workflow.worker_principal
SET scopes = scopes || '["payment:internal"]'::jsonb
WHERE principal = 'workflow-worker'
  AND NOT scopes ? 'payment:internal';

-------------------------------------------------------------------------------
-- Серверная привязка вида операции к карте. Клиент передаёт в payment.submit только
-- operationId: имя карты, её версия и правило выбора ему недоступны.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS payment.flow_binding (
    operation_kind text PRIMARY KEY CHECK (operation_kind IN ('PAYMENT_EXECUTION', 'PAYMENT_APPROVAL')),
    flow_name      text NOT NULL CHECK (flow_name ~ '^[a-z][a-z0-9_-]{0,62}$'),
    created_at     timestamptz NOT NULL DEFAULT now()
);

INSERT INTO payment.flow_binding (operation_kind, flow_name)
VALUES ('PAYMENT_EXECUTION', 'payment-processing'),
       ('PAYMENT_APPROVAL', 'payment-review')
ON CONFLICT (operation_kind) DO NOTHING;

-------------------------------------------------------------------------------
-- Правило лимита. Версия правила — часть аудита решения, поэтому строка правила неизменяема:
-- другой порог означает другую версию, а не переписанную старую.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS payment.limit_rule (
    rule_version     text PRIMARY KEY CHECK (rule_version ~ '^[a-z][a-z0-9-]{0,62}$'),
    currency         text NOT NULL CHECK (currency = 'RUB'),
    auto_approve_max numeric(18, 2) NOT NULL CHECK (auto_approve_max > 0),
    is_active        boolean NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS limit_rule_single_active
    ON payment.limit_rule (currency)
    WHERE is_active;

INSERT INTO payment.limit_rule (rule_version, currency, auto_approve_max, is_active)
VALUES ('course-limit-v1', 'RUB', 100000.00, true)
ON CONFLICT (rule_version) DO NOTHING;

CREATE OR REPLACE FUNCTION payment.limit_rule_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'limit rules cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.rule_version, NEW.currency, NEW.auto_approve_max, NEW.created_at)
       IS DISTINCT FROM
       (OLD.rule_version, OLD.currency, OLD.auto_approve_max, OLD.created_at)
    THEN
        RAISE EXCEPTION 'limit rule contract is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS limit_rule_immutable ON payment.limit_rule;
CREATE TRIGGER limit_rule_immutable
    BEFORE UPDATE OR DELETE ON payment.limit_rule
    FOR EACH ROW EXECUTE FUNCTION payment.limit_rule_immutable();

-------------------------------------------------------------------------------
-- Внешний запрос. UNIQUE(operation_id) — «один внешний запрос на операцию» как гарантия
-- хранилища; UNIQUE(execution_id) связывает его с единственным логическим заданием шага.
-- Состояние только растёт: CREATED -> SENT -> CONFIRMED.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS payment.external_request (
    external_request_id text PRIMARY KEY CHECK (external_request_id ~ '^[A-Za-z0-9._:-]{1,128}$'),
    operation_id        uuid NOT NULL UNIQUE REFERENCES payment.operation (operation_id),
    process_id          uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    execution_id        uuid NOT NULL UNIQUE,
    correlation_id      uuid NOT NULL,
    amount              numeric(18, 2) NOT NULL CHECK (amount >= 0.01),
    currency            text NOT NULL CHECK (currency = 'RUB'),
    payload_hash        text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    state               text NOT NULL CHECK (state IN ('CREATED', 'SENT', 'CONFIRMED')),
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION payment.external_request_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_rank constant jsonb := '{"CREATED": 0, "SENT": 1, "CONFIRMED": 2}';
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'external requests cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.external_request_id, NEW.operation_id, NEW.process_id, NEW.execution_id,
        NEW.correlation_id, NEW.amount, NEW.currency, NEW.payload_hash, NEW.created_at)
       IS DISTINCT FROM
       (OLD.external_request_id, OLD.operation_id, OLD.process_id, OLD.execution_id,
        OLD.correlation_id, OLD.amount, OLD.currency, OLD.payload_hash, OLD.created_at)
    THEN
        RAISE EXCEPTION 'external request identity and payload are immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (v_rank ->> NEW.state)::integer < (v_rank ->> OLD.state)::integer THEN
        RAISE EXCEPTION 'external request state cannot go backwards'
            USING ERRCODE = 'restrict_violation';
    END IF;

    NEW.updated_at := clock_timestamp();
    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS external_request_guard ON payment.external_request;
CREATE TRIGGER external_request_guard
    BEFORE UPDATE OR DELETE ON payment.external_request
    FOR EACH ROW EXECUTE FUNCTION payment.external_request_guard();

-------------------------------------------------------------------------------
-- Outbox. Строка создаётся в одной транзакции с external_request и завершением задания шага
-- (payment.prepare_external), поэтому остановленный dispatcher не теряет запрос: он лежит здесь
-- до всякого HTTP. Ключ, тело и correlation id неизменяемы — каждая повторная попытка
-- обязана отправить ровно то же самое.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS delivery.outbox (
    outbox_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    external_request_id text NOT NULL UNIQUE REFERENCES payment.external_request (external_request_id),
    correlation_id      uuid NOT NULL,
    amount              text NOT NULL CHECK (amount ~ '^[0-9]{1,16}\.[0-9]{2}$'),
    currency            text NOT NULL CHECK (currency = 'RUB'),
    state               text NOT NULL CHECK (state IN ('PENDING', 'LEASED', 'RETRY_WAIT', 'DELIVERED', 'DEAD', 'CONFIRMED')),
    attempt_count       integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    lease_owner         text,
    lease_version       bigint NOT NULL DEFAULT 0 CHECK (lease_version >= 0),
    lease_until         timestamptz,
    next_attempt_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_error_code     text CHECK (last_error_code IS NULL OR last_error_code ~ '^[a-z0-9]+(\.[a-z0-9_]+)*$'),
    provider_payment_id text CHECK (provider_payment_id IS NULL OR length(provider_payment_id) BETWEEN 1 AND 128),
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    delivered_at        timestamptz,
    confirmed_at        timestamptz,
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT outbox_lease_shape CHECK (state <> 'LEASED' OR (lease_owner IS NOT NULL AND lease_until IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS outbox_queue
    ON delivery.outbox (next_attempt_at)
    WHERE state IN ('PENDING', 'RETRY_WAIT');

CREATE INDEX IF NOT EXISTS outbox_lease
    ON delivery.outbox (lease_until)
    WHERE state = 'LEASED';

CREATE OR REPLACE FUNCTION delivery.outbox_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'outbox messages cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.outbox_id, NEW.external_request_id, NEW.correlation_id, NEW.amount, NEW.currency, NEW.created_at)
       IS DISTINCT FROM
       (OLD.outbox_id, OLD.external_request_id, OLD.correlation_id, OLD.amount, OLD.currency, OLD.created_at)
    THEN
        RAISE EXCEPTION 'outbox message identity, key and body are immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    -- Подтверждённая квитанцией доставка — конечное состояние. Опоздавший ответ dispatcher'а
    -- (или его сбой) не может вернуть её назад, даже если граница забудет это проверить.
    IF OLD.state = 'CONFIRMED' AND NEW.state <> 'CONFIRMED' THEN
        RAISE EXCEPTION 'confirmed delivery cannot regress'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF NEW.lease_version < OLD.lease_version OR NEW.attempt_count < OLD.attempt_count THEN
        RAISE EXCEPTION 'outbox lease version and attempt count cannot go backwards'
            USING ERRCODE = 'restrict_violation';
    END IF;

    NEW.updated_at := clock_timestamp();
    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS outbox_guard ON delivery.outbox;
CREATE TRIGGER outbox_guard
    BEFORE UPDATE OR DELETE ON delivery.outbox
    FOR EACH ROW EXECUTE FUNCTION delivery.outbox_guard();

-- Политика доставки видна как данные, а не зашита в plpgsql: какой профиль действует, решает
-- признак test profile базы (workflow.runtime_setting), выставляемый bootstrap.
CREATE TABLE IF NOT EXISTS delivery.delivery_policy (
    profile      text PRIMARY KEY CHECK (profile IN ('default', 'test')),
    max_attempts integer NOT NULL CHECK (max_attempts BETWEEN 1 AND 20),
    delays_ms    jsonb NOT NULL CHECK (jsonb_typeof(delays_ms) = 'array' AND jsonb_array_length(delays_ms) >= max_attempts - 1),
    lease_ms     integer NOT NULL CHECK (lease_ms BETWEEN 500 AND 600000)
);

INSERT INTO delivery.delivery_policy (profile, max_attempts, delays_ms, lease_ms)
VALUES ('default', 5, '[1000, 5000, 15000, 60000]', 30000),
       ('test', 3, '[200, 400, 800]', 3000)
ON CONFLICT (profile) DO NOTHING;

-------------------------------------------------------------------------------
-- Inbox. messageId глобально уникален; вместе с ним сохраняется SHA-256 точных байт тела —
-- именно с ним сравнивается повтор. Сообщение уже при приёме привязано к pinned process и
-- типу сигнала; единственное допустимое изменение — однократный выход из RECEIVED.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS delivery.inbox (
    message_id  text PRIMARY KEY CHECK (length(message_id) BETWEEN 1 AND 128 AND message_id !~ '[\r\n]'),
    body_hash   text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    process_id  uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    signal_type text NOT NULL CHECK (length(signal_type) BETWEEN 1 AND 200),
    state       text NOT NULL CHECK (state IN ('RECEIVED', 'APPLIED', 'CONFLICT')),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at  timestamptz
);

CREATE INDEX IF NOT EXISTS inbox_pending
    ON delivery.inbox (received_at, message_id)
    WHERE state = 'RECEIVED';

CREATE INDEX IF NOT EXISTS inbox_process_pending
    ON delivery.inbox (process_id, signal_type, received_at)
    WHERE state = 'RECEIVED';

CREATE OR REPLACE FUNCTION delivery.inbox_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'inbox messages cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.message_id, NEW.body_hash, NEW.process_id, NEW.signal_type, NEW.received_at)
       IS DISTINCT FROM
       (OLD.message_id, OLD.body_hash, OLD.process_id, OLD.signal_type, OLD.received_at)
    THEN
        RAISE EXCEPTION 'inbox message identity is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF OLD.state <> 'RECEIVED' AND NEW.state IS DISTINCT FROM OLD.state THEN
        RAISE EXCEPTION 'a settled inbox message cannot change state'
            USING ERRCODE = 'restrict_violation';
    END IF;

    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS inbox_guard ON delivery.inbox;
CREATE TRIGGER inbox_guard
    BEFORE UPDATE OR DELETE ON delivery.inbox
    FOR EACH ROW EXECUTE FUNCTION delivery.inbox_guard();

-------------------------------------------------------------------------------
-- Квитанция receipt v1. occurredAt хранится исходной строкой: квитанция — свидетельство, и
-- её нельзя «нормализовать» при сохранении. Меняется только applied_at, и только один раз.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS payment.receipt (
    message_id          text PRIMARY KEY REFERENCES delivery.inbox (message_id),
    external_request_id text NOT NULL REFERENCES payment.external_request (external_request_id),
    operation_id        uuid NOT NULL REFERENCES payment.operation (operation_id),
    process_id          uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    message_version     integer NOT NULL CHECK (message_version = 1),
    outcome             text NOT NULL CHECK (outcome IN ('COMPLETED', 'REJECTED')),
    provider_payment_id text NOT NULL CHECK (length(provider_payment_id) BETWEEN 1 AND 128),
    occurred_at_raw     text NOT NULL CHECK (length(occurred_at_raw) BETWEEN 1 AND 64),
    signature_valid     boolean NOT NULL,
    signature_version   integer NOT NULL CHECK (signature_version = 1),
    body_hash           text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    received_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at          timestamptz,
    CONSTRAINT receipt_signed CHECK (signature_valid)
);

CREATE INDEX IF NOT EXISTS receipt_operation ON payment.receipt (operation_id, received_at);

CREATE OR REPLACE FUNCTION payment.receipt_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'receipts cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.message_id, NEW.external_request_id, NEW.operation_id, NEW.process_id,
        NEW.message_version, NEW.outcome, NEW.provider_payment_id, NEW.occurred_at_raw,
        NEW.signature_valid, NEW.signature_version, NEW.body_hash, NEW.received_at)
       IS DISTINCT FROM
       (OLD.message_id, OLD.external_request_id, OLD.operation_id, OLD.process_id,
        OLD.message_version, OLD.outcome, OLD.provider_payment_id, OLD.occurred_at_raw,
        OLD.signature_valid, OLD.signature_version, OLD.body_hash, OLD.received_at)
    THEN
        RAISE EXCEPTION 'receipt evidence is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF OLD.applied_at IS NOT NULL AND NEW.applied_at IS DISTINCT FROM OLD.applied_at THEN
        RAISE EXCEPTION 'an applied receipt cannot be reapplied'
            USING ERRCODE = 'restrict_violation';
    END IF;

    RETURN NEW;
END;
$fn$;

DROP TRIGGER IF EXISTS receipt_guard ON payment.receipt;
CREATE TRIGGER receipt_guard
    BEFORE UPDATE OR DELETE ON payment.receipt
    FOR EACH ROW EXECUTE FUNCTION payment.receipt_guard();

-------------------------------------------------------------------------------
-- Аудит решений. UNIQUE(step_instance_id): у одного шага ровно одно принятое решение, поэтому
-- конкурирующие решения физически не могут оба зафиксироваться. Полный reason не хранится —
-- только его отпечаток.
-------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.decision (
    decision_id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id       uuid NOT NULL REFERENCES workflow.process_instance (process_id),
    step_instance_id uuid NOT NULL UNIQUE REFERENCES workflow.step_instance (step_instance_id),
    source           text NOT NULL CHECK (source IN ('LIMIT_RULE', 'MANUAL')),
    principal        text NOT NULL CHECK (length(principal) BETWEEN 1 AND 128),
    reason_hash      text CHECK (reason_hash IS NULL OR reason_hash ~ '^[0-9a-f]{64}$'),
    outcome          text NOT NULL CHECK (outcome IN ('APPROVED', 'REJECTED')),
    rule_version     text,
    created_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT decision_limit_shape CHECK (source <> 'LIMIT_RULE' OR rule_version IS NOT NULL),
    CONSTRAINT decision_manual_shape CHECK (source <> 'MANUAL' OR (reason_hash IS NOT NULL AND rule_version IS NULL))
);

CREATE INDEX IF NOT EXISTS decision_process ON workflow.decision (process_id, created_at);

CREATE OR REPLACE FUNCTION workflow.decision_append_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    RAISE EXCEPTION 'decision audit is append only'
        USING ERRCODE = 'restrict_violation';
END;
$fn$;

DROP TRIGGER IF EXISTS decision_append_only ON workflow.decision;
CREATE TRIGGER decision_append_only
    BEFORE UPDATE OR DELETE ON workflow.decision
    FOR EACH ROW EXECUTE FUNCTION workflow.decision_append_only();

-------------------------------------------------------------------------------
-- История операции: одно OPERATION_SUBMITTED и один финальный исход на операцию.
-------------------------------------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS operation_event_single_submitted
    ON payment.operation_event (operation_id)
    WHERE event_type = 'OPERATION_SUBMITTED';

CREATE UNIQUE INDEX IF NOT EXISTS operation_event_single_final
    ON payment.operation_event (operation_id)
    WHERE event_type IN ('OPERATION_COMPLETED', 'OPERATION_REJECTED');

CREATE INDEX IF NOT EXISTS operation_process ON payment.operation (process_id);

REVOKE ALL ON ALL TABLES IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON payment.flow_binding, payment.limit_rule, payment.external_request, payment.receipt FROM PUBLIC;
REVOKE ALL ON workflow.decision FROM PUBLIC;
