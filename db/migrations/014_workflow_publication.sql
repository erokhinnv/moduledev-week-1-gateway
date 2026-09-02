-- 014_workflow_publication.sql
-- Публикация и активация версий карты процесса.
--
-- Публикация создаёт НЕИЗМЕНЯЕМУЮ версию, но не активирует её. Активация атомарно выбирает ровно
-- одну версию для НОВЫХ экземпляров: уже запущенный процесс навсегда остаётся на своей.

-------------------------------------------------------------------------------
-- Факты каталога, нужные валидатору карты.
--
-- Валидатор живёт в CLI, но проверять существование действия, его включённость, объявленные
-- outcomes и policy он обязан по авторитетному состоянию, а не по своим догадкам. Одна функция
-- отдаёт всё это одним запросом — вместе с server-side scopes доверенного principal.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.publication_facts(p_map jsonb)
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'workerPrincipal', (
                SELECT jsonb_build_object('principal', w.principal, 'scopes', w.scopes)
                FROM workflow.worker_principal w
                WHERE w.principal = 'workflow-worker'
            ),
            'actions', coalesce((
                SELECT jsonb_agg(jsonb_build_object(
                    'module', keys.module,
                    'action', keys.action,
                    'version', keys.version,
                    'found', av.action_version_id IS NOT NULL,
                    'enabled', coalesce(av.enabled, false),
                    'requiredPolicy', coalesce(av.required_policy, '[]'::jsonb),
                    'outcomes', coalesce(av.outcomes, '[]'::jsonb)
                ))
                FROM (
                    SELECT DISTINCT
                        step -> 'task' ->> 'module' AS module,
                        step -> 'task' ->> 'action' AS action,
                        step -> 'task' ->> 'action_version' AS version
                    FROM jsonb_array_elements(coalesce(p_map -> 'steps', '[]'::jsonb)) AS step
                    WHERE step ->> 'type' = 'automatic'
                      AND jsonb_typeof(step -> 'task') = 'object'
                ) AS keys
                LEFT JOIN api.action_version av
                       ON av.module = keys.module
                      AND av.action = keys.action
                      AND av.version::text = keys.version
            ), '[]'::jsonb)
        )
    );
$fn$;

