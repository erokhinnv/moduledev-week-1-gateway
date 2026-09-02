-- 012_workflow_engine.sql
-- Исполняющее ядро workflow: продвижение процесса, аренда (lease), попытки и сигналы.
--
-- Всё, что меняет состояние процесса, живёт здесь, в PostgreSQL, а не в C#. Worker остаётся
-- сменным: он умеет только «взять задание», «вызвать зарегистрированное действие» и «сообщить
-- исход». Решение о том, куда пойдёт процесс дальше, принимается по данным опубликованной карты.

-------------------------------------------------------------------------------
-- Конверты ответов функций workflow (та же форма, что у функций каталога api).
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.ok(p_result jsonb)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object('status', 'ok', 'result', coalesce(p_result, '{}'::jsonb));
$fn$;

CREATE OR REPLACE FUNCTION workflow.failure(p_code text, p_message text)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message,
        'retryable', false,
        'details', '{}'::jsonb
    );
$fn$;

-- Одна запись истории. История только дополняется, поэтому это единственный способ её изменить.
CREATE OR REPLACE FUNCTION workflow.record_event(
    p_process_id uuid,
    p_step_instance_id uuid,
    p_event_type text,
    p_payload jsonb DEFAULT '{}'::jsonb
)
RETURNS void
LANGUAGE sql
SET search_path = pg_catalog
AS $fn$
    INSERT INTO workflow.workflow_event (process_id, step_instance_id, event_type, payload)
    VALUES (p_process_id, p_step_instance_id, p_event_type, coalesce(p_payload, '{}'::jsonb));
$fn$;

-------------------------------------------------------------------------------
-- Вход в шаг.
--
-- Единственная точка, где процесс попадает в новый узел карты. Тип шага целиком определяет
-- поведение, и ни один тип не «зашит» под конкретное имя шага или карты.
-------------------------------------------------------------------------------
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

    -- WAIT_SIGNAL. Ожидание устойчиво: оно фиксируется в базе до всякой попытки применить
    -- сигнал, поэтому перезапуск worker в этот момент ничего не теряет.
    UPDATE workflow.process_instance
    SET state = 'WAITING_SIGNAL', current_step_key = v_step.step_key, updated_at = now()
    WHERE process_id = p_process_id;

    -- Сигнал мог прийти РАНЬШЕ входа в ожидание: тогда он лежит со статусом ACCEPTED и
    -- применяется здесь же, в этой транзакции.
    PERFORM workflow.apply_pending_signal(p_process_id);
END;
$fn$;

-------------------------------------------------------------------------------
-- Применение накопленного сигнала к процессу, стоящему в wait_signal.
-- Возвращает true, если сигнал был применён и процесс продвинулся.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.apply_pending_signal(p_process_id uuid)
RETURNS boolean
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_process  workflow.process_instance%ROWTYPE;
    v_step     workflow.step_definition%ROWTYPE;
    v_instance workflow.step_instance%ROWTYPE;
    v_signal   workflow.workflow_signal%ROWTYPE;
    v_next     text;
BEGIN
    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE process_id = p_process_id
    FOR UPDATE;

    IF NOT FOUND OR v_process.state <> 'WAITING_SIGNAL' THEN
        RETURN false;
    END IF;

    SELECT * INTO v_step
    FROM workflow.step_definition
    WHERE flow_version_id = v_process.flow_version_id
      AND step_key = v_process.current_step_key
      AND step_type = 'WAIT_SIGNAL';

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    SELECT * INTO v_instance
    FROM workflow.step_instance
    WHERE process_id = p_process_id
      AND step_key = v_process.current_step_key
      AND state = 'WAITING';

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    SELECT * INTO v_signal
    FROM workflow.workflow_signal
    WHERE process_id = p_process_id
      AND signal_type = v_step.signal_type
      AND status = 'ACCEPTED'
    ORDER BY received_at, message_id
    LIMIT 1
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN false;
    END IF;

    UPDATE workflow.workflow_signal
    SET status = 'APPLIED', applied_at = clock_timestamp()
    WHERE message_id = v_signal.message_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = v_step.outcome, completed_at = clock_timestamp()
    WHERE step_instance_id = v_instance.step_instance_id;

    PERFORM workflow.record_event(
        p_process_id, v_instance.step_instance_id, 'SignalApplied',
        jsonb_build_object('signalType', v_step.signal_type, 'messageId', v_signal.message_id)
    );
    PERFORM workflow.record_event(
        p_process_id, v_instance.step_instance_id, 'StepCompleted',
        jsonb_build_object('stepKey', v_step.step_key, 'outcome', v_step.outcome)
    );

    SELECT to_step_key INTO v_next
    FROM workflow.transition_definition
    WHERE flow_version_id = v_process.flow_version_id
      AND from_step_key = v_step.step_key
      AND outcome = v_step.outcome;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'pinned flow version has no transition for outcome %', v_step.outcome
            USING ERRCODE = 'no_data_found';
    END IF;

    PERFORM workflow.enter_step(p_process_id, v_next);
    RETURN true;
