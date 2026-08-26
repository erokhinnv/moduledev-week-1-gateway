-- 003_catalog_api.sql
-- API публикации и разрешения (resolution) каталога действий.
-- Каждая функция возвращает единый конверт (envelope) вида {status, result} или
-- {status, code, message} — поэтому нарушение контракта никогда не прерывает транзакцию
-- вызывающей стороны аварийно и никогда не "утекает" в клиента текстом SQL-ошибки.

-- Хеш манифеста, используемый для проверки его неизменности при повторной публикации.
CREATE OR REPLACE FUNCTION api.manifest_hash(p_manifest jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    -- enabled и is_default — операционное состояние, а не часть контракта, поэтому они
    -- намеренно исключены из хеша неизменности: изменение этих двух полей (через activate или
    -- disable) не считается "изменением опубликованной версии".
    SELECT api.payload_hash((p_manifest - 'enabled') - 'is_default');
$$;

-- Превращает одну строку таблицы api.action_version в JSON-представление манифеста в том виде,
-- в котором его ожидает C#-код (ActionManifest.FromCatalog) — с именами полей в camelCase.
CREATE OR REPLACE FUNCTION api.action_row(p_row api.action_version)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'module', p_row.module,
        'action', p_row.action,
        'version', p_row.version,
        'httpMethod', p_row.http_method,
        'targetSchema', p_row.target_schema,
        'targetFunction', p_row.target_function,
        'requestSchema', p_row.request_schema,
        'responseSchema', p_row.response_schema,
        'outcomes', p_row.outcomes,
        'requiredPolicy', p_row.required_policy,
        'idempotencyMode', p_row.idempotency_mode,
        'idempotencyScope', p_row.idempotency_scope,
        'timeoutMs', p_row.timeout_ms,
        'enabled', p_row.enabled,
        'isDefault', p_row.is_default,
        'publishedAt', p_row.published_at
    );
$$;

-- Разрешение (resolution) действия — используется и HTTP-исполнителем (перед реальным вызовом),
-- и построителем документов OpenAPI.
-- p_version = NULL означает "взять включённую default-версию маршрута".
-- Если p_version задан явно — ищется именно эта версия, но она обязана быть включена (enabled).
CREATE OR REPLACE FUNCTION api.resolve_action(
    p_module text,
    p_action text,
    p_version integer
)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_row api.action_version%ROWTYPE;
BEGIN
    IF p_version IS NULL THEN
        SELECT * INTO v_row
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND enabled AND is_default;
    ELSE
        SELECT * INTO v_row
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND version = p_version AND enabled;
    END IF;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'action.not_found');
    END IF;

    RETURN jsonb_build_object('status', 'ok', 'result', api.action_row(v_row));
END;
$$;

-- Возвращает КАЖДЫЙ включённый маршрут в его версии по умолчанию — используется при построении
-- сводного документа /openapi/default.json.
CREATE OR REPLACE FUNCTION api.default_actions()
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
    SELECT coalesce(jsonb_agg(api.action_row(t) ORDER BY t.module, t.action), '[]'::jsonb)
    FROM api.action_version t
    WHERE t.enabled AND t.is_default;
$$;

-- Возвращает ВСЕ когда-либо опубликованные версии всех действий (включая выключенные) —
-- используется командой CLI "action list".
CREATE OR REPLACE FUNCTION api.list_actions()
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
    SELECT coalesce(jsonb_agg(api.action_row(t) ORDER BY t.module, t.action, t.version), '[]'::jsonb)
    FROM api.action_version t;
$$;

