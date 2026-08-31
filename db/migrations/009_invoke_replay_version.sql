-- 009_invoke_replay_version.sql
-- Повтор по ключу идемпотентности сообщает ту версию, которая его исполнила.
--
-- Проблема, которую чиним. Идентичность ключа идемпотентности намеренно НЕ включает версию
-- действия: смысл идемпотентности в том, что повтор не создаёт второго предметного эффекта, и
-- добавление версии в уникальный ключ разрешило бы по тому же ключу создать вторую операцию
-- после переключения default-версии. Но раньше ветка повтора возвращала сохранённый результат с
-- meta от ТЕКУЩЕЙ разрешённой версии. После activate это давало ответ, в котором результат
-- принадлежит одной версии, а объявленная версия — другой.
--
-- Решение: сохранённый результат сопровождается сохранённой же версией. Версия хранилась в
-- api.idempotency_record.action_version с самого начала — здесь она наконец попадает в ответ.
-- Дополняющая половина правки живёт в ActionRuntime: увидев признак replayed, C# не меряет чужой
-- результат сегодняшним манифестом.
--
-- Всё остальное тело функции идентично 004_invoke.sql.

CREATE OR REPLACE FUNCTION api.invoke(
    p_module text,
    p_action text,
    p_version integer,
    p_context jsonb,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_action      api.action_version%ROWTYPE;
    v_meta        jsonb;
    v_principal   text;
    v_consumer    text;
    v_scopes      text[];
    v_required    text[];
    v_request_id  text;
    v_scope_key   text;
    v_hash        text;
    v_existing    api.idempotency_record%ROWTYPE;
    v_reservation uuid;
    v_attempt     integer := 0;
    v_target      jsonb;
    v_outcome     text;
    v_result      jsonb;
    v_signature   boolean;
BEGIN
    IF jsonb_typeof(p_context) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('internal.error', 'context must be a JSON object', '{}'::jsonb, NULL);
    END IF;

    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload must be a JSON object', p_context, NULL);
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 1. Разрешаем целевое действие ТОЛЬКО из неизменяемого каталога.
    ---------------------------------------------------------------------------
    IF p_version IS NULL THEN
        SELECT * INTO v_action
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND enabled AND is_default;
    ELSE
        SELECT * INTO v_action
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND version = p_version AND enabled;
    END IF;

    IF NOT FOUND THEN
        RETURN api.error_envelope(
            'action.not_found',
            'action version is not published or is disabled',
            p_context,
            p_version
        );
    END IF;

    v_meta := jsonb_build_object(
        'correlationId', p_context ->> 'correlationId',
        'actionVersion', v_action.version
    );

    ---------------------------------------------------------------------------
    -- Шаг 2. Повторно проверяем доверенный контекст и права разрешённой версии.
    ---------------------------------------------------------------------------
    v_principal := p_context ->> 'principal';
    v_consumer := p_context ->> 'consumer';

    IF jsonb_typeof(p_context -> 'principal') IS DISTINCT FROM 'string'
       OR coalesce(v_principal, '') = ''
       OR jsonb_typeof(p_context -> 'consumer') IS DISTINCT FROM 'string'
       OR coalesce(v_consumer, '') = ''
    THEN
        RETURN api.error_envelope(
            'access.denied',
            'trusted context is incomplete',
            p_context,
            v_action.version
        );
    END IF;

    v_scopes := api.jsonb_text_array(p_context -> 'scopes');
    v_required := api.jsonb_text_array(v_action.required_policy);

    IF NOT (v_required <@ v_scopes) THEN
        RETURN api.error_envelope(
            'access.denied',
            'context does not satisfy the required policy of the action',
            p_context,
            v_action.version
        );
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 3. Проверяем точную сигнатуру зарегистрированной целевой функции.
    ---------------------------------------------------------------------------
    SELECT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = v_action.target_schema
          AND p.proname = v_action.target_function
          AND p.prokind = 'f'
          AND p.pronargs = 2
          AND p.proargtypes[0] = 'jsonb'::regtype
          AND p.proargtypes[1] = 'jsonb'::regtype
          AND p.prorettype = 'jsonb'::regtype
          AND NOT p.proretset
    ) INTO v_signature;

    IF NOT v_signature THEN
        RETURN api.error_envelope(
            'action.contract_violation',
            'registered target does not match the required signature',
            p_context,
            v_action.version
        );
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 4. Резервируем ключ идемпотентности силами PostgreSQL.
    ---------------------------------------------------------------------------
    v_request_id := nullif(p_context ->> 'requestId', '');
    v_hash := api.payload_hash(p_payload);

    IF v_action.idempotency_mode <> 'none' THEN
        IF v_request_id IS NULL THEN
            IF v_action.idempotency_mode = 'required' THEN
                RETURN api.error_envelope(
                    'idempotency.required',
                    'action requires an idempotency key',
                    p_context,
                    v_action.version
                );
            END IF;
        ELSE
            v_scope_key := CASE v_action.idempotency_scope
                WHEN 'principal_action' THEN v_principal
                WHEN 'consumer_action' THEN v_consumer
                ELSE ''
            END;

            LOOP
                v_attempt := v_attempt + 1;

                INSERT INTO api.idempotency_record (
                    scope, scope_key, module, action, request_id,
                    payload_hash, status, action_version
                )
                VALUES (
                    v_action.idempotency_scope, v_scope_key, p_module, p_action, v_request_id,
                    v_hash, 'IN_PROGRESS', v_action.version
                )
                ON CONFLICT ON CONSTRAINT idempotency_record_unique DO NOTHING
                RETURNING idempotency_id INTO v_reservation;

                EXIT WHEN v_reservation IS NOT NULL;

                SELECT * INTO v_existing
                FROM api.idempotency_record
                WHERE scope = v_action.idempotency_scope
                  AND scope_key = v_scope_key
                  AND module = p_module
                  AND action = p_action
                  AND request_id = v_request_id
                FOR UPDATE;

                IF FOUND THEN
                    IF v_existing.payload_hash <> v_hash THEN
                        RETURN api.error_envelope(
                            'idempotency.conflict',
                            'idempotency key was already used with a different payload',
                            p_context,
                            v_action.version
                        );
                    END IF;

                    IF v_existing.status = 'OK' THEN
                        -- Повтор зафиксированного эффекта. Ключевое отличие от 004_invoke.sql:
                        -- в meta уходит ВЕРСИЯ, КОТОРАЯ ЭТОТ РЕЗУЛЬТАТ ИСПОЛНИЛА, а не та, что
                        -- сейчас разрешилась как default. Иначе результат и объявленная версия
                        -- разъезжались после каждого activate.
                        RETURN jsonb_build_object(
                            'status', 'ok',
                            'outcome', v_existing.response -> 'outcome',
                            'result', v_existing.response -> 'result',
                            'meta', jsonb_build_object(
                                'correlationId', p_context ->> 'correlationId',
                                'actionVersion', v_existing.action_version
                            ),
                            'replayed', true
                        );
                    END IF;

                    RETURN api.error_envelope(
                        'idempotency.conflict',
                        'idempotency key is currently in progress',
                        p_context,
                        v_action.version,
                        '{}'::jsonb,
                        true
                    );
                END IF;

                IF v_attempt >= 3 THEN
                    RETURN api.error_envelope(
                        'internal.error',
                        'idempotency reservation could not be established',
                        p_context,
                        v_action.version,
                        '{}'::jsonb,
                        true
                    );
                END IF;
            END LOOP;
        END IF;
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 5. Вызываем только зарегистрированную функцию с доверенным контекстом.
    ---------------------------------------------------------------------------
    BEGIN
        EXECUTE pg_catalog.format(
            'SELECT %I.%I($1, $2)',
            v_action.target_schema,
            v_action.target_function
        )
        INTO v_target
        USING p_context, p_payload;
    EXCEPTION
        WHEN query_canceled THEN
            RAISE;
        WHEN OTHERS THEN
            RETURN api.error_envelope(
                'internal.error',
                'registered function failed',
                p_context,
                v_action.version,
                jsonb_build_object('sqlstate', SQLSTATE),
                false
            );
    END;

    ---------------------------------------------------------------------------
    -- Шаг 6. Проверяем конверт зарегистрированной функции до сохранения чего-либо.
    ---------------------------------------------------------------------------
    IF jsonb_typeof(v_target) IS DISTINCT FROM 'object'
       OR (v_target ->> 'status') IS NULL
       OR (v_target ->> 'status') NOT IN ('ok', 'error')
    THEN
        RETURN api.error_envelope(
            'action.contract_violation',
            'registered function returned a non contractual envelope',
            p_context,
            v_action.version
        );
    END IF;

    IF (v_target ->> 'status') = 'error' THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', coalesce(v_target ->> 'code', 'internal.error'),
            'message', coalesce(v_target ->> 'message', 'action returned a domain error'),
            'retryable', coalesce((v_target ->> 'retryable')::boolean, false),
            'details', coalesce(v_target -> 'details', '{}'::jsonb),
            'meta', v_meta
        );
    END IF;

    v_outcome := v_target ->> 'outcome';
    v_result := v_target -> 'result';

    IF v_outcome IS NULL OR NOT (v_action.outcomes ? v_outcome) THEN
        RETURN api.error_envelope(
            'action.contract_violation',
            'registered function returned an undeclared outcome',
            p_context,
            v_action.version
        );
    END IF;

    IF jsonb_typeof(v_result) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope(
            'action.contract_violation',
            'registered function returned a non object result',
            p_context,
            v_action.version
        );
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 7. Сохраняем успешный результат под резервацией, в той же транзакции.
    ---------------------------------------------------------------------------
    IF v_reservation IS NOT NULL THEN
        UPDATE api.idempotency_record
        SET status = 'OK',
            response = jsonb_build_object('outcome', v_outcome, 'result', v_result),
            completed_at = now()
        WHERE idempotency_id = v_reservation;
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_outcome,
        'result', v_result,
        'meta', v_meta
    );
END;
$$;
