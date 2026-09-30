-- 023_lease_covers_the_action_budget.sql
-- Срок аренды покрывает объявленный бюджет действия.
--
-- Проблема, которую чиним. claim_jobs ограничивал производный срок аренды кратностью к
-- настроенному профилем — не больше тройного. В test profile это 6 секунд при разрешённом
-- контрактом бюджете шага до 30. Шаг длиннее 5,5 секунд под двумя исполнителями не завершался
-- никогда: аренда истекала на середине действия, второй исполнитель перехватывал задание (попытка
-- первого становилась STALE), первый получал workflow.lease_stale на границе завершения и откатывал
-- уже выполненное действие, затем то же повторялось в обратную сторону — до абсолютного потолка
-- попыток. Второго предметного эффекта не возникало: эффект откатывается вместе с транзакцией, а
-- резервация идемпотентности по executionId блокирует соперника. Но исправное действие
-- выбрасывалось раз за разом, а процесс умирал с причиной attempt_ceiling, указывающей на сбой,
-- которого не было.
--
-- Решение: аренда не короче объявленного бюджета шага плюс запас на границу завершения и не
-- короче настроенного профилем срока. Потолок остаётся, но другой — контрактный максимум бюджета
-- шага (30 секунд и по схеме карты, и по манифесту действия) плюс тот же запас: карта не может
-- отодвинуть перехват дальше него, чего и добивался прежний потолок. Восстановление после гибели
-- исполнителя стоит одного срока аренды — то есть не больше бюджета того шага, который он исполнял.
--
-- Продление аренды живым исполнителем (heartbeat) рассматривалось и отклонено: контракт недели 2
-- закрепляет за ролью workflow_worker ровно четыре функции-границы, и проверка сверяет это
-- множество точно, а не как подмножество. Обоснование — в ADR 0004.
--
-- Всё остальное тело функции идентично 013_workflow_lease.sql.

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

    -- Контрактный максимум бюджета шага: столько разрешают и схема карты, и манифест действия.
    -- Это потолок срока аренды вместо прежней кратности к профилю: карта не может отодвинуть
    -- наблюдаемое время восстановления дальше собственного бюджета, а бюджет ограничен контрактом.
    c_task_timeout_max_ms constant integer := 30000;

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
        -- времени, а значит и срок аренды.
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

        -- Аренда покрывает объявленный бюджет действия целиком: пока живой исполнитель работает в
        -- пределах timeout_ms, перехватить задание нельзя. Потолок — контрактный максимум бюджета
        -- шага плюс запас: карта не может отодвинуть перехват дальше него, а прежняя кратность к
        -- профилю (× 3) делала любой шаг длиннее 5,5 секунд невыполнимым в test profile. За
        -- потолком повторное исполнение по-прежнему безопасно по другой причине — executionId
        -- остаётся ключом идемпотентности предметного эффекта.
        v_lease_ms := greatest(
            v_floor_ms,
            least(coalesce(v_task.timeout_ms, 0), c_task_timeout_max_ms) + c_lease_margin_ms);
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