-- Публикация манифеста действия.
-- Идентичный повторный вызов с ТЕМ ЖЕ содержимым манифеста безопасен (возвращает "unchanged").
-- Любое изменение контрактной части уже опубликованной версии — конфликт (manifest.conflict).
CREATE OR REPLACE FUNCTION api.publish_action(p_manifest jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_module    text := p_manifest ->> 'module';
    v_action    text := p_manifest ->> 'action';
    v_version   integer := (p_manifest ->> 'version')::integer;
    v_hash      text := api.manifest_hash(p_manifest);
    v_enabled   boolean := coalesce((p_manifest ->> 'enabled')::boolean, false);
    v_default   boolean := coalesce((p_manifest ->> 'is_default')::boolean, false);
    v_existing  api.action_version%ROWTYPE;
    v_has_default boolean;
BEGIN
    -- FOR UPDATE здесь и ниже — сознательная блокировка строки на время транзакции публикации,
    -- чтобы два параллельных вызова publish/activate/disable для одного и того же маршрута не
    -- смогли одновременно нарушить инвариант "ровно одна default-версия".
    SELECT * INTO v_existing
    FROM api.action_version
    WHERE module = v_module AND action = v_action AND version = v_version
    FOR UPDATE;

    IF FOUND THEN
        IF v_existing.manifest_hash <> v_hash THEN
            RETURN jsonb_build_object(
                'status', 'error',
                'code', 'manifest.conflict',
                'message', 'published action version is immutable'
            );
        END IF;
        -- Манифест совпадает byte-в-byte с уже опубликованным — публикация идемпотентна,
        -- второго эффекта нет.
        RETURN jsonb_build_object(
            'status', 'ok',
            'result', jsonb_build_object(
                'resource', 'action',
                'operation', 'unchanged',
                'key', v_module || '.' || v_action,
                'version', v_version
            )
        );
    END IF;

    -- Маршрут всегда сохраняет ровно одну default-версию: явный default в манифесте снимает
    -- default с прежней версии, а самая первая включённая версия маршрута автоматически
    -- становится default сама по себе (иначе включённый маршрут остался бы вообще без default).
    SELECT EXISTS (
        SELECT 1 FROM api.action_version
        WHERE module = v_module AND action = v_action AND is_default
    ) INTO v_has_default;

    IF v_enabled AND NOT v_has_default THEN
        v_default := true;
    END IF;

    IF v_default THEN
        v_enabled := true;
        UPDATE api.action_version
        SET is_default = false
        WHERE module = v_module AND action = v_action AND is_default;
    END IF;

    INSERT INTO api.action_version (
        module, action, version, http_method, target_schema, target_function,
        request_schema, response_schema, outcomes, required_policy,
        idempotency_mode, idempotency_scope, timeout_ms,
        manifest, manifest_hash, enabled, is_default
    )
    VALUES (
        v_module,
        v_action,
        v_version,
        p_manifest ->> 'http_method',
        p_manifest ->> 'target_schema',
        p_manifest ->> 'target_function',
        p_manifest -> 'request_schema',
        p_manifest -> 'response_schema',
        p_manifest -> 'outcomes',
        p_manifest -> 'required_policy',
        p_manifest ->> 'idempotency_mode',
        p_manifest ->> 'idempotency_scope',
        (p_manifest ->> 'timeout_ms')::integer,
        p_manifest,
        v_hash,
        v_enabled,
        v_default
    );

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'action',
            'operation', 'published',
            'key', v_module || '.' || v_action,
            'version', v_version
        )
    );
END;
$$;

-- Атомарная активация: выбранная версия становится ЕДИНСТВЕННОЙ default-версией своего
-- маршрута. Всё это происходит за одну транзакцию, поэтому снаружи невозможно увидеть
-- промежуточное состояние "две default-версии" или "ни одной".
CREATE OR REPLACE FUNCTION api.activate_action(
    p_module text,
    p_action text,
    p_version integer
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_row api.action_version%ROWTYPE;
BEGIN
    SELECT * INTO v_row
    FROM api.action_version
    WHERE module = p_module AND action = p_action AND version = p_version
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'action.not_found',
            'message', 'action version is not published'
        );
    END IF;

    -- Блокируем все версии этого маршрута сразу, чтобы исключить гонку с параллельным
    -- activate/disable другой версии того же маршрута.
    PERFORM 1 FROM api.action_version
    WHERE module = p_module AND action = p_action
    FOR UPDATE;

    UPDATE api.action_version
    SET is_default = false
    WHERE module = p_module AND action = p_action AND is_default AND version <> p_version;

    UPDATE api.action_version
    SET enabled = true, is_default = true
    WHERE module = p_module AND action = p_action AND version = p_version;

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'action',
            'operation', 'activated',
            'key', p_module || '.' || p_action,
            'version', p_version
        )
    );
