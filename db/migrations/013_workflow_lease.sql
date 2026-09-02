-- 013_workflow_lease.sql
-- Аренда заданий, fencing и завершение попыток.
--
-- Три функции этого файла — единственное, что вообще разрешено вызывать роли workflow_worker,
-- помимо api.invoke. Worker не имеет прямого DML: он не может ни продвинуть процесс, ни
-- дописать историю в обход этих границ.

-------------------------------------------------------------------------------
-- Захват готовых заданий короткой транзакцией.
--
-- FOR UPDATE SKIP LOCKED означает, что конкурирующий worker не встаёт в очередь за строкой, а
-- сразу берёт следующую: два worker'а не блокируют друг друга и не делят одно задание.
-- Право на завершение выдаётся вместе с новым lease_version — именно оно потом сверяется в
-- finish_job и делает «опоздавшее» завершение невозможным.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.claim_jobs(
    p_owner text,
    p_limit integer DEFAULT 1,
    p_lease_ms integer DEFAULT 30000
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    -- Запас поверх объявленного бюджета действия: он покрывает ожидание соединения, сборку
    -- payload, проверку схемы запроса и саму границу завершения.
    c_lease_margin_ms constant integer := 500;

    -- Во сколько раз производный срок аренды может превысить настроенный профилем. Без потолка
    -- срок становился бы функцией карты: задача с бюджетом в десятки секунд отодвигала бы
    -- перехват настолько же, и наблюдаемое время восстановления перестало бы зависеть от
    -- профиля вообще. Свыше потолка защита от повторного эффекта держится не арендой, а
    -- ключом идемпотентности действия и абсолютным потолком попыток.
    c_lease_factor constant integer := 3;

    -- Значения потолка попыток по умолчанию. Они дублируют DEFAULT колонок runtime_setting не по
    -- невнимательности: потолок — предохранитель, и он не имеет права зависеть от того, лежит ли
    -- в таблице строка. Без этих значений отсутствующая настройка давала бы NULL, сравнение
    -- attempt_count >= NULL — тоже NULL, и предохранитель молча переставал бы существовать.
    c_default_ceiling_factor  constant integer := 3;
    c_default_ceiling_minimum constant integer := 10;

    v_now        timestamptz := clock_timestamp();
    v_floor_ms   integer := greatest(coalesce(p_lease_ms, 30000), 250);
    v_lease      interval;
    v_lease_ms   integer;
    v_limit      integer := least(greatest(coalesce(p_limit, 1), 1), 16);
    v_ids        uuid[];
    v_id         uuid;
    v_job        workflow.workflow_job%ROWTYPE;
    v_task       workflow.task_definition%ROWTYPE;
    v_attempt_id uuid;
    v_expired    integer;
    v_setting    workflow.runtime_setting%ROWTYPE;
    v_cap        integer;
    v_descriptor jsonb;
    v_result     jsonb := '[]'::jsonb;
BEGIN
    IF coalesce(p_owner, '') = '' THEN
        RAISE EXCEPTION 'lease owner is required'
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    SELECT array_agg(candidate.job_id) INTO v_ids
    FROM (
        SELECT j.job_id
        FROM workflow.workflow_job j
        WHERE (j.state IN ('READY', 'RETRY_WAIT') AND j.next_attempt_at <= v_now)
           OR (j.state = 'LEASED' AND j.lease_until IS NOT NULL AND j.lease_until <= v_now)
        ORDER BY j.next_attempt_at, j.created_at
        FOR UPDATE SKIP LOCKED
        LIMIT v_limit
    ) AS candidate;

    IF v_ids IS NULL THEN
        RETURN v_result;
    END IF;

    SELECT * INTO v_setting FROM workflow.runtime_setting WHERE singleton;

    FOREACH v_id IN ARRAY v_ids
    LOOP
        -- Закреплённый картой контракт задачи нужен ДО выдачи аренды: из него берётся бюджет
        -- времени, а значит и минимальный разумный срок аренды.
        SELECT t.* INTO v_task
        FROM workflow.workflow_job j
        JOIN workflow.step_instance si ON si.step_instance_id = j.step_instance_id
        JOIN workflow.task_definition t ON t.step_definition_id = si.step_definition_id
        WHERE j.job_id = v_id;

        -- Абсолютный потолок попыток. Бюджет max_attempts тратят только отказы действия,
        -- поэтому сбой, который не доходит до fail_job (например исключение внутри границы
        -- завершения), иначе перезахватывался бы вечно. Потолок делает такой job наблюдаемо
        -- мёртвым вместо бесконечно живого — и помечается ОТДЕЛЬНОЙ причиной смерти, чтобы не
        -- выдавать «задание не доходит до отчёта» за «действие стабильно отказывает».
        v_cap := greatest(
            coalesce(v_task.max_attempts, 1)
                * coalesce(v_setting.attempt_ceiling_factor, c_default_ceiling_factor),
            coalesce(v_setting.attempt_ceiling_minimum, c_default_ceiling_minimum));

        SELECT * INTO v_job FROM workflow.workflow_job WHERE job_id = v_id;

        IF v_job.attempt_count >= v_cap THEN
            UPDATE workflow.task_attempt
            SET status = 'STALE', finished_at = clock_timestamp()
            WHERE job_id = v_id AND status = 'RUNNING';

            UPDATE workflow.workflow_job
            SET state = 'DEAD',
                dead_reason = 'attempt_ceiling',
                lease_owner = NULL,
                lease_until = NULL,
                updated_at = now()
            WHERE job_id = v_id;

            UPDATE workflow.step_instance
            SET state = 'FAILED', completed_at = clock_timestamp()
            WHERE step_instance_id = v_job.step_instance_id
              AND state NOT IN ('COMPLETED', 'FAILED');

            UPDATE workflow.process_instance
            SET state = 'FAILED', updated_at = now()
            WHERE process_id = v_job.process_id
              AND state NOT IN ('COMPLETED', 'FAILED');

            PERFORM workflow.record_event(
                v_job.process_id, v_job.step_instance_id, 'TaskAbandoned',
                jsonb_build_object(
                    'jobId', v_job.job_id,
                    'errorCode', 'workflow.attempt_ceiling_reached',
                    'attemptCount', v_job.attempt_count,
                    'attemptCeiling', v_cap
                )
            );
            PERFORM workflow.record_event(
                v_job.process_id, v_job.step_instance_id, 'ProcessFailed',
                jsonb_build_object('errorCode', 'workflow.attempt_ceiling_reached')
            );

            CONTINUE;
        END IF;

        -- Аренда должна переживать объявленный бюджет действия, иначе второй исполнитель
        -- перехватывает задание на середине работы первого. Но не любой ценой: сверху срок
        -- ограничен кратностью к профилю, чтобы карта не могла задать наблюдаемое время
        -- восстановления. За этим потолком повторное исполнение безопасно по другой причине —
        -- executionId остаётся ключом идемпотентности предметного эффекта.
        v_lease_ms := least(
            greatest(v_floor_ms, coalesce(v_task.timeout_ms, 0) + c_lease_margin_ms),
            v_floor_ms * c_lease_factor);
        v_lease := make_interval(secs => v_lease_ms / 1000.0);

        -- Истёкшая аренда оставляет попытку в статусе STALE: она увеличила attempt_count, но
        -- НЕ расходует бюджет отказов max_attempts — сбоя действия не было, было исчезновение
        -- арендатора.
        UPDATE workflow.task_attempt
        SET status = 'STALE', finished_at = clock_timestamp()
        WHERE job_id = v_id AND status = 'RUNNING';

        GET DIAGNOSTICS v_expired = ROW_COUNT;

        UPDATE workflow.workflow_job
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = lease_version + 1,
            lease_until = clock_timestamp() + v_lease,
            attempt_count = attempt_count + 1,
            updated_at = now()
        WHERE job_id = v_id
        RETURNING * INTO v_job;

        IF v_expired > 0 THEN
            PERFORM workflow.record_event(
                v_job.process_id, v_job.step_instance_id, 'TaskLeaseExpired',
                jsonb_build_object('jobId', v_job.job_id, 'leaseVersion', v_job.lease_version)
            );
        END IF;

        INSERT INTO workflow.task_attempt (
            job_id, execution_id, lease_version, attempt_number, status
        )
        VALUES (
            v_job.job_id, v_job.execution_id, v_job.lease_version, v_job.attempt_count, 'RUNNING'
        )
        RETURNING attempt_id INTO v_attempt_id;

        UPDATE workflow.step_instance
        SET state = 'RUNNING'
        WHERE step_instance_id = v_job.step_instance_id
          AND state <> 'RUNNING';

        PERFORM workflow.record_event(
            v_job.process_id, v_job.step_instance_id, 'TaskClaimed',
            jsonb_build_object(
                'jobId', v_job.job_id,
                'owner', p_owner,
                'leaseVersion', v_job.lease_version,
                'attemptNumber', v_job.attempt_count
            )
        );

        -- Всё, что нужно общему исполнителю действий, worker получает вместе с арендой:
        -- отдельного чтения каталога роли workflow_worker не требуется.
        SELECT jsonb_build_object(
            'jobId', j.job_id,
            'executionId', j.execution_id,
            'attemptId', v_attempt_id,
            'attemptNumber', j.attempt_count,
            'leaseVersion', j.lease_version,
            'leaseUntil', j.lease_until,
            'processId', j.process_id,
            'stepInstanceId', j.step_instance_id,
            'stepKey', si.step_key,
            'service', t.service,
            'module', t.module,
            'action', t.action,
            'actionVersion', t.action_version,
            'requiredPolicy', t.required_policy,
            'timeoutMs', t.timeout_ms,
            'maxAttempts', t.max_attempts,
            'delaysMs', t.delays_ms,
            'failureCount', j.failure_count,
            'inputMapping', t.input_mapping,
            'inputConstants', t.input_constants,
            'processData', p.data,
            'principal', w.principal,
            'consumer', w.consumer,
            'scopes', w.scopes,
            'actionAvailable', coalesce(av.enabled, false),
            'requestSchema', av.request_schema,
            'responseSchema', av.response_schema,
            'outcomes', av.outcomes
        )
        INTO v_descriptor
        FROM workflow.workflow_job j
        JOIN workflow.step_instance si ON si.step_instance_id = j.step_instance_id
        JOIN workflow.process_instance p ON p.process_id = j.process_id
        JOIN workflow.step_definition sd ON sd.step_definition_id = si.step_definition_id
        JOIN workflow.task_definition t ON t.step_definition_id = sd.step_definition_id
        JOIN workflow.worker_principal w ON w.principal = 'workflow-worker'
        LEFT JOIN api.action_version av
               ON av.module = t.module
              AND av.action = t.action
              AND av.version = t.action_version
        WHERE j.job_id = v_id;

        v_result := v_result || jsonb_build_array(v_descriptor);
    END LOOP;

    RETURN v_result;
END;
$fn$;

-------------------------------------------------------------------------------
-- Успешное завершение попытки.
--
-- Вызывается ВНУТРИ той же транзакции, что и предметный эффект действия: либо фиксируются оба,
-- либо ни одного. Проверка owner/leaseVersion/состояния идёт до любой записи, поэтому
-- «опоздавший» worker не меняет ни задание, ни процесс, ни предметные данные.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.finish_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_outcome text,
    p_result jsonb,
    p_dispatch jsonb DEFAULT NULL
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_job     workflow.workflow_job%ROWTYPE;
    v_process workflow.process_instance%ROWTYPE;
    v_step    workflow.step_definition%ROWTYPE;
    v_next    text;
BEGIN
    SELECT * INTO v_job
    FROM workflow.workflow_job
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN workflow.failure(
            'workflow.lease_stale',
            'the lease that authorised this completion is no longer valid');
    END IF;

    IF coalesce(p_outcome, '') = '' THEN
        RETURN workflow.failure('workflow.outcome_required', 'a declared outcome is required');
    END IF;

    SELECT * INTO v_process
    FROM workflow.process_instance
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    SELECT sd.* INTO v_step
    FROM workflow.step_instance si
    JOIN workflow.step_definition sd ON sd.step_definition_id = si.step_definition_id
    WHERE si.step_instance_id = v_job.step_instance_id;

    -- Неизвестный исход не выбирает запасной переход: он вообще не принимается.
    SELECT to_step_key INTO v_next
    FROM workflow.transition_definition
    WHERE flow_version_id = v_process.flow_version_id
      AND from_step_key = v_step.step_key
      AND outcome = p_outcome;

    IF NOT FOUND THEN
        RETURN workflow.failure(
            'workflow.unknown_outcome',
            'the pinned flow version declares no transition for this outcome');
    END IF;

    UPDATE workflow.task_attempt
    SET status = 'SUCCEEDED', outcome = p_outcome, finished_at = clock_timestamp()
    WHERE job_id = v_job.job_id
      AND lease_version = v_job.lease_version
      AND status = 'RUNNING';

    UPDATE workflow.workflow_job
    SET state = 'SUCCEEDED', lease_until = NULL, updated_at = now()
    WHERE job_id = v_job.job_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = p_outcome, completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id;

    PERFORM workflow.record_event(
        v_job.process_id, v_job.step_instance_id, 'TaskSucceeded',
        jsonb_build_object('jobId', v_job.job_id, 'outcome', p_outcome)
    );
    PERFORM workflow.record_event(
        v_job.process_id, v_job.step_instance_id, 'StepCompleted',
        jsonb_build_object('stepKey', v_step.step_key, 'outcome', p_outcome)
    );

    -- Технический след вызова пишется ЗДЕСЬ, в общей транзакции с предметным эффектом: откат
    -- действия не оставляет строки dispatch, а успех оставляет ровно одну.
    IF p_dispatch IS NOT NULL AND jsonb_typeof(p_dispatch) = 'object' THEN
        INSERT INTO api.dispatch (
            correlation_id, request_id, module, action, version,
            principal, consumer, payload_hash, status, outcome, duration_ms
        )
        VALUES (
            (p_dispatch ->> 'correlationId')::uuid,
            p_dispatch ->> 'requestId',
            p_dispatch ->> 'module',
            p_dispatch ->> 'action',
            (p_dispatch ->> 'version')::integer,
            p_dispatch ->> 'principal',
            p_dispatch ->> 'consumer',
            p_dispatch ->> 'payloadHash',
            'OK',
            p_outcome,
            (p_dispatch ->> 'durationMs')::integer
        );
    END IF;

    PERFORM workflow.enter_step(v_job.process_id, v_next);

    RETURN workflow.ok(jsonb_build_object(
        'resource', 'job',
        'jobId', v_job.job_id,
        'executionId', v_job.execution_id,
        'state', 'SUCCEEDED',
        'outcome', p_outcome,
        'process', workflow.process_summary(v_job.process_id)
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Неуспешная попытка: планирование повтора или окончательный отказ.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.fail_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text,
    p_retryable boolean DEFAULT false,
    p_dispatch jsonb DEFAULT NULL
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_job      workflow.workflow_job%ROWTYPE;
    v_task     workflow.task_definition%ROWTYPE;
    v_step_key text;
    v_failures integer;
    v_delay_ms integer;
    v_code     text := coalesce(nullif(p_error_code, ''), 'workflow.attempt_failed');
BEGIN
    SELECT * INTO v_job
    FROM workflow.workflow_job
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
       OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN workflow.failure(
            'workflow.lease_stale',
            'the lease that authorised this failure report is no longer valid');
    END IF;

    SELECT t.* INTO v_task
    FROM workflow.step_instance si
    JOIN workflow.task_definition t ON t.step_definition_id = si.step_definition_id
    WHERE si.step_instance_id = v_job.step_instance_id;

    SELECT si.step_key INTO v_step_key
    FROM workflow.step_instance si
    WHERE si.step_instance_id = v_job.step_instance_id;

    UPDATE workflow.task_attempt
    SET status = 'FAILED', error_code = v_code, finished_at = clock_timestamp()
    WHERE job_id = v_job.job_id
      AND lease_version = v_job.lease_version
      AND status = 'RUNNING';

    v_failures := v_job.failure_count + 1;

    -- Технический след неуспешного вызова. Он пишется ЭТОЙ транзакцией, а не откаченной
    -- транзакцией действия, поэтому переживает откат предметного эффекта. Строка появляется
    -- только если действие реально вызывалось: отказ маппинга или недоступность версии не
    -- являются вызовом и следа не оставляют.
    -- След неуспеха вспомогательный, поэтому кривой аргумент не должен ронять запись самой
    -- попытки: тогда задание осталось бы арендованным и ушло в перезахват. У успешного
    -- завершения (finish_job) обратный приоритет — там след обязателен, и его потеря должна
    -- быть громкой, а не тихой.
    IF p_dispatch IS NOT NULL
       AND jsonb_typeof(p_dispatch) = 'object'
       AND (p_dispatch ->> 'correlationId') ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
       AND (p_dispatch ->> 'payloadHash') ~ '^[0-9a-f]{64}$'
    THEN
        INSERT INTO api.dispatch (
            correlation_id, request_id, module, action, version,
            principal, consumer, payload_hash, status, error_code, duration_ms
        )
        VALUES (
            (p_dispatch ->> 'correlationId')::uuid,
            p_dispatch ->> 'requestId',
            p_dispatch ->> 'module',
            p_dispatch ->> 'action',
            (p_dispatch ->> 'version')::integer,
            p_dispatch ->> 'principal',
            p_dispatch ->> 'consumer',
            p_dispatch ->> 'payloadHash',
            'ERROR',
            v_code,
            (p_dispatch ->> 'durationMs')::integer
        );
    END IF;

    PERFORM workflow.record_event(
        v_job.process_id, v_job.step_instance_id, 'TaskAttemptFailed',
        jsonb_build_object(
            'jobId', v_job.job_id,
            'errorCode', v_code,
            'retryable', coalesce(p_retryable, false),
            'failureCount', v_failures
        )
    );

    IF coalesce(p_retryable, false) AND v_failures < v_task.max_attempts THEN
        -- После retryable-отказа номер N берётся задержка delays_ms[N - 1] (нумерация с нуля).
        v_delay_ms := coalesce((v_task.delays_ms ->> (v_failures - 1))::integer, 0);

        UPDATE workflow.workflow_job
        SET state = 'RETRY_WAIT',
            failure_count = v_failures,
            lease_owner = NULL,
            lease_until = NULL,
            next_attempt_at = clock_timestamp() + make_interval(secs => v_delay_ms / 1000.0),
            updated_at = now()
        WHERE job_id = v_job.job_id;

        RETURN workflow.ok(jsonb_build_object(
            'resource', 'job',
            'jobId', v_job.job_id,
            'state', 'RETRY_WAIT',
            'failureCount', v_failures,
            'nextAttemptInMs', v_delay_ms
        ));
    END IF;

    UPDATE workflow.workflow_job
    SET state = 'DEAD',
        dead_reason = 'failure_budget',
        failure_count = v_failures,
        lease_until = NULL,
        updated_at = now()
    WHERE job_id = v_job.job_id;

    -- Уже завершённые шаг и процесс не переписываются: в текущей модели у процесса не бывает
    -- второго задания, но опираться на свойство графа там, где хватает условия запроса, значит
    -- переносить хрупкость на будущие карты.
    UPDATE workflow.step_instance
    SET state = 'FAILED', completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id
      AND state NOT IN ('COMPLETED', 'FAILED');

    UPDATE workflow.process_instance
    SET state = 'FAILED', updated_at = now()
    WHERE process_id = v_job.process_id
      AND state NOT IN ('COMPLETED', 'FAILED');

    PERFORM workflow.record_event(
        v_job.process_id, v_job.step_instance_id, 'TaskFailed',
        jsonb_build_object('jobId', v_job.job_id, 'errorCode', v_code, 'stepKey', v_step_key)
    );
    PERFORM workflow.record_event(
        v_job.process_id, v_job.step_instance_id, 'ProcessFailed',
        jsonb_build_object('errorCode', v_code)
    );

    RETURN workflow.ok(jsonb_build_object(
        'resource', 'job',
        'jobId', v_job.job_id,
        'state', 'DEAD',
        'failureCount', v_failures
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Проба fencing из test profile.
--
-- Она вызывает ТУ ЖЕ production-границу завершения, что и исполнитель, поэтому проверяет
-- настоящий инвариант, а не отдельную тестовую ветку. Но доступность самой пробы решается
-- признаком, который знает БАЗА: ограничивать обход арендатора переменной окружения клиента
-- значило бы проводить границу доверия по тому, кто запустил процесс, а не по правам в базе.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.set_test_profile(p_enabled boolean)
RETURNS void
LANGUAGE sql
SET search_path = pg_catalog
AS $fn$
    UPDATE workflow.runtime_setting
    SET test_profile = coalesce(p_enabled, false), updated_at = now()
    WHERE singleton;
$fn$;

CREATE OR REPLACE FUNCTION workflow.test_finish_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_outcome text,
    p_result jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_enabled boolean;
BEGIN
    SELECT test_profile INTO v_enabled FROM workflow.runtime_setting WHERE singleton;

    IF NOT coalesce(v_enabled, false) THEN
        RETURN workflow.failure(
            'workflow.test_profile_required',
            'the fencing probe is available only under the test profile');
    END IF;

    -- Проба намеренно не передаёт технический след: она не является исполнением действия.
    RETURN workflow.finish_job(p_job_id, p_owner, p_lease_version, p_outcome, p_result, NULL);
END;
$fn$;

-------------------------------------------------------------------------------
-- Диагностика: компактная выгрузка процесса, шагов, заданий и попыток.
-- Используется и командой flow get, и зарегистрированным действием workflow.get.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.describe_process(p_process_id uuid)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'process', jsonb_build_object(
            'processId', p.process_id,
            'businessKey', p.business_key,
            'flowName', f.flow_name,
            'flowVersion', v.version,
            'state', p.state,
            'currentStepKey', p.current_step_key,
            'createdAt', p.created_at,
            'updatedAt', p.updated_at,
            -- Диагностика обязана оставаться читаемой и при разросшейся истории: ответ действия
            -- ограничен COURSE_MAX_RESULT_BYTES, и без потолка выборки workflow.get переставал бы
            -- отвечать ровно на тех процессах, ради которых его и открывают.
            'truncated', (
                SELECT count(*) > 200 FROM workflow.step_instance s WHERE s.process_id = p.process_id
            ) OR (
                SELECT count(*) > 200 FROM workflow.workflow_job j WHERE j.process_id = p.process_id
            ) OR (
                SELECT count(*) > 200
                FROM workflow.task_attempt a
                JOIN workflow.workflow_job j ON j.job_id = a.job_id
                WHERE j.process_id = p.process_id
            )
        ),
        'steps', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                'stepInstanceId', s.step_instance_id,
                'stepKey', s.step_key,
                'stepType', s.step_type,
                'state', s.state,
                'outcome', s.outcome,
                'enteredAt', s.entered_at,
                'completedAt', s.completed_at
            ) ORDER BY s.entered_at, s.step_instance_id)
            FROM (
                SELECT * FROM workflow.step_instance
                WHERE process_id = p.process_id
                ORDER BY entered_at, step_instance_id
                LIMIT 200
            ) AS s
        ), '[]'::jsonb),
        'jobs', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                'jobId', j.job_id,
                'stepInstanceId', j.step_instance_id,
                'executionId', j.execution_id,
                'state', j.state,
                'leaseOwner', j.lease_owner,
                'leaseVersion', j.lease_version,
                'leaseUntil', j.lease_until,
                'attemptCount', j.attempt_count,
                'nextAttemptAt', j.next_attempt_at
            ) ORDER BY j.created_at, j.job_id)
            FROM (
                SELECT * FROM workflow.workflow_job
                WHERE process_id = p.process_id
                ORDER BY created_at, job_id
                LIMIT 200
            ) AS j
        ), '[]'::jsonb),
        'attempts', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                'attemptId', a.attempt_id,
                'jobId', a.job_id,
                'executionId', a.execution_id,
                'leaseVersion', a.lease_version,
                'attemptNumber', a.attempt_number,
                'status', a.status,
                'outcome', a.outcome,
                'errorCode', a.error_code,
                'startedAt', a.started_at,
                'finishedAt', a.finished_at
            ) ORDER BY a.started_at, a.attempt_number)
            FROM (
                SELECT a.* FROM workflow.task_attempt a
                JOIN workflow.workflow_job j ON j.job_id = a.job_id
                WHERE j.process_id = p.process_id
                ORDER BY a.started_at, a.attempt_number
                LIMIT 200
            ) AS a
        ), '[]'::jsonb)
    )
    FROM workflow.process_instance p
    JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
    JOIN workflow.flow_version v ON v.flow_version_id = p.flow_version_id
    WHERE p.process_id = p_process_id;
$fn$;

-------------------------------------------------------------------------------
-- Зарегистрированное действие workflow.get: та же диагностика, но через общий маршрут
-- POST /api/workflow/get, с проверкой policy на обеих границах.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.get_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_process_id uuid;
    v_result     jsonb;
BEGIN
    BEGIN
        v_process_id := (p_payload ->> 'processId')::uuid;
    -- Ловится ровно нечитаемый идентификатор. WHEN OTHERS отнёс бы сюда и внутренний сбой, то есть
    -- сообщил бы вызывающему «твой ввод неверен» в случае, когда неверна была база.
    EXCEPTION WHEN invalid_text_representation THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'payload.invalid',
            'message', 'processId is not a valid identifier',
            'retryable', false,
            'details', '{}'::jsonb
        );
    END;

    v_result := workflow.describe_process(v_process_id);

    IF v_result IS NULL THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'workflow.process_not_found',
            'message', 'process is not found',
            'retryable', false,
            'details', '{}'::jsonb
        );
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', v_result,
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$fn$;
