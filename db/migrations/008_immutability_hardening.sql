-- 008_immutability_hardening.sql
-- Расширение кортежей неизменяемости до всех полей, которые выводит сервер.
--
-- Оба триггера уже существовали (002_catalog.sql и 005_payment.sql), но сравнивали неполный
-- набор колонок, из-за чего часть server-derived данных можно было переписать привилегированным
-- UPDATE, не нарвавшись на исключение. Здесь эти же функции заменяются целиком через
-- CREATE OR REPLACE — сами триггеры пересоздавать не нужно, они ссылаются на функцию по имени.

-------------------------------------------------------------------------------
-- 1. Операция: в кортеж добавлен consumer.
--
-- principal уже был защищён, а consumer — нет, хотя он ровно такое же выведенное сервером поле
-- идентичности: берётся из проверенного JWT и никогда из payload. Без него привилегированный
-- UPDATE мог переписать записанного потребителя, ослабив доказательную ценность истории.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION payment.operation_identity_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (NEW.operation_id, NEW.request_id, NEW.principal, NEW.consumer, NEW.operation_kind,
        NEW.amount, NEW.currency, NEW.created_at)
       IS DISTINCT FROM
       (OLD.operation_id, OLD.request_id, OLD.principal, OLD.consumer, OLD.operation_kind,
        OLD.amount, OLD.currency, OLD.created_at)
    THEN
        RAISE EXCEPTION 'operation identity and payload fields are immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

-------------------------------------------------------------------------------
-- 2. Версия действия: в кортеж добавлен сам документ manifest.
--
-- Триггер сравнивал manifest_hash и разложенные по колонкам поля, но не исходный документ.
-- Значит, привилегированный UPDATE мог рассогласовать manifest и manifest_hash: хеш остался бы
-- прежним, а сохранённый документ — другим. Читающие пути берут данные из разложенных колонок и
-- подмены не заметили бы, зато аудит, который опирается на документ, стал бы недостоверным.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION api.action_version_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (NEW.module, NEW.action, NEW.version, NEW.http_method, NEW.target_schema,
        NEW.target_function, NEW.request_schema, NEW.response_schema, NEW.outcomes,
        NEW.required_policy, NEW.idempotency_mode, NEW.idempotency_scope,
        NEW.timeout_ms, NEW.manifest, NEW.manifest_hash, NEW.published_at)
       IS DISTINCT FROM
       (OLD.module, OLD.action, OLD.version, OLD.http_method, OLD.target_schema,
        OLD.target_function, OLD.request_schema, OLD.response_schema, OLD.outcomes,
        OLD.required_policy, OLD.idempotency_mode, OLD.idempotency_scope,
        OLD.timeout_ms, OLD.manifest, OLD.manifest_hash, OLD.published_at)
    THEN
        RAISE EXCEPTION 'published action version is immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;
