-- 007_catalog_serialization.sql
-- Сериализация операций каталога по маршруту.
--
-- Проблема, которую чиним. Функции публикации из 003_catalog_api.sql брали FOR UPDATE только на
-- уже существующие строки версий. Из этого следовали две неприятности:
--
--   1. При публикации НОВОЙ версии блокировать было нечего — строки ещё нет. Два параллельных
--      publish разных новых версий одного маршрута могли оба решить стать default, и один из них
--      падал на уникальном индексе action_version_single_default.
--   2. activate_action блокировала все строки маршрута одним запросом, а disable_action — сначала
--      выключаемую версию, затем версию-замену. Разный порядок захвата строк между двумя
--      командами — классический рецепт взаимной блокировки (deadlock).
--
-- Решение: consultative (advisory) блокировка по имени маршрута, которую все три команды берут
-- ПЕРВЫМ делом. Это блокировка по произвольному 64-битному числу, не привязанная ни к какой
-- строке таблицы, поэтому она работает и тогда, когда строки ещё не существует. Суффикс _xact_
-- означает, что блокировка автоматически снимается по завершении транзакции — освобождать её
-- вручную не нужно, и она не переживёт сбой соединения.
--
-- Ключ блокировки — хеш от "module.action", поэтому команды по РАЗНЫМ маршрутам друг друга не
-- ждут, а по одному маршруту выстраиваются в очередь.

-- Публикация манифеста. Отличие от 003_catalog_api.sql — только advisory-блокировка в начале.
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
    -- Сериализация по маршруту: держится до конца транзакции.
    PERFORM pg_advisory_xact_lock(hashtextextended(v_module || '.' || v_action, 0));

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

-- Активация версии. Advisory-блокировка снимает необходимость гадать о порядке захвата строк.
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
    PERFORM pg_advisory_xact_lock(hashtextextended(p_module || '.' || p_action, 0));

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

    -- Порядок захвата строк маршрута зафиксирован по номеру версии — тот же, что в
    -- disable_action ниже. Advisory-блокировка уже исключила гонку, но одинаковый порядок
    -- оставлен намеренно: он делает функции безопасными и сам по себе.
    PERFORM 1 FROM api.action_version
    WHERE module = p_module AND action = p_action
    ORDER BY version
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

-- Выключение версии с обязательной заменой, если выключается текущая default-версия.
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
    PERFORM pg_advisory_xact_lock(hashtextextended(p_module || '.' || p_action, 0));

    -- Единый с activate_action порядок захвата строк маршрута.
    PERFORM 1 FROM api.action_version
    WHERE module = p_module AND action = p_action
    ORDER BY version
    FOR UPDATE;

    SELECT * INTO v_row
    FROM api.action_version
    WHERE module = p_module AND action = p_action AND version = p_version;

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
        WHERE module = p_module AND action = p_action AND version = p_replacement;

        IF NOT FOUND THEN
            RETURN jsonb_build_object(
                'status', 'error',
                'code', 'action.not_found',
                'message', 'replacement version is not published'
            );
        END IF;
    ELSIF v_row.is_default THEN
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
