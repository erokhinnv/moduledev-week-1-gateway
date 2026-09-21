-- 020_invoke_body_identity.sql
-- Идентичность тела идемпотентной HTTP-команды — это точные байты запроса; транзиентный
-- конфликт блокировок — повторяемый отказ.
--
-- Проблема, которую чиним. Контракт недели 3 определяет повтор receipt.accept через «тот же
-- Idempotency-Key и тот же body», а Inbox хранит SHA-256 точных байт. api.invoke же сравнивал
-- повтор по отпечатку jsonb, который байты не сохраняет: пробелы и порядок ключей исчезают при
-- разборе. Два байтово разных, но корректно подписанных тела с одним ключом давали 200 replay,
-- хотя по тому же messageId с другим ключом граница отвечала 409. Одна команда получала две
-- разные трактовки «того же тела» в зависимости от ключа.
--
-- Решение: если доверенный контекст несёт payloadHash (его ставит только C#-граница HTTP),
-- резервация идемпотентности сравнивает именно его. Путь worker'а контекста HTTP не имеет и не
-- меняется.
--
-- Вторая правка той же границы: deadlock и serialization failure внутри зарегистрированной
-- функции возвращаются повторяемым dependency.unavailable (HTTP 503), а не неповторяемым
-- internal.error. Всё остальное тело функции идентично 009_invoke_replay_version.sql.
--
-- Совместимость: запись идемпотентности, сделанная до этой миграции, хранит jsonb-отпечаток,
-- поэтому её повтор по HTTP после миграции даст конфликт. Разделение ролей недели 3 и так
-- требует чистого тома, поэтому переходного периода со старыми записями у стенда нет.

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
    -- Идентичность тела команды. HTTP-граница передаёт отпечаток ТОЧНЫХ байт запроса: именно
    -- его видит клиент в autocheck.action_dispatches, именно над ними проверена подпись, и
    -- именно его сохраняет Inbox. Раньше здесь хешировалось jsonb-представление, и тот же ключ
    -- с байтово другим телом (например, повторно сериализованная подписанная квитанция) давал
    -- повтор вместо конфликта. Вызов в обход HTTP (worker) отпечатка не несёт и сохраняет
    -- прежнюю семантику.
    v_hash := CASE
        WHEN (p_context ->> 'payloadHash') ~ '^[0-9a-f]{64}$' THEN p_context ->> 'payloadHash'
        ELSE api.payload_hash(p_payload)
    END;

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
        -- Конфликт блокировок или сериализации — транзиентный отказ, а не дефект функции: вся
        -- транзакция откатывается, и та же команда с тем же ключом безопасно повторяема. Раньше он
        -- попадал в WHEN OTHERS и превращался в неповторяемый internal.error, из-за чего worker
        -- переводил процесс в FAILED вместо повтора.
        WHEN deadlock_detected OR serialization_failure THEN
            RETURN api.error_envelope(
                'dependency.unavailable',
                'transient database conflict, the command can be retried',
                p_context,
                v_action.version,
                jsonb_build_object('sqlstate', SQLSTATE),
                true
            );
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