END;
$$;

-- Выключение (disable) версии действия. Если выключается ИМЕННО default-версия, требуется
-- явно указать версию-замену (replacement) — иначе включённый маршрут мог бы остаться вообще
-- без default-версии, что запрещено контрактом.
CREATE OR REPLACE FUNCTION api.disable_action(
    p_module text,
    p_action text,
    p_version integer,
    p_replacement integer
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_row         api.action_version%ROWTYPE;
    v_replacement api.action_version%ROWTYPE;
    v_remaining   integer;
BEGIN
    SELECT * INTO v_row
    FROM api.action_version
    WHERE module = p_module AND action = p_action AND version = p_version
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'action.not_found',
            'message', 'action version is not published'
        );
    END IF;

    IF p_replacement IS NOT NULL THEN
        IF p_replacement = p_version THEN
            RETURN jsonb_build_object(
                'status', 'error',
                'code', 'request.invalid',
                'message', 'replacement version must differ from the disabled version'
            );
        END IF;

        SELECT * INTO v_replacement
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND version = p_replacement
        FOR UPDATE;

        IF NOT FOUND THEN
            RETURN jsonb_build_object(
                'status', 'error',
                'code', 'action.not_found',
                'message', 'replacement version is not published'
            );
        END IF;
    ELSIF v_row.is_default THEN
        -- Замена не указана, а выключаемая версия — default. Проверяем: остаются ли ещё
        -- включённые версии этого же маршрута? Если да — маршрут остался бы включён, но без
        -- default, что запрещено; отказываем и просим явную замену.
        SELECT count(*) INTO v_remaining
        FROM api.action_version
        WHERE module = p_module AND action = p_action AND enabled AND version <> p_version;

        IF v_remaining > 0 THEN
            RETURN jsonb_build_object(
                'status', 'error',
                'code', 'manifest.conflict',
                'message', 'disabling the default version requires a replacement version'
            );
        END IF;
    END IF;

    UPDATE api.action_version
    SET enabled = false, is_default = false
    WHERE module = p_module AND action = p_action AND version = p_version;

    IF p_replacement IS NOT NULL THEN
        UPDATE api.action_version
        SET enabled = true, is_default = true
        WHERE module = p_module AND action = p_action AND version = p_replacement;
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'action',
            'operation', 'disabled',
            'key', p_module || '.' || p_action,
            'version', p_version,
            'replacementVersion', p_replacement
        )
    );
END;
$$;

-- Автономная запись технического следа вызова, выполняемая runtime вне предметной транзакции
-- (см. Course.Api/DispatchRecorder.cs).
CREATE OR REPLACE FUNCTION api.record_dispatch(p_event jsonb)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
BEGIN
    INSERT INTO api.dispatch (
        correlation_id, request_id, module, action, version,
        principal, consumer, payload_hash, status, outcome, error_code, duration_ms
    )
    VALUES (
        (p_event ->> 'correlationId')::uuid,
        nullif(p_event ->> 'requestId', ''),
        p_event ->> 'module',
        p_event ->> 'action',
        (p_event ->> 'version')::integer,
        nullif(p_event ->> 'principal', ''),
        nullif(p_event ->> 'consumer', ''),
        p_event ->> 'payloadHash',
        p_event ->> 'status',
        nullif(p_event ->> 'outcome', ''),
        nullif(p_event ->> 'errorCode', ''),
        (p_event ->> 'durationMs')::integer
    );
END;
$$;

-- Описание РОВНО ОДНОЙ опубликованной версии, используется версионным документом OpenAPI.
-- В отличие от resolve_action, здесь можно получить и ВЫКЛЮЧЕННУЮ версию — она документируется
-- как deprecated (устаревшая), а не просто перестаёт существовать для документации.
CREATE OR REPLACE FUNCTION api.describe_action(
    p_module text,
    p_action text,
    p_version integer
)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_row api.action_version%ROWTYPE;
BEGIN
    SELECT * INTO v_row
    FROM api.action_version
    WHERE module = p_module AND action = p_action AND version = p_version;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'action.not_found');
    END IF;

    RETURN jsonb_build_object('status', 'ok', 'result', api.action_row(v_row));
END;
$$;