END;
$fn$;

-------------------------------------------------------------------------------
-- Запуск процесса. Идемпотентен по паре (flow, business_key).
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.start_process(
    p_flow_name text,
    p_business_key text,
    p_data jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_flow     workflow.flow_definition%ROWTYPE;
    v_version  workflow.flow_version%ROWTYPE;
    v_process  workflow.process_instance%ROWTYPE;
    v_data     jsonb := coalesce(p_data, '{}'::jsonb);
    v_hash     text;
    v_process_id uuid;
BEGIN
    IF jsonb_typeof(v_data) <> 'object' THEN
        RETURN workflow.failure('workflow.data_invalid', 'process data must be a JSON object');
    END IF;

    IF coalesce(p_business_key, '') = '' THEN
        RETURN workflow.failure('workflow.business_key_required', 'business key is required');
    END IF;

    v_hash := api.payload_hash(v_data);

    SELECT * INTO v_flow FROM workflow.flow_definition WHERE flow_name = p_flow_name;
    IF NOT FOUND THEN
        RETURN workflow.failure('workflow.flow_not_found', 'flow is not published');
    END IF;

    -- Повтор идентичного запуска обязан вернуть ПРЕЖНИЙ процесс, а изменённые данные — конфликт.
    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE flow_id = v_flow.flow_id AND business_key = p_business_key;

    IF FOUND THEN
        IF v_process.data_hash <> v_hash THEN
            RETURN workflow.failure(
                'workflow.start_conflict',
                'business key was already started with different process data');
        END IF;

        RETURN workflow.ok(workflow.process_summary(v_process.process_id));
    END IF;

    SELECT * INTO v_version
    FROM workflow.flow_version
    WHERE flow_id = v_flow.flow_id AND is_active;

    IF NOT FOUND THEN
        RETURN workflow.failure('workflow.flow_inactive', 'flow has no active published version');
    END IF;

    BEGIN
        INSERT INTO workflow.process_instance (
            flow_id, flow_version_id, business_key, data, data_hash, state
        )
        VALUES (
            v_flow.flow_id, v_version.flow_version_id, p_business_key, v_data, v_hash, 'CREATED'
        )
        RETURNING process_id INTO v_process_id;
    EXCEPTION WHEN unique_violation THEN
        -- Гонка двух одинаковых стартов: победила чужая транзакция, разбираем её результат
        -- ровно теми же правилами, что и обычный повтор.
        SELECT * INTO v_process
        FROM workflow.process_instance
        WHERE flow_id = v_flow.flow_id AND business_key = p_business_key;

        IF NOT FOUND THEN
            RETURN workflow.failure('workflow.start_conflict', 'process could not be started');
        END IF;

        IF v_process.data_hash <> v_hash THEN
            RETURN workflow.failure(
                'workflow.start_conflict',
                'business key was already started with different process data');
        END IF;

        RETURN workflow.ok(workflow.process_summary(v_process.process_id));
    END;

    PERFORM workflow.record_event(
        v_process_id, NULL, 'ProcessStarted',
        jsonb_build_object('businessKey', p_business_key, 'flowVersion', v_version.version)
    );

    -- До возврата процесс уже стоит либо в устойчивом ожидании, либо с созданным заданием.
    PERFORM workflow.enter_step(v_process_id, v_version.start_step_key);

    RETURN workflow.ok(workflow.process_summary(v_process_id));
END;
$fn$;

-- Краткая карточка процесса — общий вид для flow start и flow get.
CREATE OR REPLACE FUNCTION workflow.process_summary(p_process_id uuid)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'resource', 'process',
        'processId', p.process_id,
        'businessKey', p.business_key,
        'flowName', f.flow_name,
        'flowVersion', v.version,
        'state', p.state,
        'currentStepKey', p.current_step_key
    )
    FROM workflow.process_instance p
    JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
    JOIN workflow.flow_version v ON v.flow_version_id = p.flow_version_id
    WHERE p.process_id = p_process_id;
