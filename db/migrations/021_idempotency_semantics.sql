-- 021_idempotency_semantics.sql
-- Семантика идемпотентности закреплена за маршрутом, а не за версией.
--
-- Проблема, которую чиним. Запись идемпотентности уникальна по кортежу
-- (scope, scope_key, module, action, request_id), где scope и scope_key берутся из ТЕКУЩЕЙ
-- версии действия. Публикация же разрешала версиям одного маршрута объявлять разные
-- idempotency_mode и idempotency_scope. После activate версии с другим scope тот же
-- Idempotency-Key с тем же телом попадал в другой кортеж, резервация проходила заново, и
-- зарегистрированная функция исполнялась второй раз: клиент с ретраями получал вторую операцию.
-- С idempotency_mode то же самое: версия с 'none' не резервирует ключ вовсе.
--
-- Решение: mode и scope маршрута фиксирует его первая опубликованная версия, и каждая следующая
-- обязана их повторить. Версии по-прежнему могут менять схемы, target и timeout — всё, кроме
-- ответа на вопрос «что считается повтором». Правило держится в двух местах:
--
--   1. api.publish_action отвечает контрактным manifest.conflict — так его видит CLI;
--   2. триггер на api.action_version — так его не обойти привилегированным INSERT мимо функции.
--
-- Изменять эти поля у уже опубликованной версии и так нельзя (action_version_immutable), поэтому
-- триггер нужен только на INSERT.
--
-- Уже сохранённые версии миграция не переписывает: выбрать за автора, какая из расходящихся
-- семантик верна, нельзя. Маршрут, у которого расхождение уже есть, просто не примет ни одной
-- новой версии — триггер сверяет вставку со ВСЕМИ его версиями. Встроенные действия и fixtures
-- проверки публикуют версии одного маршрута с одинаковой семантикой.

-------------------------------------------------------------------------------
-- 1. Триггер: вставка версии с чужой семантикой отклоняется.
--
-- Берёт ту же advisory-блокировку маршрута, что и команды каталога (007_catalog_serialization.sql).
-- Внутри publish_action она уже захвачена, и повторный захват в той же транзакции бесплатен; а
-- два конкурирующих INSERT в обход функции без неё оба не увидели бы друг друга и оба прошли.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION api.action_version_idempotency_fixed()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.module || '.' || NEW.action, 0));

    IF EXISTS (
        SELECT 1
        FROM api.action_version av
        WHERE av.module = NEW.module
          AND av.action = NEW.action
          AND (av.idempotency_mode, av.idempotency_scope)
              IS DISTINCT FROM (NEW.idempotency_mode, NEW.idempotency_scope)
    ) THEN
        RAISE EXCEPTION 'idempotency semantics of a route are fixed by its first published version'
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS action_version_idempotency_fixed ON api.action_version;
CREATE TRIGGER action_version_idempotency_fixed
    BEFORE INSERT ON api.action_version
    FOR EACH ROW EXECUTE FUNCTION api.action_version_idempotency_fixed();

-------------------------------------------------------------------------------
-- 2. Публикация: отличие от 007_catalog_serialization.sql — только сверка семантики
-- идемпотентности новой версии с уже опубликованными версиями маршрута.
--
-- Сверка стоит ПОСЛЕ ветки «версия уже опубликована»: повторная публикация того же манифеста
-- по-прежнему даёт unchanged, а другой манифест под тем же номером — прежний manifest.conflict.
-------------------------------------------------------------------------------
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
    v_published api.action_version%ROWTYPE;
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

    -- Все опубликованные версии маршрута разделяют одну семантику, поэтому достаточно любой из
    -- них. Сообщение называет её значения, чтобы автор манифеста видел, что именно надо
    -- повторить; это имена из закрытых перечислений каталога, а не данные вызывающего.
    SELECT * INTO v_published
    FROM api.action_version
    WHERE module = v_module AND action = v_action
    ORDER BY version
    LIMIT 1;

    IF FOUND AND (v_published.idempotency_mode, v_published.idempotency_scope)
                 IS DISTINCT FROM (p_manifest ->> 'idempotency_mode', p_manifest ->> 'idempotency_scope')
    THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'manifest.conflict',
            'message', format(
                'idempotency semantics of a route are fixed: published version %s declares mode %s, scope %s',
                v_published.version, v_published.idempotency_mode, v_published.idempotency_scope)
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