-------------------------------------------------------------------------------
-- Публикация карты. Повтор идентичного контракта возвращает исходный результат,
-- другой контракт под тем же flow_name/version — конфликт.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.publish_flow(p_map jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_flow_name  text := p_map ->> 'flow_name';
    v_version    integer;
    v_start      text := p_map ->> 'start_step';
    v_hash       text := api.payload_hash(p_map);
    v_flow_id    uuid;
    v_existing   workflow.flow_version%ROWTYPE;
    v_version_id uuid;
    v_step       jsonb;
    v_step_id    uuid;
    v_task       jsonb;
    v_transition jsonb;
BEGIN
    IF jsonb_typeof(p_map) IS DISTINCT FROM 'object'
       OR (p_map ->> 'contract_version') IS DISTINCT FROM 'course-1'
    THEN
        RETURN workflow.failure('workflow.map_invalid', 'workflow map must declare contract_version course-1');
    END IF;

    BEGIN
        v_version := (p_map ->> 'version')::integer;
    EXCEPTION WHEN OTHERS THEN
        v_version := NULL;
    END;

    IF v_flow_name IS NULL OR v_version IS NULL OR v_start IS NULL THEN
        RETURN workflow.failure('workflow.map_invalid', 'flow_name, version and start_step are required');
    END IF;

    -- Публикация одной карты сериализуется на время транзакции: параллельные publish одного
    -- flow_name не могут «разъехаться» между проверкой и вставкой.
    PERFORM pg_advisory_xact_lock(hashtext('workflow.flow'), hashtext(v_flow_name));

    INSERT INTO workflow.flow_definition (flow_name)
    VALUES (v_flow_name)
    ON CONFLICT (flow_name) DO NOTHING;

    SELECT flow_id INTO v_flow_id
    FROM workflow.flow_definition
    WHERE flow_name = v_flow_name;

    SELECT * INTO v_existing
    FROM workflow.flow_version
    WHERE flow_id = v_flow_id AND version = v_version;

    IF FOUND THEN
        IF v_existing.map_hash = v_hash THEN
            RETURN workflow.ok(jsonb_build_object(
                'resource', 'flow',
                'operation', 'published',
                'flowName', v_flow_name,
                'flowVersion', v_version,
                'status', v_existing.status,
                'republished', true
            ));
        END IF;

        RETURN workflow.failure(
            'workflow.flow_conflict',
            'flow version was already published with a different contract');
    END IF;

    BEGIN
        INSERT INTO workflow.flow_version (flow_id, version, start_step_key, map, map_hash)
        VALUES (v_flow_id, v_version, v_start, p_map, v_hash)
        RETURNING flow_version_id INTO v_version_id;

        FOR v_step IN SELECT jsonb_array_elements(coalesce(p_map -> 'steps', '[]'::jsonb))
        LOOP
            INSERT INTO workflow.step_definition (
                flow_version_id, step_key, step_type, signal_type, outcome, allowed_outcomes
            )
            VALUES (
                v_version_id,
                v_step ->> 'key',
                upper(v_step ->> 'type'),
                v_step ->> 'signal_type',
                v_step ->> 'outcome',
                coalesce(v_step -> 'allowed_outcomes', '[]'::jsonb)
            )
            RETURNING step_definition_id INTO v_step_id;

            v_task := v_step -> 'task';
            IF jsonb_typeof(v_task) = 'object' THEN
                INSERT INTO workflow.task_definition (
                    step_definition_id, service, module, action, action_version,
                    required_policy, timeout_ms, max_attempts, delays_ms,
                    input_mapping, input_constants
                )
                VALUES (
                    v_step_id,
                    v_task ->> 'service',
                    v_task ->> 'module',
                    v_task ->> 'action',
                    (v_task ->> 'action_version')::integer,
                    coalesce(v_task -> 'required_policy', '[]'::jsonb),
                    (v_task ->> 'timeout_ms')::integer,
                    (v_task -> 'retry' ->> 'max_attempts')::integer,
                    coalesce(v_task -> 'retry' -> 'delays_ms', '[]'::jsonb),
                    coalesce(v_task -> 'input_mapping', '{}'::jsonb),
                    coalesce(v_task -> 'input_constants', '{}'::jsonb)
                );
            END IF;
        END LOOP;

        FOR v_transition IN SELECT jsonb_array_elements(coalesce(p_map -> 'transitions', '[]'::jsonb))
        LOOP
            INSERT INTO workflow.transition_definition (
                flow_version_id, from_step_key, outcome, to_step_key
            )
            VALUES (
                v_version_id,
                v_transition ->> 'from',
                v_transition ->> 'outcome',
                v_transition ->> 'to'
            );
        END LOOP;
    EXCEPTION
        WHEN unique_violation OR check_violation OR not_null_violation OR invalid_text_representation THEN
            -- Карта не прошла контрактные ограничения хранилища. Подblock откатывается целиком,
            -- поэтому отклонённая публикация не оставляет ни одной строки.
            RETURN workflow.failure(
                'workflow.map_invalid',
                'workflow map violates the course-1 storage contract');
    END;

    RETURN workflow.ok(jsonb_build_object(
        'resource', 'flow',
        'operation', 'published',
        'flowName', v_flow_name,
        'flowVersion', v_version,
        'status', 'PUBLISHED',
        'republished', false
    ));
END;
$fn$;

-------------------------------------------------------------------------------
-- Активация версии: ровно одна активная версия маршрута для новых экземпляров.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.activate_flow(p_flow_name text, p_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_flow_id    uuid;
    v_version_id uuid;
BEGIN
    SELECT flow_id INTO v_flow_id
    FROM workflow.flow_definition
    WHERE flow_name = p_flow_name;

    IF NOT FOUND THEN
        RETURN workflow.failure('workflow.flow_not_found', 'flow is not published');
    END IF;

    PERFORM pg_advisory_xact_lock(hashtext('workflow.flow'), hashtext(p_flow_name));

    SELECT flow_version_id INTO v_version_id
    FROM workflow.flow_version
    WHERE flow_id = v_flow_id AND version = p_version;

    IF NOT FOUND THEN
        RETURN workflow.failure('workflow.flow_version_not_found', 'flow version is not published');
    END IF;

    -- Снятие прежней активной версии и установка новой идут одной транзакцией: частичного
    -- состояния «активных нет» или «активных две» наблюдать нельзя.
    UPDATE workflow.flow_version
    SET is_active = false
    WHERE flow_id = v_flow_id AND is_active AND flow_version_id <> v_version_id;

    UPDATE workflow.flow_version
    SET is_active = true
    WHERE flow_version_id = v_version_id AND NOT is_active;

    RETURN workflow.ok(jsonb_build_object(
        'resource', 'flow',
        'operation', 'activated',
        'flowName', p_flow_name,
        'flowVersion', p_version
    ));
END;
$fn$;

CREATE OR REPLACE FUNCTION workflow.list_flows()
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
    SELECT coalesce(jsonb_agg(jsonb_build_object(
        'flowName', f.flow_name,
        'flowVersion', v.version,
        'status', v.status,
        'isActive', v.is_active,
        'startStep', v.start_step_key,
        'publishedAt', v.published_at
    ) ORDER BY f.flow_name, v.version), '[]'::jsonb)
    FROM workflow.flow_version v
    JOIN workflow.flow_definition f ON f.flow_id = v.flow_id;
$fn$;

-- Обёртка для команды flow get: тот же снимок, что отдаёт действие workflow.get.
CREATE OR REPLACE FUNCTION workflow.describe_process_envelope(p_process_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_snapshot jsonb := workflow.describe_process(p_process_id);
BEGIN
    IF v_snapshot IS NULL THEN
        RETURN workflow.failure('workflow.process_not_found', 'process is not found');
    END IF;

    RETURN workflow.ok(
        (v_snapshot -> 'process')
        || jsonb_build_object(
            'resource', 'process',
            'steps', v_snapshot -> 'steps',
            'jobs', v_snapshot -> 'jobs',
            'attempts', v_snapshot -> 'attempts'
        )
    );
END;
$fn$;