$fn$;

-------------------------------------------------------------------------------
-- Приём сигнала. message_id глобально уникален; повтор того же тела — duplicate,
-- любое расхождение — конфликт без изменения сохранённой строки.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.accept_signal(
    p_process_id uuid,
    p_signal_type text,
    p_message_id text,
    p_body jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_process  workflow.process_instance%ROWTYPE;
    v_existing workflow.workflow_signal%ROWTYPE;
    v_body     jsonb := coalesce(p_body, '{}'::jsonb);
    v_hash     text;
    v_declared boolean;
BEGIN
    IF jsonb_typeof(v_body) <> 'object' THEN
        RETURN workflow.failure('workflow.signal_invalid', 'signal payload must be a JSON object');
    END IF;

    IF coalesce(p_message_id, '') = '' OR coalesce(p_signal_type, '') = '' THEN
        RETURN workflow.failure('workflow.signal_invalid', 'signal type and message id are required');
    END IF;

    v_hash := api.payload_hash(v_body);

    -- Сначала разбираем сам message_id: он глобален, поэтому проверка не зависит от процесса.
    SELECT * INTO v_existing
    FROM workflow.workflow_signal
    WHERE message_id = p_message_id
    FOR UPDATE;

    IF FOUND THEN
        IF v_existing.process_id = p_process_id
           AND v_existing.signal_type = p_signal_type
           AND v_existing.body_hash = v_hash
        THEN
            RETURN workflow.ok(jsonb_build_object(
                'resource', 'signal',
                'processId', v_existing.process_id,
                'messageId', v_existing.message_id,
                'signalType', v_existing.signal_type,
                'status', 'duplicate'
            ));
        END IF;

        RETURN workflow.failure(
            'workflow.signal_conflict',
            'message id was already used for a different signal');
    END IF;

    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE process_id = p_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN workflow.failure('workflow.process_not_found', 'process does not exist');
    END IF;

    -- Тип сигнала обязан быть объявлен в ЗАКРЕПЛЁННОЙ версии карты этого процесса.
    SELECT EXISTS (
        SELECT 1 FROM workflow.step_definition
        WHERE flow_version_id = v_process.flow_version_id
          AND step_type = 'WAIT_SIGNAL'
          AND signal_type = p_signal_type
    ) INTO v_declared;

    IF NOT v_declared THEN
        RETURN workflow.failure(
            'workflow.signal_type_unknown',
            'signal type is not declared by the pinned flow version');
    END IF;

    BEGIN
        INSERT INTO workflow.workflow_signal (
            message_id, process_id, signal_type, body, body_hash, status
        )
        VALUES (p_message_id, p_process_id, p_signal_type, v_body, v_hash, 'ACCEPTED');
    EXCEPTION WHEN unique_violation THEN
        RETURN workflow.failure(
            'workflow.signal_conflict',
            'message id was already used for a different signal');
    END;

    PERFORM workflow.record_event(
        p_process_id, NULL, 'SignalAccepted',
        jsonb_build_object('signalType', p_signal_type, 'messageId', p_message_id)
    );

    -- Процесс, уже стоящий в соответствующем ожидании, продвигается прямо здесь. Завершённый
    -- процесс сигнал только сохраняет и никуда не двигает.
    PERFORM workflow.apply_pending_signal(p_process_id);

    RETURN workflow.ok(jsonb_build_object(
        'resource', 'signal',
        'processId', p_process_id,
        'messageId', p_message_id,
        'signalType', p_signal_type,
        'status', 'accepted'
    ));
END;
$fn$;
