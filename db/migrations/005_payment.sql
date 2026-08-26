-- 005_payment.sql
-- Авторитетное (главное) предметное состояние и две зарегистрированные функции первой недели.
-- Runtime-роль НИКОГДА не обращается к этим объектам напрямую: единственный путь к ним —
-- через api.invoke, который выполняется от лица владельца-без-входа (NOLOGIN owner) благодаря
-- SECURITY DEFINER.

-- Таблица операций (платежей). Это и есть "авторитетное состояние" — единственный источник
-- истины о том, какие платёжные операции существуют и в каком они статусе.
CREATE TABLE IF NOT EXISTS payment.operation (
    operation_id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id     text NOT NULL,
    principal      text NOT NULL,
    consumer       text,
    operation_kind text NOT NULL CHECK (operation_kind IN ('PAYMENT_EXECUTION', 'PAYMENT_APPROVAL')),
    amount         numeric(18, 2) NOT NULL CHECK (amount >= 0.01),
    currency       text NOT NULL CHECK (currency = 'RUB'),
    status         text NOT NULL CHECK (status IN ('CREATED', 'PROCESSING', 'COMPLETED', 'REJECTED')),
    process_id     uuid,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS operation_request ON payment.operation (request_id);

-- История событий операции. На первой неделе всегда ровно одно событие на операцию
-- (OPERATION_CREATED), но таблица спроектирована так, чтобы позже поддержать полноценную историю.
CREATE TABLE IF NOT EXISTS payment.operation_event (
    event_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    operation_id uuid NOT NULL REFERENCES payment.operation (operation_id),
    event_type   text NOT NULL CHECK (event_type ~ '^[A-Z][A-Z0-9_]{0,62}$'),
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    sequence_no  bigint GENERATED ALWAYS AS IDENTITY
);

CREATE INDEX IF NOT EXISTS operation_event_operation ON payment.operation_event (operation_id, sequence_no);

-- Ровно одно событие OPERATION_CREATED на операцию — гарантия PostgreSQL через частичный
-- уникальный индекс, а не проверка в коде приложения. Даже если конкурентные запросы попытаются
-- создать второе такое событие для одной и той же операции — второй попытке будет отказано.
CREATE UNIQUE INDEX IF NOT EXISTS operation_event_single_created
    ON payment.operation_event (operation_id)
    WHERE event_type = 'OPERATION_CREATED';

-- Поля идентичности и содержимого операции НЕИЗМЕНЯЕМЫ после её создания: их нельзя поменять
-- задним числом даже владельцу объекта. Изменяться может только статус и производные поля
-- (например process_id в будущем), но не то, ЧТО это была за операция и для кого.
CREATE OR REPLACE FUNCTION payment.operation_identity_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (NEW.operation_id, NEW.request_id, NEW.principal, NEW.operation_kind, NEW.amount,
        NEW.currency, NEW.created_at)
       IS DISTINCT FROM
       (OLD.operation_id, OLD.request_id, OLD.principal, OLD.operation_kind, OLD.amount,
        OLD.currency, OLD.created_at)
    THEN
        RAISE EXCEPTION 'operation identity and payload fields are immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS operation_identity_immutable ON payment.operation;
CREATE TRIGGER operation_identity_immutable
    BEFORE UPDATE ON payment.operation
    FOR EACH ROW EXECUTE FUNCTION payment.operation_identity_immutable();

-- Операции нельзя удалять вообще, ни при каких условиях.
CREATE OR REPLACE FUNCTION payment.operation_no_delete()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'operations cannot be deleted'
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS operation_no_delete ON payment.operation;
CREATE TRIGGER operation_no_delete
    BEFORE DELETE ON payment.operation
    FOR EACH ROW EXECUTE FUNCTION payment.operation_no_delete();

-- История событий — журнал "только на добавление" (append only) для ВСЕХ прикладных ролей,
-- включая самого владельца объекта. Это гарантирует, что предметная история никогда не может
-- быть переписана задним числом, даже кодом, который случайно получит достаточно прав.
CREATE OR REPLACE FUNCTION payment.operation_event_append_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'operation history is append only'
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS operation_event_append_only ON payment.operation_event;
CREATE TRIGGER operation_event_append_only
    BEFORE UPDATE OR DELETE ON payment.operation_event
    FOR EACH ROW EXECUTE FUNCTION payment.operation_event_append_only();

-- Каноническая контрактная проекция одной операции — общая для payment.request и operation.get,
-- поэтому обе функции гарантированно возвращают результат ОДНОЙ И ТОЙ ЖЕ формы (как того требует
-- нормативный справочник задания).
CREATE OR REPLACE FUNCTION payment.operation_result(p_row payment.operation)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'operationId', p_row.operation_id,
        'requestId', p_row.request_id,
        'operationKind', p_row.operation_kind,
        -- to_char с маской 'FM9999999999999990.00' форматирует numeric ровно так, как того
        -- требует JSON Schema контракта: без лишних нулей слева, ровно с двумя знаками
        -- после точки, без экспоненциальной записи.
        'amount', pg_catalog.to_char(p_row.amount, 'FM9999999999999990.00'),
        'currency', p_row.currency,
        'status', p_row.status
    );
