-- 018_perimeter_actions.sql
-- Неделя 3: зарегистрированные функции двух платёжных процессов, приём квитанции, ручное
-- решение и фиксированные SQL-границы Python-периметра.
--
-- Ни одна функция ниже не выбирает карту по имени и не ветвит ядро workflow: карты исполняет
-- общий worker, а предметные решения (лимит, финальный статус) принимаются здесь, в PostgreSQL,
-- только по сохранённым свидетельствам — квитанции или аудированному решению.

-------------------------------------------------------------------------------
-- Общие помощники
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION api.try_uuid(p_value text)
RETURNS uuid
LANGUAGE plpgsql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF p_value IS NULL THEN
        RETURN NULL;
    END IF;
    RETURN p_value::uuid;
EXCEPTION WHEN invalid_text_representation THEN
    RETURN NULL;
END;
$fn$;

CREATE OR REPLACE FUNCTION api.action_fail(p_code text, p_message text, p_retryable boolean DEFAULT false)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message,
        'retryable', coalesce(p_retryable, false),
        'details', '{}'::jsonb
    );
$fn$;

CREATE OR REPLACE FUNCTION api.action_ok(p_context jsonb, p_outcome text, p_result jsonb)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'status', 'ok',
        'outcome', p_outcome,
        'result', p_result,
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
$fn$;

CREATE OR REPLACE FUNCTION payment.amount_text(p_amount numeric)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT pg_catalog.to_char(p_amount, 'FM9999999999999990.00');
$fn$;

CREATE OR REPLACE FUNCTION api.sha256_text(p_value text)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT encode(sha256(convert_to(p_value, 'UTF8')), 'hex');
$fn$;

CREATE OR REPLACE FUNCTION payment.record_event(p_operation_id uuid, p_event_type text, p_payload_hash text)
RETURNS void
LANGUAGE sql
SET search_path = pg_catalog
AS $fn$
    INSERT INTO payment.operation_event (operation_id, event_type, payload_hash)
    VALUES (p_operation_id, p_event_type, p_payload_hash);
$fn$;

-- Операция шага процесса. Автоматический шаг получает operationId из данных процесса, а
-- processId — из доверенного контекста worker'а. Они обязаны указывать друг на друга: иначе шаг
-- одного процесса мог бы тронуть чужую операцию. Строка операции блокируется до конца транзакции.
CREATE OR REPLACE FUNCTION payment.bound_operation(p_context jsonb, p_payload jsonb)
RETURNS payment.operation
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation_id uuid := api.try_uuid(p_payload ->> 'operationId');
    v_process_id   uuid := api.try_uuid(p_context ->> 'processId');
    v_operation    payment.operation%ROWTYPE;
BEGIN
    IF v_operation_id IS NULL OR v_process_id IS NULL THEN
        RETURN NULL;
    END IF;

    SELECT * INTO v_operation
    FROM payment.operation
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND OR v_operation.process_id IS DISTINCT FROM v_process_id THEN
        RETURN NULL;
    END IF;

    RETURN v_operation;
END;
$fn$;

CREATE OR REPLACE FUNCTION payment.not_bound()
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT api.action_fail('payment.operation_not_bound', 'operation is not bound to the executing process');
$fn$;

-------------------------------------------------------------------------------
-- payment.submit v1: operation CREATED -> PROCESSING + pinned process + первый job + событие.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.submit_result(p_operation_id uuid, p_process_id uuid)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'operationId', p_operation_id,
        'processId', p.process_id,
        'flowName', f.flow_name,
        'flowVersion', v.version,
        -- Результат команды submit — это то, что команда сделала, а не актуальный статус
        -- операции: повтор после завершения обязан вернуть тот же subject result.
        'status', 'PROCESSING'
    )
    FROM workflow.process_instance p
    JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
    JOIN workflow.flow_version v ON v.flow_version_id = p.flow_version_id
    WHERE p.process_id = p_process_id;
$fn$;

CREATE OR REPLACE FUNCTION payment.submit_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation_id uuid := api.try_uuid(p_payload ->> 'operationId');
    v_operation    payment.operation%ROWTYPE;
    v_flow_name    text;
    v_started      jsonb;
    v_process_id   uuid;
BEGIN
    IF v_operation_id IS NULL THEN
        RETURN api.action_fail('payload.invalid', 'operationId is not a valid identifier');
    END IF;

    -- Блокировка строки операции сериализует конкурентные submit одной операции: второй увидит
    -- уже закреплённый процесс и вернёт его, а не создаст новый.
    SELECT * INTO v_operation
    FROM payment.operation
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.action_fail('operation.not_found', 'operation is not found');
    END IF;

    IF v_operation.process_id IS NOT NULL THEN
        RETURN api.action_ok(p_context, 'SUBMITTED',
            payment.submit_result(v_operation.operation_id, v_operation.process_id));
    END IF;

    IF v_operation.status <> 'CREATED' THEN
        RETURN api.action_fail('payment.operation_not_submittable', 'operation cannot be submitted in its current status');
    END IF;

    SELECT b.flow_name INTO v_flow_name
    FROM payment.flow_binding b
    WHERE b.operation_kind = v_operation.operation_kind;

    IF NOT FOUND THEN
        RETURN api.action_fail('payment.flow_binding_missing', 'operation kind has no server-side flow binding');
    END IF;

    -- Запуск идёт через ту же границу, что и flow start: процесс закрепляется за активной
    -- версией, первый шаг создаёт job или устойчивое ожидание — всё в этой транзакции.
    v_started := workflow.start_process(
        v_flow_name,
        v_operation.operation_id::text,
        jsonb_build_object('operationId', v_operation.operation_id::text));

    IF v_started ->> 'status' IS DISTINCT FROM 'ok' THEN
        RETURN api.action_fail(
            coalesce(v_started ->> 'code', 'workflow.start_failed'),
            'payment process could not be started');
    END IF;

    v_process_id := (v_started -> 'result' ->> 'processId')::uuid;

    -- start_process идемпотентен по business key и вернёт уже существующий процесс, если его
    -- запустили в обход submit (trusted-командой flow start). Привязать к нему операцию нельзя:
    -- такой процесс мог уже пройти или провалить шаги, не видя операцию в PROCESSING. Процесс,
    -- созданный этой транзакцией, имеет created_at = now() (время начала транзакции).
    IF NOT EXISTS (
        SELECT 1 FROM workflow.process_instance p
        WHERE p.process_id = v_process_id AND p.created_at = now()
    ) THEN
        RETURN api.action_fail('payment.process_conflict', 'a process for this operation was started outside payment.submit');
    END IF;

    UPDATE payment.operation
    SET status = 'PROCESSING', process_id = v_process_id
    WHERE operation_id = v_operation.operation_id;

    PERFORM payment.record_event(v_operation.operation_id, 'OPERATION_SUBMITTED', api.payload_hash(p_payload));

    RETURN api.action_ok(p_context, 'SUBMITTED',
        payment.submit_result(v_operation.operation_id, v_process_id));
