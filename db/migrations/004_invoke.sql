-- 004_invoke.sql
-- Единственная точка предметного (доменного) исполнения во всей системе.
--
-- api.invoke разрешает целевую функцию только из неизменяемого каталога, повторно проверяет
-- права (policy) по доверенному контексту, резервирует ключ идемпотентности через уникальное
-- ограничение PostgreSQL и вызывает ТОЛЬКО зарегистрированную функцию. Она никогда не принимает
-- ни целевую функцию/схему, ни SQL-текст, ни права доступа из payload запроса — все эти данные
-- приходят только из каталога (v_action) и из доверенного контекста (p_context), которые
-- полностью контролируются сервером, а не клиентом.

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
    -- p_version = NULL означает "версия по умолчанию для этого маршрута".
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
    -- Шаг 2. Повторно проверяем доверенный контекст и права (policy) уже РАЗРЕШЁННОЙ версии.
    -- Это ВТОРАЯ, полностью независимая проверка прав (первая была в C# на HTTP-границе,
    -- см. ActionRuntime.RunAsync шаг 6): даже если кто-то вызовет api.invoke напрямую из psql,
    -- в обход HTTP-слоя, права всё равно будут проверены здесь.
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

    -- Оператор "<@" проверяет, что МАССИВ v_required целиком содержится в МАССИВЕ v_scopes —
    -- то есть у вызывающего есть ВСЕ требуемые права, а не хотя бы одно из них.
    IF NOT (v_required <@ v_scopes) THEN
        RETURN api.error_envelope(
            'access.denied',
            'context does not satisfy the required policy of the action',
            p_context,
            v_action.version
        );
    END IF;

    ---------------------------------------------------------------------------
    -- Шаг 3. Проверяем, что зарегистрированная целевая функция реально существует и имеет ТОЧНО
    -- ту сигнатуру, которую требует контракт: два параметра типа jsonb на входе, jsonb на выходе,
    -- обычная (не табличная/множественная) функция. Это защита от рассинхронизации каталога и
    -- реального состояния базы данных — например, если функцию случайно удалили или изменили её
    -- сигнатуру в обход публикации через CLI.
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
    -- Шаг 4. Резервируем ключ идемпотентности. Уникальность гарантируется САМОЙ PostgreSQL:
    -- конкурирующие транзакции блокируются друг на друге на уровне строки (через
    -- ON CONFLICT / FOR UPDATE), а не на process-local блокировке C#-кода — благодаря этому
    -- гарантия работает одинаково независимо от того, сколько реплик сервиса api запущено.
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
            -- Область действия ключа (scope) определяет, среди каких запросов ключ должен быть
            -- уникален: только у одного и того же principal, только у одного и того же consumer,
            -- либо глобально среди всех.
            v_scope_key := CASE v_action.idempotency_scope
                WHEN 'principal_action' THEN v_principal
                WHEN 'consumer_action' THEN v_consumer
                ELSE ''
            END;

            LOOP
                v_attempt := v_attempt + 1;

                -- Пытаемся "застолбить" ключ идемпотентности вставкой строки со статусом
                -- IN_PROGRESS. Если конфликт (такой ключ уже кем-то зарезервирован) —
                -- ON CONFLICT DO NOTHING просто ничего не вставляет, и v_reservation остаётся NULL.
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

                -- Резервирование не удалось — значит, такая запись уже существует. Читаем её
                -- с FOR UPDATE: если конкурирующая транзакция ещё не завершилась, этот SELECT
                -- ЗАБЛОКИРУЕТСЯ и будет ждать, пока та транзакция не сделает commit или rollback.
                -- Именно это ожидание и есть механизм конкурентной идемпотентности — вторая
                -- параллельная попытка не проскочит вперёд первой.
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
                        -- Тот же ключ идемпотентности, но другое содержимое запроса — это
                        -- конфликт, а не "тот же самый повтор".
                        RETURN api.error_envelope(
                            'idempotency.conflict',
                            'idempotency key was already used with a different payload',
                            p_context,
                            v_action.version
                        );
                    END IF;

                    IF v_existing.status = 'OK' THEN
                        -- Повтор уже зафиксированного (committed) эффекта: возвращаем исходный
                        -- результат, не выполняя предметную функцию заново — второго эффекта нет.
                        RETURN jsonb_build_object(
                            'status', 'ok',
                            'outcome', v_existing.response -> 'outcome',
                            'result', v_existing.response -> 'result',
                            'meta', v_meta,
                            'replayed', true
                        );
                    END IF;

                    -- Найденная запись всё ещё IN_PROGRESS — значит, мы дождались завершения
                    -- конкурирующей транзакции, но её FOR UPDATE выше уже само по себе означает,
                    -- что мы прочитали актуальное состояние ПОСЛЕ её commit/rollback. Если статус
                    -- всё равно IN_PROGRESS, это редкий пограничный случай (например rollback
                    -- конкурирующей транзакции без обновления статуса) — сообщаем клиенту, что
                    -- операция в процессе, и что запрос можно повторить.
                    RETURN api.error_envelope(
                        'idempotency.conflict',
                        'idempotency key is currently in progress',
                        p_context,
                        v_action.version,
                        '{}'::jsonb,
                        true
                    );
                END IF;

                -- Строка не найдена — значит, конкурирующая транзакция откатилась (rollback) и
                -- освободила ключ ещё до того, как мы успели её прочитать. Пробуем застолбить
                -- ключ ещё раз (не более трёх попыток, чтобы не зациклиться навечно).
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
    -- Шаг 5. Вызываем ТОЛЬКО зарегистрированную функцию, передавая ей доверенный контекст.
    -- Имя схемы и функции берутся исключительно из v_action (то есть из каталога) — они
    -- НИКОГДА не собираются из данных, присланных клиентом в payload.
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
            -- Достигнут дедлайн (statement_timeout, выставленный в C# из timeout_ms манифеста) —
            -- это технический таймаут ВСЕГО запроса, а не предметная (доменная) ошибка, поэтому
            -- исключение пробрасывается дальше наверх, а не превращается в обычный конверт ошибки.
            RAISE;
        WHEN OTHERS THEN
            -- Любая другая непредвиденная ошибка внутри целевой функции. Под-транзакция
            -- автоматически откатывается самим PostgreSQL при выходе из BEGIN/EXCEPTION блока —
            -- никакой текст SQL-ошибки наружу не передаётся, только код SQLSTATE в деталях,
            -- который сам по себе не раскрывает ни имён объектов, ни текста запроса.
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
    -- Шаг 6. Проверяем конверт, вернувшийся от зарегистрированной функции, ДО того как что-либо
    -- из него будет сохранено или отправлено клиенту.
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
        -- Функция сама объявила предметную (доменную) ошибку — например
        -- "operation.not_found" из payment.operation_get_v1. Такая ошибка передаётся клиенту
        -- как есть (со своим кодом и сообщением), но выполнение api.invoke на этом завершается,
        -- и caller (C#-код) откатит всю транзакцию целиком.
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

    -- Исход (outcome) обязан входить в список, объявленный самим манифестом действия
    -- (v_action.outcomes) — оператор "?" проверяет наличие ключа/значения в jsonb-массиве.
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
    -- Шаг 7. Сохраняем успешный результат под резервацией идемпотентности — В ТОЙ ЖЕ САМОЙ
    -- транзакции, что и сам предметный эффект. Благодаря этому результат идемпотентности и
    -- реальный предметный эффект коммитятся или откатываются строго вместе, атомарно.
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

COMMENT ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) IS
    'Single domain execution boundary: catalog resolution, policy re-verification, idempotency and registered target call';
