-- 001_core.sql
-- Технические схемы движка исполнения действий (action runtime).
--
--   api       - неизменяемый каталог действий, граница вызова (invoke), хранилище идемпотентности
--               и технический след вызовов
--   payment   - предметное (доменное) состояние: операции и их история только-на-добавление
--   autocheck - read-only (только для чтения) проекции для чёрного ящика автопроверки
--
-- Каждый файл миграции выполняется сервисом cli внутри одной транзакции с командой
-- SET LOCAL ROLE course_owner, поэтому владельцем каждого объекта ниже становится роль
-- course_owner — единственный владелец без права входа (NOLOGIN), см. db/init/00-roles.sh.

CREATE SCHEMA IF NOT EXISTS api;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS autocheck;

COMMENT ON SCHEMA api IS 'Action catalog and the single domain execution entry point';
COMMENT ON SCHEMA payment IS 'Authoritative domain state';
COMMENT ON SCHEMA autocheck IS 'Read-only projections published for black-box verification';

-- Канонический sha256-хеш jsonb-документа. Тип jsonb в PostgreSQL сам нормализует порядок ключей
-- и пробелы при хранении, поэтому один и тот же логический payload всегда даёт один и тот же
-- хеш в нижнем регистре — независимо от того, в каком порядке ключи были в исходном JSON-тексте.
CREATE OR REPLACE FUNCTION api.payload_hash(p_payload jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    SELECT encode(sha256(convert_to(coalesce(p_payload, 'null'::jsonb)::text, 'UTF8')), 'hex');
$$;

-- Превращает JSON-массив строк (jsonb) в обычный SQL-массив text[], сохраняя исходный порядок
-- элементов и молча пропуская элементы, которые строками не являются. Используется, например,
-- для превращения списка scopes из доверенного контекста в массив, удобный для сравнения.
CREATE OR REPLACE FUNCTION api.jsonb_text_array(p_value jsonb)
RETURNS text[]
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    SELECT CASE
        WHEN jsonb_typeof(p_value) <> 'array' THEN ARRAY[]::text[]
        ELSE coalesce(
            (
                SELECT array_agg(element #>> '{}' ORDER BY ordinality)
                FROM jsonb_array_elements(p_value) WITH ORDINALITY AS t(element, ordinality)
                WHERE jsonb_typeof(element) = 'string'
            ),
            ARRAY[]::text[]
        )
    END;
$$;

-- Единый технический конверт ошибки, который формируется на границе базы данных. Используется
-- повсюду внутри api.invoke и функций каталога, чтобы все ошибки, приходящие "снизу" (из
-- PostgreSQL), имели ровно ту же форму, что и ошибки, формируемые в C#-коде.
CREATE OR REPLACE FUNCTION api.error_envelope(
    p_code text,
    p_message text,
    p_context jsonb,
    p_version integer,
    p_details jsonb DEFAULT '{}'::jsonb,
    p_retryable boolean DEFAULT false
)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message,
        'retryable', p_retryable,
        'details', coalesce(p_details, '{}'::jsonb),
        'meta', jsonb_build_object(
            'correlationId', p_context ->> 'correlationId',
            'actionVersion', p_version
        )
    );
$$;