END;
$fn$;

-------------------------------------------------------------------------------
-- operation.events v1: история операции без payload.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.operation_events_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation_id uuid := api.try_uuid(p_payload ->> 'operationId');
    v_operation    payment.operation%ROWTYPE;
BEGIN
    IF v_operation_id IS NULL THEN
        RETURN api.action_fail('payload.invalid', 'operationId is not a valid identifier');
    END IF;

    SELECT * INTO v_operation FROM payment.operation WHERE operation_id = v_operation_id;

    IF NOT FOUND THEN
        RETURN api.action_fail('operation.not_found', 'operation is not found');
    END IF;

    RETURN api.action_ok(p_context, 'FOUND', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'status', v_operation.status,
        'processId', v_operation.process_id,
        'events', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                'eventId', e.event_id,
                'eventType', e.event_type,
                'occurredAt', e.occurred_at
            ) ORDER BY e.sequence_no)
            FROM (
                SELECT * FROM payment.operation_event
                WHERE operation_id = v_operation.operation_id
                ORDER BY sequence_no
                LIMIT 200
            ) AS e
        ), '[]'::jsonb)
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- payment.validate v1 (обе карты)
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.validate_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation payment.operation%ROWTYPE;
BEGIN
    v_operation := payment.bound_operation(p_context, p_payload);
    IF v_operation.operation_id IS NULL THEN
        RETURN payment.not_bound();
    END IF;

    IF v_operation.status <> 'PROCESSING' THEN
        RETURN api.action_fail('payment.operation_not_processing', 'operation is not processing');
    END IF;

    -- Процесс обязан исполнять ровно ту карту, которую серверная привязка назначает виду
    -- операции. Это проверка предметного инварианта, а не выбор ветки ядра.
    IF NOT EXISTS (
        SELECT 1
        FROM workflow.process_instance p
        JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
        JOIN payment.flow_binding b ON b.flow_name = f.flow_name
        WHERE p.process_id = v_operation.process_id
          AND b.operation_kind = v_operation.operation_kind
    ) THEN
        RETURN api.action_fail('payment.flow_binding_mismatch', 'process does not execute the flow bound to the operation kind');
    END IF;

    RETURN api.action_ok(p_context, 'VALID', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'operationKind', v_operation.operation_kind,
        'amount', payment.amount_text(v_operation.amount),
        'currency', v_operation.currency
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- payment.prepare_external v1: external_request + Outbox одной транзакцией с finish_job.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.prepare_external_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation    payment.operation%ROWTYPE;
    v_request      payment.external_request%ROWTYPE;
    v_execution_id uuid := api.try_uuid(p_context ->> 'executionId');
    v_external_id  text;
    v_amount       text;
    v_body         text;
BEGIN
    v_operation := payment.bound_operation(p_context, p_payload);
    IF v_operation.operation_id IS NULL THEN
        RETURN payment.not_bound();
    END IF;

    IF v_operation.operation_kind <> 'PAYMENT_EXECUTION' THEN
        RETURN api.action_fail('payment.operation_kind_invalid', 'only payment execution requires an external request');
    END IF;

    IF v_operation.status <> 'PROCESSING' THEN
        RETURN api.action_fail('payment.operation_not_processing', 'operation is not processing');
    END IF;

    IF v_execution_id IS NULL THEN
        RETURN api.action_fail('payment.execution_required', 'trusted context must carry an executionId');
    END IF;

    SELECT * INTO v_request
    FROM payment.external_request
    WHERE operation_id = v_operation.operation_id;

    IF NOT FOUND THEN
        -- Идентификатор выводится из executionId задания: все повторы одного задания дают один и
        -- тот же ключ провайдера, а UNIQUE(operation_id) не даёт появиться второму запросу.
        v_external_id := 'ext-' || v_execution_id::text;
        v_amount := payment.amount_text(v_operation.amount);
        -- Точные байты тела провайдера v0.2.0 в порядке полей контракта. Отпечаток считается
        -- над теми же байтами, которые отправит dispatcher.
        v_body := '{"operationId":' || to_json(v_external_id)::text
               || ',"amount":' || to_json(v_amount)::text
               || ',"currency":' || to_json(v_operation.currency)::text || '}';

        INSERT INTO payment.external_request (
            external_request_id, operation_id, process_id, execution_id, correlation_id,
            amount, currency, payload_hash, state
        )
        VALUES (
            v_external_id, v_operation.operation_id, v_operation.process_id, v_execution_id,
            coalesce(api.try_uuid(p_context ->> 'correlationId'), gen_random_uuid()),
            v_operation.amount, v_operation.currency, api.sha256_text(v_body), 'CREATED'
        )
        RETURNING * INTO v_request;

        INSERT INTO delivery.outbox (external_request_id, correlation_id, amount, currency, state)
        VALUES (v_request.external_request_id, v_request.correlation_id, v_amount, v_request.currency, 'PENDING');

        PERFORM payment.record_event(v_operation.operation_id, 'EXTERNAL_REQUEST_PREPARED', v_request.payload_hash);
    END IF;

    RETURN api.action_ok(p_context, 'PREPARED', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'externalRequestId', v_request.external_request_id
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- payment.apply_receipt v1: исход берётся ТОЛЬКО из сохранённой квитанции, применённой к
-- этому процессу через Inbox. Ни тело клиента, ни ответ провайдера сюда не доходят.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.apply_receipt_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation payment.operation%ROWTYPE;
    v_receipt   payment.receipt%ROWTYPE;
BEGIN
    v_operation := payment.bound_operation(p_context, p_payload);
    IF v_operation.operation_id IS NULL THEN
        RETURN payment.not_bound();
    END IF;

    IF v_operation.status <> 'PROCESSING' THEN
        RETURN api.action_fail('payment.operation_not_processing', 'operation is not processing');
    END IF;

    SELECT r.* INTO v_receipt
    FROM payment.receipt r
    JOIN delivery.inbox i ON i.message_id = r.message_id
    WHERE r.operation_id = v_operation.operation_id
      AND r.process_id = v_operation.process_id
      AND i.state = 'APPLIED'
    ORDER BY i.applied_at, r.message_id
    LIMIT 1
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN api.action_fail('payment.receipt_missing', 'no applied receipt exists for the operation');
    END IF;

    IF v_receipt.applied_at IS NULL THEN
        UPDATE payment.receipt
        SET applied_at = clock_timestamp()
        WHERE message_id = v_receipt.message_id;

        PERFORM payment.record_event(v_operation.operation_id, 'RECEIPT_APPLIED', v_receipt.body_hash);
    END IF;

    RETURN api.action_ok(p_context, v_receipt.outcome, jsonb_build_object(
        'operationId', v_operation.operation_id,
        'externalRequestId', v_receipt.external_request_id,
        'messageId', v_receipt.message_id,
        'outcome', v_receipt.outcome
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Финальные действия. Каждое требует свидетельство, иначе отказывает: финальный статус
-- не может появиться без применённой квитанции или аудированного решения.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.finish_operation(
    p_context jsonb,
    p_payload jsonb,
    p_status text,
    p_outcome text,
    p_evidence jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation payment.operation%ROWTYPE;
BEGIN
    v_operation := payment.bound_operation(p_context, p_payload);
    IF v_operation.operation_id IS NULL THEN
        RETURN payment.not_bound();
    END IF;

    IF p_evidence IS NULL THEN
        RETURN api.action_fail('payment.final_evidence_missing', 'final status requires a receipt or an audited decision');
    END IF;

    IF v_operation.status = 'PROCESSING' THEN
        UPDATE payment.operation
        SET status = p_status
        WHERE operation_id = v_operation.operation_id;

        PERFORM payment.record_event(
            v_operation.operation_id,
            CASE p_status WHEN 'COMPLETED' THEN 'OPERATION_COMPLETED' ELSE 'OPERATION_REJECTED' END,
            api.payload_hash(p_evidence));
    ELSIF v_operation.status <> p_status THEN
        RETURN api.action_fail('payment.operation_not_processing', 'operation already has a different final status');
    END IF;

    RETURN api.action_ok(p_context, p_outcome, jsonb_build_object(
        'operationId', v_operation.operation_id,
        'status', p_status,
        'evidence', p_evidence
    ));
END;
$fn$;

CREATE OR REPLACE FUNCTION payment.receipt_evidence(p_payload jsonb, p_context jsonb, p_outcome text)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object('kind', 'RECEIPT', 'messageId', r.message_id)
    FROM payment.receipt r
    WHERE r.operation_id = api.try_uuid(p_payload ->> 'operationId')
      AND r.process_id = api.try_uuid(p_context ->> 'processId')
      AND r.outcome = p_outcome
      AND r.applied_at IS NOT NULL
    ORDER BY r.applied_at
    LIMIT 1;
$fn$;

CREATE OR REPLACE FUNCTION payment.decision_evidence(p_context jsonb, p_outcome text)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object('kind', 'DECISION', 'decisionId', d.decision_id, 'source', d.source)
    FROM workflow.decision d
    WHERE d.process_id = api.try_uuid(p_context ->> 'processId')
      AND d.outcome = p_outcome
    ORDER BY d.created_at
    LIMIT 1;
$fn$;

CREATE OR REPLACE FUNCTION payment.complete_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
BEGIN
    RETURN payment.finish_operation(p_context, p_payload, 'COMPLETED', 'COMPLETED',
        payment.receipt_evidence(p_payload, p_context, 'COMPLETED'));
END;
$fn$;

CREATE OR REPLACE FUNCTION payment.reject_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
BEGIN
    RETURN payment.finish_operation(p_context, p_payload, 'REJECTED', 'REJECTED',
        coalesce(
            payment.receipt_evidence(p_payload, p_context, 'REJECTED'),
            payment.decision_evidence(p_context, 'REJECTED')));
END;
$fn$;

CREATE OR REPLACE FUNCTION payment.approve_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
BEGIN
    RETURN payment.finish_operation(p_context, p_payload, 'COMPLETED', 'APPROVED',
        payment.decision_evidence(p_context, 'APPROVED'));
END;
$fn$;

-------------------------------------------------------------------------------
-- payment.check_limit v1: серверное правило course-limit-v1. Автоматическое одобрение
-- фиксируется решением LIMIT_RULE в той же транзакции, что и завершение шага.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.check_limit_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_operation payment.operation%ROWTYPE;
    v_rule      payment.limit_rule%ROWTYPE;
    v_step_id   uuid;
    v_decision  workflow.decision%ROWTYPE;
    v_inserted  integer;
BEGIN
    v_operation := payment.bound_operation(p_context, p_payload);
    IF v_operation.operation_id IS NULL THEN
        RETURN payment.not_bound();
    END IF;

    IF v_operation.operation_kind <> 'PAYMENT_APPROVAL' THEN
        RETURN api.action_fail('payment.operation_kind_invalid', 'limit rule applies only to payment approval');
    END IF;

    IF v_operation.status <> 'PROCESSING' THEN
        RETURN api.action_fail('payment.operation_not_processing', 'operation is not processing');
    END IF;

    SELECT * INTO v_rule
    FROM payment.limit_rule
    WHERE is_active AND currency = v_operation.currency;

    IF NOT FOUND THEN
        RETURN api.action_fail('payment.limit_rule_missing', 'no active limit rule for the currency');
    END IF;

    SELECT j.step_instance_id INTO v_step_id
    FROM workflow.workflow_job j
    WHERE j.job_id = api.try_uuid(p_context ->> 'jobId')
      AND j.process_id = v_operation.process_id;

    IF NOT FOUND THEN
        RETURN api.action_fail('payment.step_unknown', 'trusted context does not identify the executing step');
    END IF;

    IF v_operation.amount > v_rule.auto_approve_max THEN
        RETURN api.action_ok(p_context, 'REVIEW_REQUIRED', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'ruleVersion', v_rule.rule_version,
            'decisionId', NULL
        ));
    END IF;

    INSERT INTO workflow.decision (process_id, step_instance_id, source, principal, outcome, rule_version)
    VALUES (v_operation.process_id, v_step_id, 'LIMIT_RULE', p_context ->> 'principal', 'APPROVED', v_rule.rule_version)
    ON CONFLICT (step_instance_id) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    SELECT * INTO v_decision FROM workflow.decision WHERE step_instance_id = v_step_id;

    -- Уже записанное решение шага не дублирует событие истории: одно решение — одна запись.
    IF v_inserted > 0 THEN
        PERFORM workflow.record_event(
            v_operation.process_id, v_step_id, 'DecisionRecorded',
            jsonb_build_object('decisionId', v_decision.decision_id, 'source', v_decision.source, 'outcome', v_decision.outcome));
    END IF;

    RETURN api.action_ok(p_context, 'WITHIN_LIMIT', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'ruleVersion', v_rule.rule_version,
        'decisionId', v_decision.decision_id
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Inbox -> workflow signal.
--
-- Порядок блокировок везде один: process_instance, затем строка inbox. Worker приходит сюда из
-- finish_job (процесс уже заблокирован), согласователь сам берёт процесс первым — взаимной
-- блокировки быть не может. Строку inbox оба берут через SKIP LOCKED.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION delivery.materialize_signal(p_message_id text)
RETURNS text
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_inbox    delivery.inbox%ROWTYPE;
    v_existing workflow.workflow_signal%ROWTYPE;
    v_body     jsonb;
    v_hash     text;
    v_rows     integer;
BEGIN
    SELECT * INTO v_inbox FROM delivery.inbox WHERE message_id = p_message_id;

    -- Тело сигнала несёт только ссылку на сообщение: сама квитанция лежит в payment.receipt.
    v_body := jsonb_build_object('source', 'inbox', 'messageId', v_inbox.message_id, 'bodyHash', v_inbox.body_hash);
    v_hash := api.payload_hash(v_body);

    INSERT INTO workflow.workflow_signal (message_id, process_id, signal_type, body, body_hash, status)
    VALUES (v_inbox.message_id, v_inbox.process_id, v_inbox.signal_type, v_body, v_hash, 'ACCEPTED')
    ON CONFLICT (message_id) DO NOTHING;

    GET DIAGNOSTICS v_rows = ROW_COUNT;

    IF v_rows = 0 THEN
        SELECT * INTO v_existing FROM workflow.workflow_signal WHERE message_id = v_inbox.message_id;

        IF v_existing.process_id IS DISTINCT FROM v_inbox.process_id
           OR v_existing.signal_type IS DISTINCT FROM v_inbox.signal_type
           OR v_existing.body_hash IS DISTINCT FROM v_hash
        THEN
            UPDATE delivery.inbox SET state = 'CONFLICT' WHERE message_id = v_inbox.message_id;
            RETURN 'CONFLICT';
        END IF;
    ELSE
        PERFORM workflow.record_event(
            v_inbox.process_id, NULL, 'SignalAccepted',
            jsonb_build_object('signalType', v_inbox.signal_type, 'messageId', v_inbox.message_id, 'source', 'inbox'));
    END IF;

    UPDATE delivery.inbox
    SET state = 'APPLIED', applied_at = clock_timestamp()
    WHERE message_id = v_inbox.message_id;

    RETURN 'APPLIED';
END;
$fn$;

-- Ждёт ли процесс сигнал этого типа прямо сейчас (процесс должен быть заблокирован вызывающим).
CREATE OR REPLACE FUNCTION delivery.process_waits_for(p_process_id uuid, p_signal_type text)
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT EXISTS (
        SELECT 1
        FROM workflow.process_instance p
        JOIN workflow.step_definition sd
          ON sd.flow_version_id = p.flow_version_id
         AND sd.step_key = p.current_step_key
        JOIN workflow.step_instance si
          ON si.process_id = p.process_id
         AND si.step_key = sd.step_key
        WHERE p.process_id = p_process_id
          AND p.state = 'WAITING_SIGNAL'
          AND sd.step_type = 'WAIT_SIGNAL'
          AND sd.signal_type = p_signal_type
          AND si.state = 'WAITING'
    );
$fn$;

-- Сообщение опоздало навсегда: процесс завершён, ожидание этого типа уже пройдено или карта
-- такого ожидания не объявляет. Раннее сообщение (ожидание ещё впереди) сюда не относится.
CREATE OR REPLACE FUNCTION delivery.message_superseded(p_process_id uuid, p_signal_type text)
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT p.state IN ('COMPLETED', 'FAILED')
        OR NOT EXISTS (
            SELECT 1 FROM workflow.step_definition sd
            WHERE sd.flow_version_id = p.flow_version_id
              AND sd.step_type = 'WAIT_SIGNAL'
              AND sd.signal_type = p_signal_type)
        OR EXISTS (
            SELECT 1
            FROM workflow.step_definition sd
            JOIN workflow.step_instance si
              ON si.process_id = p.process_id
             AND si.step_key = sd.step_key
            WHERE sd.flow_version_id = p.flow_version_id
              AND sd.step_type = 'WAIT_SIGNAL'
              AND sd.signal_type = p_signal_type
              AND si.state IN ('COMPLETED', 'FAILED'))
    FROM workflow.process_instance p
    WHERE p.process_id = p_process_id;
$fn$;

-- Вход в wait_signal проверяет Inbox в своей транзакции: раннее сообщение не теряется.
CREATE OR REPLACE FUNCTION delivery.apply_inbox_for_process(p_process_id uuid)
RETURNS boolean
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_signal_type text;
    v_message_id  text;
BEGIN
    SELECT sd.signal_type INTO v_signal_type
    FROM workflow.process_instance p
    JOIN workflow.step_definition sd
      ON sd.flow_version_id = p.flow_version_id
     AND sd.step_key = p.current_step_key
    WHERE p.process_id = p_process_id
      AND p.state = 'WAITING_SIGNAL'
      AND sd.step_type = 'WAIT_SIGNAL';

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    LOOP
        SELECT i.message_id INTO v_message_id
        FROM delivery.inbox i
        WHERE i.process_id = p_process_id
          AND i.signal_type = v_signal_type
          AND i.state = 'RECEIVED'
        ORDER BY i.received_at, i.message_id
        LIMIT 1
        FOR UPDATE SKIP LOCKED;

        IF NOT FOUND THEN
            RETURN false;
        END IF;

        IF delivery.materialize_signal(v_message_id) = 'APPLIED' THEN
            RETURN true;
        END IF;
    END LOOP;
END;
$fn$;

-- Ядро workflow: вход в шаг. Тело совпадает с 012_workflow_engine.sql, добавлен один вызов —
-- проверка Inbox при входе в ожидание сигнала. Имя карты или шага по-прежнему не используется.
CREATE OR REPLACE FUNCTION workflow.enter_step(p_process_id uuid, p_step_key text)
RETURNS void
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_process         workflow.process_instance%ROWTYPE;
    v_step            workflow.step_definition%ROWTYPE;
    v_step_instance_id uuid;
    v_state           text;
BEGIN
    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE process_id = p_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'process % does not exist', p_process_id
            USING ERRCODE = 'no_data_found';
    END IF;

    SELECT * INTO v_step
    FROM workflow.step_definition
    WHERE flow_version_id = v_process.flow_version_id
      AND step_key = p_step_key;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'step % is not part of the pinned flow version', p_step_key
            USING ERRCODE = 'no_data_found';
    END IF;

    v_state := CASE v_step.step_type
        WHEN 'AUTOMATIC' THEN 'RUNNING'
        WHEN 'END'       THEN 'COMPLETED'
        ELSE 'WAITING'
    END;

    INSERT INTO workflow.step_instance (
        process_id, step_definition_id, step_key, step_type, state
    )
    VALUES (
        p_process_id, v_step.step_definition_id, v_step.step_key, v_step.step_type, v_state
    )
    RETURNING step_instance_id INTO v_step_instance_id;

    PERFORM workflow.record_event(
        p_process_id,
        v_step_instance_id,
        'StepEntered',
        jsonb_build_object('stepKey', v_step.step_key, 'stepType', v_step.step_type)
    );

    IF v_step.step_type = 'AUTOMATIC' THEN
        UPDATE workflow.process_instance
        SET state = 'RUNNING', current_step_key = v_step.step_key, updated_at = now()
        WHERE process_id = p_process_id;

        INSERT INTO workflow.workflow_job (process_id, step_instance_id, state, next_attempt_at)
        VALUES (p_process_id, v_step_instance_id, 'READY', now());

        PERFORM workflow.record_event(
            p_process_id, v_step_instance_id, 'TaskScheduled',
            jsonb_build_object('stepKey', v_step.step_key)
        );
        RETURN;
    END IF;

    IF v_step.step_type = 'MANUAL' THEN
        UPDATE workflow.process_instance
        SET state = 'WAITING_MANUAL', current_step_key = v_step.step_key, updated_at = now()
        WHERE process_id = p_process_id;
        RETURN;
    END IF;

    IF v_step.step_type = 'END' THEN
        UPDATE workflow.step_instance
        SET outcome = v_step.outcome, completed_at = clock_timestamp()
        WHERE step_instance_id = v_step_instance_id;

        UPDATE workflow.process_instance
        SET state = 'COMPLETED', current_step_key = v_step.step_key, updated_at = now()
        WHERE process_id = p_process_id;

        PERFORM workflow.record_event(
            p_process_id, v_step_instance_id, 'ProcessCompleted',
            jsonb_build_object('stepKey', v_step.step_key, 'outcome', v_step.outcome)
        );
        RETURN;
    END IF;

    UPDATE workflow.process_instance
    SET state = 'WAITING_SIGNAL', current_step_key = v_step.step_key, updated_at = now()
    WHERE process_id = p_process_id;

    -- Сообщение могло прийти раньше входа в ожидание: либо уже как сигнал (ACCEPTED), либо как
    -- сохранённое, но ещё не согласованное сообщение Inbox. Второе превращается в сигнал здесь,
    -- в этой же транзакции, и применяется следующей строкой.
    PERFORM delivery.apply_inbox_for_process(p_process_id);
    PERFORM workflow.apply_pending_signal(p_process_id);
END;
$fn$;

-------------------------------------------------------------------------------
-- receipt.accept v1
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.receipt_replay(p_context jsonb, p_message_id text, p_body_hash text)
RETURNS jsonb
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_inbox   delivery.inbox%ROWTYPE;
    v_receipt payment.receipt%ROWTYPE;
BEGIN
    SELECT * INTO v_inbox FROM delivery.inbox WHERE message_id = p_message_id FOR UPDATE;
    SELECT * INTO v_receipt FROM payment.receipt WHERE message_id = p_message_id;

    -- Тот же messageId с другими байтами — конфликт. Исходная запись, сигнал и финальный
    -- статус остаются нетронутыми.
    IF v_inbox.body_hash IS DISTINCT FROM p_body_hash OR v_receipt.message_id IS NULL THEN
        RETURN api.action_fail('idempotency.conflict', 'messageId was already accepted with a different body');
    END IF;

    RETURN api.action_ok(p_context, 'DUPLICATE', jsonb_build_object(
        'messageId', v_inbox.message_id,
        'externalRequestId', v_receipt.external_request_id,
        'state', v_inbox.state
    ));
END;
$fn$;

CREATE OR REPLACE FUNCTION payment.receipt_accept_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_message_id  text := p_payload ->> 'messageId';
    v_external_id text := p_payload ->> 'externalRequestId';
    v_body_hash   text;
    v_request     payment.external_request%ROWTYPE;
    v_rows        integer;
BEGIN
    -- Доверенный маркер ставит только общая C#-граница подписи, и только после проверки HMAC
    -- над точными байтами тела. Payload его подделать не может: контекст строится сервером.
    IF (p_context #> '{transport,signatureVerified}') IS DISTINCT FROM 'true'::jsonb
       OR (p_context #> '{transport,signatureVersion}') IS DISTINCT FROM '1'::jsonb
    THEN
        RETURN api.action_fail('receipt.signature_required', 'receipt requires a verified transport signature');
    END IF;

    IF (p_payload -> 'version') IS DISTINCT FROM '1'::jsonb
       OR jsonb_typeof(p_payload -> 'messageId') IS DISTINCT FROM 'string'
       OR jsonb_typeof(p_payload -> 'externalRequestId') IS DISTINCT FROM 'string'
       OR jsonb_typeof(p_payload -> 'providerPaymentId') IS DISTINCT FROM 'string'
       OR jsonb_typeof(p_payload -> 'occurredAt') IS DISTINCT FROM 'string'
       OR coalesce(p_payload ->> 'outcome', '') NOT IN ('COMPLETED', 'REJECTED')
       OR length(v_message_id) NOT BETWEEN 1 AND 128
       OR v_message_id ~ '[\r\n]'
    THEN
        RETURN api.action_fail('payload.invalid', 'receipt does not match receipt v1');
    END IF;

    -- Отпечаток точных байт HTTP-тела, посчитанный C#-границей. Для вызова в обход HTTP
    -- (его нет, но граница не должна от этого зависеть) используется отпечаток jsonb.
    v_body_hash := CASE
        WHEN (p_context ->> 'payloadHash') ~ '^[0-9a-f]{64}$' THEN p_context ->> 'payloadHash'
        ELSE api.payload_hash(p_payload)
    END;

    IF EXISTS (SELECT 1 FROM delivery.inbox WHERE message_id = v_message_id) THEN
        RETURN payment.receipt_replay(p_context, v_message_id, v_body_hash);
    END IF;

    SELECT * INTO v_request
    FROM payment.external_request
    WHERE external_request_id = v_external_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.action_fail('receipt.external_request_not_found', 'externalRequestId is not known');
    END IF;

    -- Порядок блокировок: external_request -> operation -> process. Шаги процесса берут операцию
    -- FOR UPDATE, а затем finish_job — процесс. Вставки ниже неявно берут FOR KEY SHARE на строки
    -- процесса (inbox, receipt) и операции (receipt, operation_event); без явной блокировки
    -- операции первой квитанция держала бы процесс и ждала операцию, а шаг — наоборот, и
    -- PostgreSQL прервал бы одну из транзакций как deadlock.
    PERFORM 1
    FROM payment.operation o
    WHERE o.operation_id = v_request.operation_id
    FOR UPDATE;

    INSERT INTO delivery.inbox (message_id, body_hash, process_id, signal_type, state)
    VALUES (v_message_id, v_body_hash, v_request.process_id, 'payment.receipt', 'RECEIVED')
    ON CONFLICT (message_id) DO NOTHING;

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    IF v_rows = 0 THEN
        -- Конкурентная доставка того же messageId другой командой успела раньше.
        RETURN payment.receipt_replay(p_context, v_message_id, v_body_hash);
    END IF;

    INSERT INTO payment.receipt (
        message_id, external_request_id, operation_id, process_id, message_version, outcome,
        provider_payment_id, occurred_at_raw, signature_valid, signature_version, body_hash
    )
    VALUES (
        v_message_id, v_request.external_request_id, v_request.operation_id, v_request.process_id,
        1, p_payload ->> 'outcome', p_payload ->> 'providerPaymentId', p_payload ->> 'occurredAt',
        true, 1, v_body_hash
    );

    -- Квитанция подтверждает доставку, даже если dispatcher ещё не отчитался: его опоздавший
    -- succeed/fail увидит CONFIRMED и ничего не изменит. Порядок блокировок external_request ->
    -- outbox совпадает с delivery.succeed_outbox.
    UPDATE delivery.outbox
    SET state = 'CONFIRMED', confirmed_at = clock_timestamp(), lease_owner = NULL, lease_until = NULL
    WHERE external_request_id = v_request.external_request_id
      AND state <> 'CONFIRMED';

    UPDATE payment.external_request
    SET state = 'CONFIRMED'
    WHERE external_request_id = v_request.external_request_id
      AND state <> 'CONFIRMED';

    PERFORM payment.record_event(v_request.operation_id, 'RECEIPT_RECEIVED', v_body_hash);

    -- Ответ после durable commit: сигнал применит согласователь (или вход в ожидание).
    RETURN api.action_ok(p_context, 'RECEIVED', jsonb_build_object(
        'messageId', v_message_id,
        'externalRequestId', v_request.external_request_id,
        'state', 'RECEIVED'
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- workflow.manual v1: решение, событие и следующий job — одна транзакция.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.manual_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_process_id  uuid := api.try_uuid(p_payload ->> 'processId');
    v_step_id     uuid := api.try_uuid(p_payload ->> 'stepInstanceId');
    v_decision    text := p_payload ->> 'decision';
    v_reason      text := p_payload ->> 'reason';
    v_principal   text := p_context ->> 'principal';
    v_process     workflow.process_instance%ROWTYPE;
    v_instance    workflow.step_instance%ROWTYPE;
    v_definition  workflow.step_definition%ROWTYPE;
    v_next        text;
    v_decision_id uuid;
BEGIN
    IF v_process_id IS NULL OR v_step_id IS NULL
       OR coalesce(v_decision, '') NOT IN ('APPROVED', 'REJECTED')
       OR jsonb_typeof(p_payload -> 'reason') IS DISTINCT FROM 'string'
       OR length(v_reason) NOT BETWEEN 1 AND 500
    THEN
        RETURN api.action_fail('payload.invalid', 'manual decision payload is invalid');
    END IF;

    IF coalesce(v_principal, '') = '' OR length(v_principal) > 128 THEN
        RETURN api.action_fail('access.denied', 'trusted context does not carry a principal');
    END IF;

    -- Блокировка процесса сериализует конкурирующие решения: второе увидит уже принятое.
    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE process_id = v_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.action_fail('workflow.process_not_found', 'process is not found');
    END IF;

    SELECT * INTO v_instance
    FROM workflow.step_instance
    WHERE step_instance_id = v_step_id
      AND process_id = v_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.action_fail('workflow.step_not_found', 'step instance does not belong to the process');
    END IF;

    IF v_instance.step_type <> 'MANUAL' THEN
        RETURN api.action_fail('workflow.step_not_manual', 'step instance is not a manual step');
    END IF;

    IF v_instance.state = 'COMPLETED'
       OR EXISTS (SELECT 1 FROM workflow.decision WHERE step_instance_id = v_step_id)
    THEN
        RETURN api.action_fail('workflow.decision_conflict', 'manual step already has an accepted decision');
    END IF;

    IF v_instance.state <> 'WAITING'
       OR v_process.state <> 'WAITING_MANUAL'
       OR v_process.current_step_key IS DISTINCT FROM v_instance.step_key
    THEN
        RETURN api.action_fail('workflow.step_not_waiting', 'manual step is not the current waiting step');
    END IF;

    SELECT * INTO v_definition
    FROM workflow.step_definition
    WHERE step_definition_id = v_instance.step_definition_id;

    SELECT to_step_key INTO v_next
    FROM workflow.transition_definition
    WHERE flow_version_id = v_process.flow_version_id
      AND from_step_key = v_definition.step_key
      AND outcome = v_decision;

    IF NOT (v_definition.allowed_outcomes ? v_decision) OR v_next IS NULL THEN
        RETURN api.action_fail('workflow.outcome_not_allowed', 'decision is not allowed by the pinned flow version');
    END IF;

    INSERT INTO workflow.decision (process_id, step_instance_id, source, principal, reason_hash, outcome)
    VALUES (v_process_id, v_step_id, 'MANUAL', v_principal, api.sha256_text(v_reason), v_decision)
    RETURNING decision_id INTO v_decision_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = v_decision, completed_at = clock_timestamp()
    WHERE step_instance_id = v_step_id;

    PERFORM workflow.record_event(
        v_process_id, v_step_id, 'DecisionRecorded',
        jsonb_build_object('decisionId', v_decision_id, 'source', 'MANUAL', 'outcome', v_decision));
    PERFORM workflow.record_event(
        v_process_id, v_step_id, 'StepCompleted',
        jsonb_build_object('stepKey', v_definition.step_key, 'outcome', v_decision));

    PERFORM workflow.enter_step(v_process_id, v_next);

    RETURN api.action_ok(p_context, v_decision, jsonb_build_object(
        'decisionId', v_decision_id,
        'processId', v_process_id,
        'stepInstanceId', v_step_id,
        'decision', v_decision,
        'source', 'MANUAL',
        'principal', v_principal
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Фиксированные SQL-границы Python-периметра
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION delivery.active_policy()
RETURNS delivery.delivery_policy
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT p.*
    FROM delivery.delivery_policy p
    WHERE p.profile = CASE
        WHEN coalesce((SELECT s.test_profile FROM workflow.runtime_setting s WHERE s.singleton), false)
        THEN 'test' ELSE 'default' END;
$fn$;

CREATE OR REPLACE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)
RETURNS TABLE (
    outbox_id uuid,
    lease_version bigint,
    external_request_id text,
    correlation_id uuid,
    amount text,
    currency text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
#variable_conflict use_column
DECLARE
    v_limit  integer := least(greatest(coalesce(p_limit, 1), 1), 16);
    v_policy delivery.delivery_policy%ROWTYPE;
    v_id     uuid;
    v_row    delivery.outbox%ROWTYPE;
BEGIN
    IF coalesce(p_owner, '') = '' OR length(p_owner) > 128 OR p_owner ~ '[\r\n]' THEN
        RAISE EXCEPTION 'outbox owner is required'
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    v_policy := delivery.active_policy();

    FOR v_id IN
        SELECT o.outbox_id
        FROM delivery.outbox o
        WHERE (o.state IN ('PENDING', 'RETRY_WAIT') AND o.next_attempt_at <= clock_timestamp())
           OR (o.state = 'LEASED' AND o.lease_until <= clock_timestamp())
        ORDER BY o.next_attempt_at, o.created_at
        LIMIT v_limit
        FOR UPDATE SKIP LOCKED
    LOOP
        SELECT * INTO v_row FROM delivery.outbox o WHERE o.outbox_id = v_id;

        -- Истёкшая аренда означает попытку без отчёта. Она расходует бюджет попыток так же, как
        -- transport error: иначе исчезающий dispatcher перезахватывал бы доставку вечно.
        IF v_row.state = 'LEASED' AND v_row.attempt_count >= v_policy.max_attempts THEN
            UPDATE delivery.outbox o
            SET state = 'DEAD', last_error_code = 'lease.expired.terminal', lease_owner = NULL, lease_until = NULL
            WHERE o.outbox_id = v_id;
            CONTINUE;
        END IF;

        -- Перехват истёкшей аренды оставляет след: предыдущая попытка могла дойти до provider, и
        -- без кода в проекции её нельзя было бы отличить от доставки, у которой отказов не было.
        UPDATE delivery.outbox o
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = o.lease_version + 1,
            lease_until = clock_timestamp() + make_interval(secs => v_policy.lease_ms / 1000.0),
            attempt_count = o.attempt_count + 1,
            last_error_code = CASE WHEN v_row.state = 'LEASED' THEN 'lease.expired.retryable' ELSE o.last_error_code END
        WHERE o.outbox_id = v_id
        RETURNING o.* INTO v_row;

        outbox_id := v_row.outbox_id;
        lease_version := v_row.lease_version;
        external_request_id := v_row.external_request_id;
        correlation_id := v_row.correlation_id;
        amount := v_row.amount;
        currency := v_row.currency;
        RETURN NEXT;
    END LOOP;
END;
$fn$;

CREATE OR REPLACE FUNCTION delivery.succeed_outbox(
    p_outbox_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_provider_payment_id text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_external_id text;
    v_row         delivery.outbox%ROWTYPE;
BEGIN
    SELECT o.external_request_id INTO v_external_id FROM delivery.outbox o WHERE o.outbox_id = p_outbox_id;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.outbox_not_found');
    END IF;

    -- Тот же порядок блокировок, что у receipt.accept: external_request, затем outbox.
    PERFORM 1 FROM payment.external_request e WHERE e.external_request_id = v_external_id FOR UPDATE;
    SELECT * INTO v_row FROM delivery.outbox o WHERE o.outbox_id = p_outbox_id FOR UPDATE;

    IF v_row.state = 'CONFIRMED' THEN
        RETURN jsonb_build_object('status', 'ok', 'applied', false, 'state', v_row.state);
    END IF;

    IF v_row.state <> 'LEASED'
       OR v_row.lease_owner IS DISTINCT FROM p_owner
       OR v_row.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.lease_stale', 'state', v_row.state);
    END IF;

    IF coalesce(p_provider_payment_id, '') = ''
       OR length(p_provider_payment_id) > 128
       OR p_provider_payment_id ~ '[\r\n]'
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.provider_payment_id_invalid', 'state', v_row.state);
    END IF;

    UPDATE delivery.outbox o
    SET state = 'DELIVERED',
        delivered_at = clock_timestamp(),
        provider_payment_id = p_provider_payment_id,
        lease_owner = NULL,
        lease_until = NULL
    WHERE o.outbox_id = p_outbox_id;

    UPDATE payment.external_request e
    SET state = 'SENT'
    WHERE e.external_request_id = v_external_id
      AND e.state = 'CREATED';

    RETURN jsonb_build_object('status', 'ok', 'applied', true, 'state', 'DELIVERED');
END;
$fn$;

CREATE OR REPLACE FUNCTION delivery.fail_outbox(
    p_outbox_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_row       delivery.outbox%ROWTYPE;
    v_policy    delivery.delivery_policy%ROWTYPE;
    v_status    integer;
    v_retryable boolean;
    v_code      text;
    v_delay_ms  integer;
BEGIN
    SELECT * INTO v_row FROM delivery.outbox o WHERE o.outbox_id = p_outbox_id FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.outbox_not_found');
    END IF;

    IF v_row.state = 'CONFIRMED' THEN
        RETURN jsonb_build_object('status', 'ok', 'applied', false, 'state', v_row.state);
    END IF;

    IF v_row.state <> 'LEASED'
       OR v_row.lease_owner IS DISTINCT FROM p_owner
       OR v_row.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.lease_stale', 'state', v_row.state);
    END IF;

    -- Повторяемость решает PostgreSQL по самому факту, а не по суффиксу, присланному клиентом.
    IF p_error_code = 'transport.error.retryable' THEN
        v_retryable := true;
        v_code := p_error_code;
    ELSIF p_error_code ~ '^http\.[1-5][0-9]{2}\.(retryable|terminal)$' THEN
        v_status := substring(p_error_code FROM '^http\.([0-9]{3})\.')::integer;
        v_retryable := v_status IN (408, 429) OR v_status BETWEEN 500 AND 599;
        v_code := 'http.' || v_status || CASE WHEN v_retryable THEN '.retryable' ELSE '.terminal' END;
    ELSIF p_error_code = 'response.invalid.terminal' THEN
        v_retryable := false;
        v_code := p_error_code;
    ELSE
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.error_code_invalid', 'state', v_row.state);
    END IF;

    v_policy := delivery.active_policy();

    IF v_retryable AND v_row.attempt_count < v_policy.max_attempts THEN
        v_delay_ms := coalesce(
            (v_policy.delays_ms ->> (v_row.attempt_count - 1))::integer,
            (v_policy.delays_ms ->> (jsonb_array_length(v_policy.delays_ms) - 1))::integer,
            1000);

        UPDATE delivery.outbox o
        SET state = 'RETRY_WAIT',
            last_error_code = v_code,
            next_attempt_at = clock_timestamp() + make_interval(secs => v_delay_ms / 1000.0),
            lease_owner = NULL,
            lease_until = NULL
        WHERE o.outbox_id = p_outbox_id;

        RETURN jsonb_build_object('status', 'ok', 'applied', true, 'state', 'RETRY_WAIT', 'nextAttemptInMs', v_delay_ms);
    END IF;

    UPDATE delivery.outbox o
    SET state = 'DEAD',
        last_error_code = v_code,
        lease_owner = NULL,
        lease_until = NULL
    WHERE o.outbox_id = p_outbox_id;

    RETURN jsonb_build_object('status', 'ok', 'applied', true, 'state', 'DEAD');
END;
$fn$;

CREATE OR REPLACE FUNCTION delivery.reconcile_inbox(p_limit integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_limit     integer := least(greatest(coalesce(p_limit, 1), 1), 100);
    v_candidate record;
    v_message   delivery.inbox%ROWTYPE;
    v_applied   integer := 0;
BEGIN
    -- Кандидаты — только сообщения, по которым уже можно принять решение: процесс ждёт этот
    -- сигнал или сообщение опоздало навсегда. Раннее сообщение в выборку не попадает и не
    -- загораживает очередь.
    FOR v_candidate IN
        SELECT i.message_id, i.process_id, i.signal_type
        FROM delivery.inbox i
        WHERE i.state = 'RECEIVED'
          AND (delivery.process_waits_for(i.process_id, i.signal_type)
               OR delivery.message_superseded(i.process_id, i.signal_type))
        ORDER BY i.received_at, i.message_id
        LIMIT v_limit
    LOOP
        PERFORM 1 FROM workflow.process_instance p WHERE p.process_id = v_candidate.process_id FOR UPDATE;

        SELECT * INTO v_message
        FROM delivery.inbox i
        WHERE i.message_id = v_candidate.message_id
          AND i.state = 'RECEIVED'
        FOR UPDATE SKIP LOCKED;

        CONTINUE WHEN NOT FOUND;

        IF delivery.process_waits_for(v_message.process_id, v_message.signal_type) THEN
            IF delivery.materialize_signal(v_message.message_id) = 'APPLIED' THEN
                PERFORM workflow.apply_pending_signal(v_message.process_id);
                v_applied := v_applied + 1;
            END IF;
        ELSIF delivery.message_superseded(v_message.process_id, v_message.signal_type) THEN
            UPDATE delivery.inbox SET state = 'CONFLICT' WHERE message_id = v_message.message_id;
        END IF;
    END LOOP;

    RETURN v_applied;
END;
$fn$;