$$;

-------------------------------------------------------------------------------
-- payment.request версия 1 — зарегистрированная функция действия "создать операцию".
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.request_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
DECLARE
    v_operation payment.operation%ROWTYPE;
BEGIN
    -- Обратите внимание: principal и consumer берутся из p_context (доверенного, серверного
    -- контекста), а НЕ из p_payload — клиент не может выдать себя за другого principal, даже
    -- если попытается передать такое поле в теле запроса (оно было бы отклонено JSON Schema
    -- запроса ещё до вызова этой функции, но здесь дополнительно физически невозможно достать
    -- такое значение из payload).
    INSERT INTO payment.operation (
        request_id, principal, consumer, operation_kind, amount, currency, status
    )
    VALUES (
        p_context ->> 'requestId',
        p_context ->> 'principal',
        p_context ->> 'consumer',
        p_payload ->> 'operationKind',
        (p_payload ->> 'amount')::numeric,
        p_payload ->> 'currency',
        'CREATED'
    )
    RETURNING * INTO v_operation;

    -- Вставка операции и вставка её первого события происходят в одной и той же вызванной
    -- функции — а значит, в одной и той же транзакции api.invoke. Если что-то дальше пойдёт не
    -- так (например результат не пройдёт проверку схемы в C#), откатятся ОБЕ вставки разом.
    INSERT INTO payment.operation_event (operation_id, event_type, payload_hash)
    VALUES (v_operation.operation_id, 'OPERATION_CREATED', api.payload_hash(p_payload));

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'CREATED',
        'result', payment.operation_result(v_operation),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$$;

-------------------------------------------------------------------------------
-- operation.get версия 1 — зарегистрированная функция действия "прочитать операцию по ID".
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.operation_get_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
DECLARE
    v_operation_id uuid;
    v_operation    payment.operation%ROWTYPE;
BEGIN
    BEGIN
        v_operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        -- Значение operationId прошло проверку JSON Schema (format: uuid) на уровне C#, но
        -- явное приведение типа здесь — дополнительный уровень защиты: если строка всё же не
        -- парсится как UUID, это контролируемая предметная ошибка, а не необработанный сбой.
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'payload.invalid',
            'message', 'operationId is not a valid identifier',
            'retryable', false,
            'details', '{}'::jsonb
        );
    END;

    SELECT * INTO v_operation
    FROM payment.operation
    WHERE operation_id = v_operation_id;

    IF NOT FOUND THEN
        -- Неизвестный operationId — это контролируемая предметная ошибка (422), а не 500:
        -- запрос был понят правильно, но такой операции просто не существует.
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'operation.not_found',
            'message', 'operation is not found',
            'retryable', false,
            'details', '{}'::jsonb
        );
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', payment.operation_result(v_operation),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$$;
