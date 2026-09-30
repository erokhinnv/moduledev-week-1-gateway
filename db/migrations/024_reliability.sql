-- 024_reliability.sql
-- Неделя 4: надёжная доставка, наблюдаемость и доказательство результата.
--
-- Что добавляет эта миграция:
--   * профиль доставки как настройка САМОЙ базы (ALTER DATABASE ... SET в db/init/00-roles.sh),
--     а не как константа внутри plpgsql;
--   * exponential backoff с jitter и диагностический след исчерпания доставки (dead_at);
--   * проекцию аренды Outbox наружу (lease_owner/lease_version/lease_until/dead_at);
--   * согласованную проекцию очередей для метрик api;
--   * роль наблюдателя autocheck_reader с правом читать ровно представления схемы autocheck;
--   * два зарегистрированных действия схемы diagnostics: trace и stalled.
--
-- Ни одно действие ниже не меняет предметное состояние: diagnostics читает уже сохранённые
-- факты и исполняется тем же generic runtime, что и любое другое действие каталога.

-------------------------------------------------------------------------------
-- Настройки профиля, принадлежащие базе
--
-- Читается настройка уровня БАЗЫ из каталога pg_db_role_setting, а не current_setting().
-- current_setting отдаёт значение СЕССИИ: для custom-параметра оно лишь по умолчанию равно
-- настройке базы, и любая роль вправе переопределить его у себя командой SET. Профиль доставки
-- не имеет права зависеть от того, что выставил в своей сессии клиент границы, поэтому сессионное
-- значение здесь не участвует вовсе.
--
-- Отсутствие ALTER DATABASE (например на старом томе) не ломает границу, а откатывает её к
-- значению, записанному в delivery.delivery_policy.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION api.setting_int(p_name text, p_fallback integer)
RETURNS integer
LANGUAGE plpgsql
STABLE
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_raw text;
BEGIN
    SELECT pg_catalog.substr(entry, pg_catalog.length(p_name) + 2) INTO v_raw
    FROM pg_catalog.pg_db_role_setting s
    CROSS JOIN LATERAL pg_catalog.unnest(s.setconfig) AS entry
    WHERE s.setdatabase = (SELECT d.oid FROM pg_catalog.pg_database d
                           WHERE d.datname = pg_catalog.current_database())
      AND s.setrole = 0
      AND pg_catalog.starts_with(entry, p_name || '=')
    LIMIT 1;

    v_raw := btrim(coalesce(v_raw, ''));

    -- Настройка базы — это текст. Нечисловое или слишком длинное значение не имеет права
    -- превратиться в исключение посреди захвата доставки: граница продолжает работать по
    -- значению из таблицы, а расхождение видно в самой настройке.
    IF v_raw !~ '^[0-9]{1,9}$' THEN
        RETURN p_fallback;
    END IF;

    RETURN v_raw::integer;
END;
$fn$;

-- RFC 3339 UTC одной строкой. Представление времени — часть контракта наблюдаемости, поэтому оно
-- не отдаётся на откуп настройкам DateStyle сессии, которая читает trace.
CREATE OR REPLACE FUNCTION api.rfc3339(p_value timestamptz)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $fn$
    SELECT CASE
        WHEN p_value IS NULL THEN NULL
        ELSE pg_catalog.to_char(p_value AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
    END;
$fn$;

-------------------------------------------------------------------------------
-- Профиль доставки
-------------------------------------------------------------------------------

-- Таблица профилей остаётся значением по умолчанию, но решение теперь принимается по параметрам
-- экспоненты: база, потолок и jitter. Колонка delays_ms недели 3 остаётся наглядной записью той
-- же лестницы (без jitter) и ниже пересчитывается ИЗ экспоненты: список задержек и правило, не
-- совпадающие между собой, — это таблица, которая снова противоречит самой себе.
ALTER TABLE delivery.delivery_policy
    ADD COLUMN IF NOT EXISTS backoff_base_ms integer,
    ADD COLUMN IF NOT EXISTS backoff_max_ms  integer,
    ADD COLUMN IF NOT EXISTS jitter_max_ms   integer;

UPDATE delivery.delivery_policy
SET backoff_base_ms = coalesce(backoff_base_ms, (delays_ms ->> 0)::integer, 1000),
    backoff_max_ms  = coalesce(
        backoff_max_ms,
        (delays_ms ->> (jsonb_array_length(delays_ms) - 1))::integer,
        60000),
    jitter_max_ms   = coalesce(jitter_max_ms, 0);

-- Тестовый профиль недели 4: четыре попытки вместе с первой, аренда доставки 2 секунды.
UPDATE delivery.delivery_policy
SET max_attempts    = 4,
    backoff_base_ms = 200,
    backoff_max_ms  = 800,
    jitter_max_ms   = 100,
    lease_ms        = 2000
WHERE profile = 'test';

ALTER TABLE delivery.delivery_policy
    ALTER COLUMN backoff_base_ms SET NOT NULL,
    ALTER COLUMN backoff_max_ms  SET NOT NULL,
    ALTER COLUMN jitter_max_ms   SET NOT NULL;

ALTER TABLE delivery.delivery_policy DROP CONSTRAINT IF EXISTS delivery_policy_backoff_shape;
ALTER TABLE delivery.delivery_policy
    ADD CONSTRAINT delivery_policy_backoff_shape CHECK (
        backoff_base_ms BETWEEN 1 AND 600000
        AND backoff_max_ms BETWEEN backoff_base_ms AND 600000
        AND jitter_max_ms BETWEEN 0 AND 60000);

-- Лестница строки — следствие её же экспоненты: задержка после отказа номер n равна
-- least(base * 2^(n-1), max). Профиль test остаётся 200/400/800 мс; профиль default вместо
-- прежних 1/5/15/60 с, которых экспонента не даёт, получает то, что действительно применяется, —
-- 1/2/4/8 с. Длина по-прежнему max_attempts - 1 (ограничение delivery_policy_delays_shape из 022).
UPDATE delivery.delivery_policy p
SET delays_ms = coalesce((
    SELECT jsonb_agg(
               least(p.backoff_base_ms::numeric * pg_catalog.power(2::numeric, n - 1),
                     p.backoff_max_ms::numeric)::integer
               ORDER BY n)
    FROM generate_series(1, p.max_attempts - 1) AS n), '[]'::jsonb);

-- Действующий профиль — составное значение, а не строка таблицы: часть полей приходит из
-- настроек базы и вообще не обязана существовать в таблице.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_type t
        JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = 'delivery' AND t.typname = 'outbox_policy')
    THEN
        CREATE TYPE delivery.outbox_policy AS (
            max_attempts    integer,
            backoff_base_ms integer,
            backoff_max_ms  integer,
            jitter_max_ms   integer,
            lease_ms        integer
        );
    END IF;
END;
$$;

DROP FUNCTION IF EXISTS delivery.active_policy();

CREATE OR REPLACE FUNCTION delivery.active_policy()
RETURNS delivery.outbox_policy
LANGUAGE plpgsql
STABLE
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_row    delivery.delivery_policy%ROWTYPE;
    v_result delivery.outbox_policy;
BEGIN
    SELECT p.* INTO v_row
    FROM delivery.delivery_policy p
    WHERE p.profile = CASE
        WHEN coalesce((SELECT s.test_profile FROM workflow.runtime_setting s WHERE s.singleton), false)
        THEN 'test' ELSE 'default' END;

    v_result.max_attempts    := api.setting_int('course.outbox_max_attempts', v_row.max_attempts);
    v_result.backoff_base_ms := api.setting_int('course.outbox_backoff_base_ms', v_row.backoff_base_ms);
    v_result.backoff_max_ms  := api.setting_int('course.outbox_backoff_max_ms', v_row.backoff_max_ms);
    v_result.jitter_max_ms   := api.setting_int('course.outbox_jitter_max_ms', v_row.jitter_max_ms);
    v_result.lease_ms        := api.setting_int('course.outbox_lease_ms', v_row.lease_ms);

    -- Границы профиля проверяются здесь, а не у вызывающего: настройка базы приходит из
    -- окружения контейнера postgres, и опечатка в ней не имеет права остановить доставку
    -- навсегда или сделать аренду бесконечной.
    v_result.max_attempts    := least(greatest(coalesce(v_result.max_attempts, 5), 1), 20);
    v_result.backoff_base_ms := least(greatest(coalesce(v_result.backoff_base_ms, 1000), 1), 600000);
    v_result.backoff_max_ms  := least(greatest(coalesce(v_result.backoff_max_ms, 60000), v_result.backoff_base_ms), 600000);
    v_result.jitter_max_ms   := least(greatest(coalesce(v_result.jitter_max_ms, 0), 0), 60000);
    v_result.lease_ms        := least(greatest(coalesce(v_result.lease_ms, 30000), 250), 600000);

    RETURN v_result;
END;
$fn$;

-- Задержка перед следующей попыткой: экспонента от базы с потолком плюс равномерный jitter.
-- Jitter добавляется ВСЕГДА, а не только при совпадении моментов: два dispatcher'а, получившие
-- отказ в одну миллисекунду, обязаны разойтись по времени сами, без внешнего координатора.
CREATE OR REPLACE FUNCTION delivery.backoff_ms(p_attempt integer, p_policy delivery.outbox_policy)
RETURNS integer
LANGUAGE sql
VOLATILE
SET search_path = pg_catalog
AS $fn$
    SELECT least(
               (p_policy.backoff_base_ms::numeric
                   * pg_catalog.power(2::numeric, least(greatest(coalesce(p_attempt, 1) - 1, 0), 30)))::bigint,
               p_policy.backoff_max_ms::bigint
           )::integer
           + pg_catalog.floor(pg_catalog.random() * (p_policy.jitter_max_ms + 1))::integer;
$fn$;

-------------------------------------------------------------------------------
-- Диагностический след исчерпанной доставки
-------------------------------------------------------------------------------
ALTER TABLE delivery.outbox ADD COLUMN IF NOT EXISTS dead_at timestamptz;

UPDATE delivery.outbox
SET dead_at = updated_at
WHERE state = 'DEAD' AND dead_at IS NULL;

-- Момент исчерпания — свидетельство того же рода, что delivered_at и confirmed_at: он пишется
-- один раз и переживает поздний перевод доставки в CONFIRMED.
CREATE OR REPLACE FUNCTION delivery.outbox_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'outbox messages cannot be deleted'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (NEW.outbox_id, NEW.external_request_id, NEW.correlation_id, NEW.amount, NEW.currency, NEW.created_at)
       IS DISTINCT FROM
       (OLD.outbox_id, OLD.external_request_id, OLD.correlation_id, OLD.amount, OLD.currency, OLD.created_at)
    THEN
        RAISE EXCEPTION 'outbox message identity, key and body are immutable'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF OLD.state = 'CONFIRMED' AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'confirmed delivery is final'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF (OLD.provider_payment_id IS NOT NULL AND NEW.provider_payment_id IS DISTINCT FROM OLD.provider_payment_id)
       OR (OLD.delivered_at IS NOT NULL AND NEW.delivered_at IS DISTINCT FROM OLD.delivered_at)
       OR (OLD.confirmed_at IS NOT NULL AND NEW.confirmed_at IS DISTINCT FROM OLD.confirmed_at)
       OR (OLD.dead_at IS NOT NULL AND NEW.dead_at IS DISTINCT FROM OLD.dead_at)
    THEN
        RAISE EXCEPTION 'delivery evidence is write-once'
            USING ERRCODE = 'restrict_violation';
    END IF;

    IF NEW.lease_version < OLD.lease_version OR NEW.attempt_count < OLD.attempt_count THEN
        RAISE EXCEPTION 'outbox lease version and attempt count cannot go backwards'
            USING ERRCODE = 'restrict_violation';
    END IF;

    NEW.updated_at := clock_timestamp();
    RETURN NEW;
END;
$fn$;

ALTER TABLE delivery.outbox DROP CONSTRAINT IF EXISTS outbox_dead_shape;
ALTER TABLE delivery.outbox
    ADD CONSTRAINT outbox_dead_shape CHECK (state <> 'DEAD' OR dead_at IS NOT NULL);

-------------------------------------------------------------------------------
-- Захват доставки
--
-- Отличия от недели 3: срок аренды берётся из действующего профиля, а исчерпание аренды
-- оставляет и момент смерти, и сохранённый счётчик попыток.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)
RETURNS TABLE (
    outbox_id uuid,
    lease_version bigint,
    external_request_id text,
    correlation_id uuid,
    amount text,
    currency text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
#variable_conflict use_column
DECLARE
    v_limit  integer := least(greatest(coalesce(p_limit, 1), 1), 16);
    v_policy delivery.outbox_policy;
    v_id     uuid;
    v_row    delivery.outbox%ROWTYPE;
BEGIN
    IF coalesce(p_owner, '') = '' OR length(p_owner) > 128 OR p_owner ~ '[\r\n]' THEN
        RAISE EXCEPTION 'outbox owner is required'
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    v_policy := delivery.active_policy();

    FOR v_id IN
        SELECT o.outbox_id
        FROM delivery.outbox o
        WHERE (o.state IN ('PENDING', 'RETRY_WAIT') AND o.next_attempt_at <= clock_timestamp())
           OR (o.state = 'LEASED' AND o.lease_until <= clock_timestamp())
        ORDER BY o.next_attempt_at, o.created_at
        LIMIT v_limit
        FOR UPDATE SKIP LOCKED
    LOOP
        SELECT * INTO v_row FROM delivery.outbox o WHERE o.outbox_id = v_id;

        -- Истёкшая аренда означает попытку без отчёта. Она расходует бюджет попыток так же, как
        -- transport error: иначе исчезающий dispatcher перезахватывал бы доставку вечно.
        IF v_row.state = 'LEASED' AND v_row.attempt_count >= v_policy.max_attempts THEN
            UPDATE delivery.outbox o
            SET state = 'DEAD',
                last_error_code = 'lease.expired.terminal',
                dead_at = clock_timestamp(),
                lease_owner = NULL,
                lease_until = NULL
            WHERE o.outbox_id = v_id;
            CONTINUE;
        END IF;

        -- Перехват истёкшей аренды оставляет след: предыдущая попытка могла дойти до provider, и
        -- без кода в проекции её нельзя было бы отличить от доставки, у которой отказов не было.
        UPDATE delivery.outbox o
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = o.lease_version + 1,
            lease_until = clock_timestamp() + make_interval(secs => v_policy.lease_ms / 1000.0),
            attempt_count = o.attempt_count + 1,
            last_error_code = CASE WHEN v_row.state = 'LEASED' THEN 'lease.expired.retryable' ELSE o.last_error_code END
        WHERE o.outbox_id = v_id
        RETURNING o.* INTO v_row;

        outbox_id := v_row.outbox_id;
        lease_version := v_row.lease_version;
        external_request_id := v_row.external_request_id;
        correlation_id := v_row.correlation_id;
        amount := v_row.amount;
        currency := v_row.currency;
        RETURN NEXT;
    END LOOP;
END;
$fn$;

-------------------------------------------------------------------------------
-- Отчёт об отказе попытки
--
-- Отличия от недели 3: задержка считается экспонентой с jitter, а исчерпание фиксирует dead_at.
-- Сам факт исчерпания остаётся ТЕХНИЧЕСКИМ: ни статус операции, ни состояние процесса здесь не
-- меняются — ожидающий квитанцию процесс продолжит ждать, и поздняя квитанция его завершит.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION delivery.fail_outbox(
    p_outbox_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_row       delivery.outbox%ROWTYPE;
    v_policy    delivery.outbox_policy;
    v_status    integer;
    v_retryable boolean;
    v_code      text;
    v_delay_ms  integer;
BEGIN
    SELECT * INTO v_row FROM delivery.outbox o WHERE o.outbox_id = p_outbox_id FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.outbox_not_found');
    END IF;

    IF v_row.state = 'CONFIRMED' THEN
        RETURN jsonb_build_object('status', 'ok', 'applied', false, 'state', v_row.state);
    END IF;

    IF v_row.state <> 'LEASED'
       OR v_row.lease_owner IS DISTINCT FROM p_owner
       OR v_row.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.lease_stale', 'state', v_row.state);
    END IF;

    -- Повторяемость решает PostgreSQL по самому факту, а не по суффиксу, присланному клиентом.
    IF p_error_code = 'transport.error.retryable' THEN
        v_retryable := true;
        v_code := p_error_code;
    ELSIF p_error_code ~ '^http\.[1-5][0-9]{2}\.(retryable|terminal)$' THEN
        v_status := substring(p_error_code FROM '^http\.([0-9]{3})\.')::integer;
        v_retryable := v_status IN (408, 429) OR v_status BETWEEN 500 AND 599;
        v_code := 'http.' || v_status || CASE WHEN v_retryable THEN '.retryable' ELSE '.terminal' END;
    ELSIF p_error_code = 'response.invalid.terminal' THEN
        v_retryable := false;
        v_code := p_error_code;
    ELSE
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.error_code_invalid', 'state', v_row.state);
    END IF;

    v_policy := delivery.active_policy();

    IF v_retryable AND v_row.attempt_count < v_policy.max_attempts THEN
        v_delay_ms := delivery.backoff_ms(v_row.attempt_count, v_policy);

        UPDATE delivery.outbox o
        SET state = 'RETRY_WAIT',
            last_error_code = v_code,
            next_attempt_at = clock_timestamp() + make_interval(secs => v_delay_ms / 1000.0),
            lease_owner = NULL,
            lease_until = NULL
        WHERE o.outbox_id = p_outbox_id;

        RETURN jsonb_build_object('status', 'ok', 'applied', true, 'state', 'RETRY_WAIT', 'nextAttemptInMs', v_delay_ms);
    END IF;

    UPDATE delivery.outbox o
    SET state = 'DEAD',
        last_error_code = v_code,
        dead_at = clock_timestamp(),
        lease_owner = NULL,
        lease_until = NULL
    WHERE o.outbox_id = p_outbox_id;

    RETURN jsonb_build_object('status', 'ok', 'applied', true, 'state', 'DEAD');
END;
$fn$;

-------------------------------------------------------------------------------
-- Проекция аренды Outbox наружу
-------------------------------------------------------------------------------
-- Представление пересоздаётся, а не заменяется: CREATE OR REPLACE VIEW умеет добавлять колонки
-- только в конец списка, а поля аренды читаются рядом с состоянием и счётчиком попыток —
-- порядок колонок контрактом не является, но читаемость проекции стоит одного DROP.
DROP VIEW IF EXISTS autocheck.outbox;

CREATE VIEW autocheck.outbox AS
SELECT
    o.outbox_id                 AS outbox_id,
    o.external_request_id::text AS external_request_id,
    o.state::text               AS state,
    o.attempt_count::integer    AS attempt_count,
    o.lease_owner::text         AS lease_owner,
    o.lease_version::bigint     AS lease_version,
    o.lease_until               AS lease_until,
    o.next_attempt_at           AS next_attempt_at,
    o.last_error_code::text     AS last_error_code,
    o.created_at                AS created_at,
    o.delivered_at              AS delivered_at,
    o.dead_at                   AS dead_at
FROM delivery.outbox o;

GRANT SELECT ON autocheck.outbox TO course_runtime;

-------------------------------------------------------------------------------
-- Согласованная проекция очередей для метрик api
--
-- Метрики считает PostgreSQL по тому же состоянию, которое видит проверка: иначе «метрика
-- расходится с очередью» стало бы вопросом того, кто из двух счётчиков ошибся.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION api.runtime_metrics()
RETURNS jsonb
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
    SELECT jsonb_build_object(
        -- «Доступно для захвата сейчас» — ровно то условие, по которому кандидатов выбирает
        -- workflow.claim_jobs: готовые и дождавшиеся своего времени задания И задания с истёкшей
        -- арендой. Брошенное погибшим исполнителем задание не выполняется и ждёт перехвата —
        -- очередь без него показывала бы ноль именно тогда, когда работа стоит.
        'workflowJobsReady', (
            SELECT count(*) FROM workflow.workflow_job j
            WHERE (j.state IN ('READY', 'RETRY_WAIT') AND j.next_attempt_at <= clock_timestamp())
               OR (j.state = 'LEASED' AND j.lease_until IS NOT NULL AND j.lease_until <= clock_timestamp())),
        -- Возраст считается от момента, с которого задание СТАЛО доступным, а не от его
        -- создания: отложенный retry не имеет права выглядеть залежавшейся очередью. Для
        -- брошенного задания этот момент — истечение аренды.
        'workflowJobOldestAgeSeconds', (
            SELECT round(coalesce(extract(epoch FROM (clock_timestamp() - min(
                       CASE WHEN j.state = 'LEASED' THEN j.lease_until ELSE j.next_attempt_at END))), 0), 3)
            FROM workflow.workflow_job j
            WHERE (j.state IN ('READY', 'RETRY_WAIT') AND j.next_attempt_at <= clock_timestamp())
               OR (j.state = 'LEASED' AND j.lease_until IS NOT NULL AND j.lease_until <= clock_timestamp())),
        'workflowProcessesWaiting', (
            SELECT count(*) FROM workflow.process_instance p
            WHERE p.state IN ('WAITING_SIGNAL', 'WAITING_MANUAL')),
        -- DELIVERED, DEAD и CONFIRMED не входят в обе метрики Outbox: очередь доставки — это
        -- то, что ещё будет отправлено, а не то, что уже завершилось тем или иным исходом.
        'outboxPending', (
            SELECT count(*) FROM delivery.outbox o
            WHERE o.state IN ('PENDING', 'LEASED', 'RETRY_WAIT')),
        'outboxOldestAgeSeconds', (
            SELECT round(coalesce(extract(epoch FROM (clock_timestamp() - min(o.created_at))), 0), 3)
            FROM delivery.outbox o
            WHERE o.state IN ('PENDING', 'LEASED', 'RETRY_WAIT')),
        -- Счётчик отказов растёт только вперёд, потому что история процессов append-only:
        -- ProcessFailed пишется один раз и никогда не удаляется.
        'workflowFailuresTotal', (
            SELECT count(*) FROM workflow.workflow_event e
            WHERE e.event_type = 'ProcessFailed')
    );
$fn$;

GRANT EXECUTE ON FUNCTION api.runtime_metrics() TO course_runtime;

-------------------------------------------------------------------------------
-- Аренда задания: значение по умолчанию принадлежит базе
--
-- Отличий от 023 два. Первое — источник значения, которым заполняется не переданный срок аренды:
-- оно приходит настройкой базы из окружения сервиса postgres, поэтому профиль восстановления
-- задаётся конфигурацией контура, а не константой внутри функции. Второе — описание задания
-- называет закреплённую карту и её версию (flowName, flowVersion): они нужны журналу исполнителя.
-------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION workflow.claim_jobs(
    p_owner text,
    p_limit integer DEFAULT 1,
    p_lease_ms integer DEFAULT NULL
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

    -- Потолок бюджета шага по контракту course-1: больше этого timeout_ms карта объявить не
    -- может, поэтому и аренда не уходит дальше 30,5 с. Прежний потолок «настроенный срок × 3»
    -- (6 с в test profile) делал любой шаг длиннее 5,5 с невыполнимым: аренда истекала на
    -- середине честной работы, задание перехватывалось, готовый эффект откатывался по
    -- lease_stale — и так до потолка попыток (см. 023 и ADR 0004).
    c_task_timeout_max_ms constant integer := 30000;

    -- Значения потолка попыток по умолчанию. Они дублируют DEFAULT колонок runtime_setting не по
    -- невнимательности: потолок — предохранитель, и он не имеет права зависеть от того, лежит ли
    -- в таблице строка. Без этих значений отсутствующая настройка давала бы NULL, сравнение
    -- attempt_count >= NULL — тоже NULL, и предохранитель молча переставал бы существовать.
    c_default_ceiling_factor  constant integer := 3;
    c_default_ceiling_minimum constant integer := 10;

    v_now        timestamptz := clock_timestamp();
    -- Отличие от 023: срок аренды по умолчанию принадлежит базе. Он приходит
    -- настройкой из окружения сервиса postgres, поэтому наблюдаемое время восстановления
    -- задаётся конфигурацией контура, а не константой внутри функции.
    v_floor_ms   integer := greatest(
        coalesce(p_lease_ms, api.setting_int('course.job_lease_ms', 30000)), 250);
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
        -- времени, а значит и минимальный разумный срок аренды.
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

        -- Аренда должна переживать объявленный бюджет действия, иначе второй исполнитель
        -- перехватывает задание на середине работы первого. Настроенный срок — нижняя граница
        -- для коротких действий; для длинных аренда покрывает бюджет шага целиком. Верхняя
        -- граница — контрактный максимум бюджета плюс запас, а не кратность к профилю: карта не
        -- может отодвинуть перехват дальше 30,5 с, и ни одно допустимое действие не
        -- перехватывается на середине работы.
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
            -- Имя и версия закреплённой карты нужны исполнителю только для журнала: запись о шаге
            -- обязана называть процесс, карту и её версию. Ветвиться по ним он по-прежнему не может
            -- и не должен — исполнение определяют поля задачи ниже.
            'flowName', f.flow_name,
            'flowVersion', fv.version,
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
        JOIN workflow.flow_definition f ON f.flow_id = p.flow_id
        JOIN workflow.flow_version fv ON fv.flow_version_id = p.flow_version_id
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

GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer, integer) TO workflow_worker;

-------------------------------------------------------------------------------
-- Схема наблюдаемости
--
-- diagnostics.* — обычные зарегистрированные действия: тот же generic runtime, тот же манифест,
-- та же проверка policy. Клиент по-прежнему не выбирает ни базу, ни схему, ни функцию, ни SQL.
-------------------------------------------------------------------------------
CREATE SCHEMA IF NOT EXISTS diagnostics;

COMMENT ON SCHEMA diagnostics IS 'Read-only projections of saved facts for observability actions';

REVOKE ALL ON SCHEMA diagnostics FROM PUBLIC;

-- Операции с затянувшимся ожиданием квитанции. Признак «затянулось» — не таймер наблюдателя, а
-- сохранённый факт: автоматические попытки доставки исчерпаны (Outbox DEAD), а предметный
-- результат до сих пор не решён. Именно такая операция требует ручного разбирательства.
CREATE OR REPLACE FUNCTION diagnostics.stalled_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_items jsonb;
BEGIN
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR p_payload <> '{}'::jsonb THEN
        RETURN api.action_fail('payload.invalid', 'diagnostics.stalled accepts an empty payload');
    END IF;

    SELECT coalesce(jsonb_agg(item ORDER BY item ->> 'operationId'), '[]'::jsonb)
    INTO v_items
    FROM (
        SELECT jsonb_build_object(
            'operationId', e.operation_id,
            'processId', e.process_id,
            'externalRequestId', e.external_request_id
        ) AS item
        FROM delivery.outbox o
        JOIN payment.external_request e ON e.external_request_id = o.external_request_id
        JOIN payment.operation op ON op.operation_id = e.operation_id
        WHERE o.state = 'DEAD'
          AND op.status = 'PROCESSING'
    ) AS stalled;

    RETURN api.action_ok(p_context, 'FOUND', jsonb_build_object('items', v_items));
END;
$fn$;

-- К какой цепочке относится один записанный вызов действия. Вызов адресуется ключом запроса, а
-- ключ у разных вызовов разной природы, поэтому связи перебираются от самой точной к общей:
--   * сохранённый результат идемпотентной команды называет операцию или процесс сам — так
--     связываются payment.submit и workflow.manual, чей ключ не лежит ни в одной предметной строке;
--   * ключ payment.request хранится в самой операции;
--   * ключ receipt.accept — это messageId квитанции;
--   * вызов действия исполнителем адресован executionId задания (успех) или attemptId (отказ).
-- Вызов без ключа (чтение) ни с чем не связывается: связывать его было бы нечем, кроме догадки.
CREATE OR REPLACE FUNCTION diagnostics.dispatch_chain(p_module text, p_action text, p_request_id text)
RETURNS TABLE (operation_id uuid, process_id uuid)
LANGUAGE plpgsql
STABLE
SET search_path = pg_catalog
AS $fn$
#variable_conflict use_column
DECLARE
    v_uuid uuid := api.try_uuid(p_request_id);
BEGIN
    IF p_request_id IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT o.operation_id, coalesce(o.process_id, p.process_id)
    FROM api.idempotency_record r
    LEFT JOIN payment.operation o
           ON o.operation_id = api.try_uuid(r.response #>> '{result,operationId}')
    LEFT JOIN workflow.process_instance p
           ON p.process_id = api.try_uuid(r.response #>> '{result,processId}')
    WHERE r.module = p_module
      AND r.action = p_action
      AND r.request_id = p_request_id
      AND (o.operation_id IS NOT NULL OR p.process_id IS NOT NULL)
    ORDER BY r.created_at, r.idempotency_id
    LIMIT 1;
    IF FOUND THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT o.operation_id, o.process_id
    FROM payment.operation o
    WHERE o.request_id = p_request_id
    ORDER BY o.created_at, o.operation_id
    LIMIT 1;
    IF FOUND THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT r.operation_id, r.process_id
    FROM payment.receipt r
    WHERE r.message_id = p_request_id;
    IF FOUND THEN
        RETURN;
    END IF;

    IF v_uuid IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT NULL::uuid, j.process_id
    FROM workflow.workflow_job j
    WHERE j.execution_id = v_uuid
    ORDER BY j.created_at, j.job_id
    LIMIT 1;
    IF FOUND THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT NULL::uuid, j.process_id
    FROM workflow.task_attempt a
    JOIN workflow.workflow_job j ON j.job_id = a.job_id
    WHERE a.attempt_id = v_uuid;
END;
$fn$;

-- Полный путь одной операции по сохранённым связям. Trace ничего не парсит из логов: каждое
-- поле ниже — это строка таблицы, а не разобранная строка журнала.
CREATE OR REPLACE FUNCTION diagnostics.trace_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_identifier   text := p_payload ->> 'identifier';
    v_uuid         uuid;
    v_matched      jsonb := '[]'::jsonb;
    v_operation_id uuid;
    v_process_id   uuid;
    v_hit_op       uuid;
    v_hit_process  uuid;
    v_operation    payment.operation%ROWTYPE;
    v_process      workflow.process_instance%ROWTYPE;
    v_request_ids  text[];
    v_correlations uuid[];
    v_result       jsonb;
BEGIN
    IF jsonb_typeof(p_payload -> 'identifier') IS DISTINCT FROM 'string'
       OR length(v_identifier) NOT BETWEEN 1 AND 200
       OR v_identifier ~ '[\r\n]'
    THEN
        RETURN api.action_fail('payload.invalid', 'identifier is required');
    END IF;

    v_uuid := api.try_uuid(v_identifier);

    -- Каждый вид идентификатора проверяется отдельно и ТОЛЬКО дополняет уже найденное: один и
    -- тот же текст может быть и requestId команды, и messageId квитанции, и тогда matchedBy
    -- честно перечисляет оба. Неудачный поиск не имеет права стереть найденную связь, поэтому
    -- SELECT ... INTO пишет в отдельные переменные, а не прямо в якорь цепочки.
    IF v_uuid IS NOT NULL THEN
        SELECT o.operation_id, o.process_id INTO v_hit_op, v_hit_process
        FROM payment.operation o WHERE o.operation_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["operationId"]'::jsonb;
            v_operation_id := coalesce(v_operation_id, v_hit_op);
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.process_instance p WHERE p.process_id = v_uuid) THEN
            v_matched := v_matched || '["processId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_uuid);
        END IF;

        SELECT s.process_id INTO v_hit_process
        FROM workflow.step_instance s WHERE s.step_instance_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["stepInstanceId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        SELECT j.process_id INTO v_hit_process
        FROM workflow.workflow_job j WHERE j.job_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["jobId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        SELECT j.process_id INTO v_hit_process
        FROM workflow.workflow_job j WHERE j.execution_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["executionId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        SELECT j.process_id INTO v_hit_process
        FROM workflow.task_attempt a
        JOIN workflow.workflow_job j ON j.job_id = a.job_id
        WHERE a.attempt_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["attemptId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        SELECT d.process_id INTO v_hit_process
        FROM workflow.decision d WHERE d.decision_id = v_uuid;
        IF FOUND THEN
            v_matched := v_matched || '["decisionId"]'::jsonb;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;

        IF EXISTS (SELECT 1 FROM api.dispatch d WHERE d.correlation_id = v_uuid) THEN
            v_matched := v_matched || '["correlationId"]'::jsonb;
            v_correlations := array_append(v_correlations, v_uuid);

            -- correlationId вызова payment.prepare_external сохранён вместе с внешним запросом и
            -- доставкой (он же уходит provider заголовком X-Correlation-ID). correlationId любого
            -- другого вызова связан с цепочкой через ключ запроса этого вызова.
            SELECT e.operation_id, e.process_id INTO v_hit_op, v_hit_process
            FROM payment.external_request e WHERE e.correlation_id = v_uuid;
            IF NOT FOUND THEN
                SELECT link.operation_id, link.process_id INTO v_hit_op, v_hit_process
                FROM api.dispatch d
                CROSS JOIN LATERAL diagnostics.dispatch_chain(d.module, d.action, d.request_id) AS link
                WHERE d.correlation_id = v_uuid
                ORDER BY d.occurred_at, d.dispatch_id
                LIMIT 1;
            END IF;
            IF FOUND THEN
                v_operation_id := coalesce(v_operation_id, v_hit_op);
                v_process_id := coalesce(v_process_id, v_hit_process);
            END IF;
        END IF;
    END IF;

    SELECT e.operation_id, e.process_id INTO v_hit_op, v_hit_process
    FROM payment.external_request e WHERE e.external_request_id = v_identifier;
    IF FOUND THEN
        v_matched := v_matched || '["externalRequestId"]'::jsonb;
        v_operation_id := coalesce(v_operation_id, v_hit_op);
        v_process_id := coalesce(v_process_id, v_hit_process);
    END IF;

    IF EXISTS (SELECT 1 FROM delivery.inbox i WHERE i.message_id = v_identifier) THEN
        v_matched := v_matched || '["messageId"]'::jsonb;

        SELECT r.operation_id, r.process_id INTO v_hit_op, v_hit_process
        FROM payment.receipt r WHERE r.message_id = v_identifier;
        IF FOUND THEN
            v_operation_id := coalesce(v_operation_id, v_hit_op);
            v_process_id := coalesce(v_process_id, v_hit_process);
        ELSE
            SELECT i.process_id INTO v_hit_process
            FROM delivery.inbox i WHERE i.message_id = v_identifier;
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;
    END IF;

    IF EXISTS (SELECT 1 FROM api.dispatch d WHERE d.request_id = v_identifier)
       OR EXISTS (SELECT 1 FROM payment.operation o WHERE o.request_id = v_identifier)
    THEN
        v_matched := v_matched || '["requestId"]'::jsonb;

        SELECT o.operation_id, o.process_id INTO v_hit_op, v_hit_process
        FROM payment.operation o WHERE o.request_id = v_identifier
        ORDER BY o.created_at, o.operation_id
        LIMIT 1;
        IF NOT FOUND THEN
            -- Ключ любой другой команды цепочки: submit, ручное решение, квитанция или вызов
            -- действия исполнителем. Он известен только записанному вызову.
            SELECT link.operation_id, link.process_id INTO v_hit_op, v_hit_process
            FROM api.dispatch d
            CROSS JOIN LATERAL diagnostics.dispatch_chain(d.module, d.action, d.request_id) AS link
            WHERE d.request_id = v_identifier
            ORDER BY d.occurred_at, d.dispatch_id
            LIMIT 1;
        END IF;
        IF FOUND THEN
            v_operation_id := coalesce(v_operation_id, v_hit_op);
            v_process_id := coalesce(v_process_id, v_hit_process);
        END IF;
    END IF;

    IF jsonb_array_length(v_matched) = 0 THEN
        RETURN api.action_fail('diagnostics.trace_not_found', 'identifier is not known to the platform');
    END IF;

    -- Связь operation <-> process достраивается в обе стороны: по какому бы концу цепочки ни
    -- пришёл идентификатор, набор предметных фактов обязан получиться один и тот же.
    IF v_operation_id IS NULL AND v_process_id IS NOT NULL THEN
        SELECT o.operation_id INTO v_operation_id
        FROM payment.operation o WHERE o.process_id = v_process_id;
    END IF;

    IF v_operation_id IS NOT NULL THEN
        SELECT * INTO v_operation FROM payment.operation WHERE operation_id = v_operation_id;
        v_process_id := coalesce(v_process_id, v_operation.process_id);
    END IF;

    IF v_process_id IS NOT NULL THEN
        SELECT * INTO v_process FROM workflow.process_instance WHERE process_id = v_process_id;
    END IF;

    -- След вызовов собирается по тем же явным связям, по которым вызов разрешается в цепочку
    -- (diagnostics.dispatch_chain), только в обратную сторону: ключ запроса команды, создавшей
    -- операцию; идентификатор квитанции, он же ключ receipt.accept; executionId заданий и attemptId
    -- попыток — ими адресованы вызовы действий исполнителем; ключи идемпотентных команд, чей
    -- сохранённый результат называет эту операцию или этот процесс (payment.submit,
    -- workflow.manual); и correlation id, сохранённый вместе с внешним запросом и доставкой.
    -- Набор не зависит от того, каким идентификатором спросили. Журналы не читаются вовсе.
    SELECT array_agg(DISTINCT value) INTO v_request_ids
    FROM (
        SELECT v_operation.request_id AS value
        UNION ALL
        SELECT r.message_id FROM payment.receipt r WHERE r.operation_id = v_operation_id
        UNION ALL
        SELECT j.execution_id::text FROM workflow.workflow_job j WHERE j.process_id = v_process_id
        UNION ALL
        SELECT a.attempt_id::text
        FROM workflow.task_attempt a
        JOIN workflow.workflow_job j ON j.job_id = a.job_id
        WHERE j.process_id = v_process_id
        UNION ALL
        SELECT r.request_id
        FROM api.idempotency_record r
        WHERE r.response IS NOT NULL
          AND (r.response #>> '{result,operationId}' = v_operation_id::text
               OR r.response #>> '{result,processId}' = v_process_id::text)
    ) AS request_ids
    WHERE value IS NOT NULL;

    SELECT array_agg(DISTINCT value) INTO v_correlations
    FROM (
        SELECT unnest(coalesce(v_correlations, ARRAY[]::uuid[])) AS value
        UNION ALL
        SELECT e.correlation_id FROM payment.external_request e WHERE e.operation_id = v_operation_id
        UNION ALL
        SELECT o.correlation_id
        FROM delivery.outbox o
        JOIN payment.external_request e ON e.external_request_id = o.external_request_id
        WHERE e.operation_id = v_operation_id
    ) AS correlations
    WHERE value IS NOT NULL;

    v_result := jsonb_build_object(
        'query', jsonb_build_object(
            'identifier', v_identifier,
            'matchedBy', (SELECT coalesce(jsonb_agg(DISTINCT kind), '[]'::jsonb)
                          FROM jsonb_array_elements_text(v_matched) AS kinds(kind))
        ),
        'dispatches', coalesce((
            SELECT jsonb_agg(item ORDER BY occurred_at, correlation_id)
            FROM (
                SELECT d.occurred_at, d.correlation_id, jsonb_build_object(
                    'correlationId', d.correlation_id,
                    'requestId', d.request_id,
                    'module', d.module,
                    'action', d.action,
                    'version', d.version,
                    'status', d.status,
                    'outcome', d.outcome,
                    'occurredAt', api.rfc3339(d.occurred_at)
                ) AS item
                FROM api.dispatch d
                -- Вызов, не дошедший до разрешения версии действия, шагом пути операции не
                -- является: у него нет ни версии, ни ключа запроса, по которому его связать.
                WHERE d.version IS NOT NULL
                  AND d.request_id IS NOT NULL
                  AND (d.request_id = ANY (coalesce(v_request_ids, ARRAY[]::text[]))
                       OR d.correlation_id = ANY (coalesce(v_correlations, ARRAY[]::uuid[])))
            ) AS dispatches
        ), '[]'::jsonb),
        'operation', CASE
            WHEN v_operation.operation_id IS NULL THEN 'null'::jsonb
            ELSE jsonb_build_object(
                'operationId', v_operation.operation_id,
                'requestId', v_operation.request_id,
                'operationKind', v_operation.operation_kind,
                'amount', payment.amount_text(v_operation.amount),
                'currency', v_operation.currency,
                'status', v_operation.status,
                'processId', v_operation.process_id,
                'createdAt', api.rfc3339(v_operation.created_at),
                'updatedAt', api.rfc3339(v_operation.updated_at)
            )
        END,
        'operationEvents', coalesce((
            SELECT jsonb_agg(item ORDER BY occurred_at, event_id)
            FROM (
                SELECT e.occurred_at, e.event_id, jsonb_build_object(
                    'eventId', e.event_id,
                    'eventType', e.event_type,
                    'occurredAt', api.rfc3339(e.occurred_at)
                ) AS item
                FROM payment.operation_event e
                WHERE e.operation_id = v_operation_id
            ) AS events
        ), '[]'::jsonb),
        'process', CASE
            WHEN v_process.process_id IS NULL THEN 'null'::jsonb
            ELSE jsonb_build_object(
                'processId', v_process.process_id,
                'flowName', (SELECT f.flow_name FROM workflow.flow_definition f WHERE f.flow_id = v_process.flow_id),
                'flowVersion', (SELECT fv.version FROM workflow.flow_version fv WHERE fv.flow_version_id = v_process.flow_version_id),
                'state', v_process.state,
                'currentStepKey', v_process.current_step_key,
                'createdAt', api.rfc3339(v_process.created_at),
                'updatedAt', api.rfc3339(v_process.updated_at)
            )
        END,
        'steps', coalesce((
            SELECT jsonb_agg(item ORDER BY entered_at, step_instance_id)
            FROM (
                SELECT s.entered_at, s.step_instance_id, jsonb_build_object(
                    'stepInstanceId', s.step_instance_id,
                    'stepKey', s.step_key,
                    'stepType', s.step_type,
                    'state', s.state,
                    'outcome', s.outcome,
                    'enteredAt', api.rfc3339(s.entered_at),
                    'completedAt', api.rfc3339(s.completed_at)
                ) AS item
                FROM workflow.step_instance s
                WHERE s.process_id = v_process_id
            ) AS steps
        ), '[]'::jsonb),
        'jobs', coalesce((
            SELECT jsonb_agg(item ORDER BY created_at, job_id)
            FROM (
                SELECT j.created_at, j.job_id, jsonb_build_object(
                    'jobId', j.job_id,
                    'stepInstanceId', j.step_instance_id,
                    'executionId', j.execution_id,
                    'state', j.state,
                    'leaseVersion', j.lease_version,
                    'attemptCount', j.attempt_count,
                    'nextAttemptAt', api.rfc3339(j.next_attempt_at)
                ) AS item
                FROM workflow.workflow_job j
                WHERE j.process_id = v_process_id
            ) AS jobs
        ), '[]'::jsonb),
        'attempts', coalesce((
            SELECT jsonb_agg(item ORDER BY started_at, attempt_id)
            FROM (
                SELECT a.started_at, a.attempt_id, jsonb_build_object(
                    'attemptId', a.attempt_id,
                    'jobId', a.job_id,
                    'executionId', a.execution_id,
                    'leaseVersion', a.lease_version,
                    'attemptNumber', a.attempt_number,
                    'status', a.status,
                    'outcome', a.outcome,
                    'errorCode', a.error_code,
                    'startedAt', api.rfc3339(a.started_at),
                    'finishedAt', api.rfc3339(a.finished_at)
                ) AS item
                FROM workflow.task_attempt a
                JOIN workflow.workflow_job j ON j.job_id = a.job_id
                WHERE j.process_id = v_process_id
            ) AS attempts
        ), '[]'::jsonb),
        'outbox', coalesce((
            SELECT jsonb_agg(item ORDER BY created_at, outbox_id)
            FROM (
                SELECT o.created_at, o.outbox_id, jsonb_build_object(
                    'outboxId', o.outbox_id,
                    'externalRequestId', o.external_request_id,
                    'state', o.state,
                    'attemptCount', o.attempt_count,
                    'leaseVersion', o.lease_version,
                    'nextAttemptAt', api.rfc3339(o.next_attempt_at),
                    'lastErrorCode', o.last_error_code,
                    'createdAt', api.rfc3339(o.created_at),
                    'deliveredAt', api.rfc3339(o.delivered_at),
                    'deadAt', api.rfc3339(o.dead_at)
                ) AS item
                FROM delivery.outbox o
                JOIN payment.external_request e ON e.external_request_id = o.external_request_id
                WHERE e.operation_id = v_operation_id
            ) AS outbox
        ), '[]'::jsonb),
        'inbox', coalesce((
            SELECT jsonb_agg(item ORDER BY received_at, message_id)
            FROM (
                SELECT i.received_at, i.message_id, jsonb_build_object(
                    'messageId', i.message_id,
                    'state', i.state,
                    'receivedAt', api.rfc3339(i.received_at),
                    'appliedAt', api.rfc3339(i.applied_at)
                ) AS item
                FROM delivery.inbox i
                WHERE i.process_id = v_process_id
            ) AS inbox
        ), '[]'::jsonb),
        'receipts', coalesce((
            SELECT jsonb_agg(item ORDER BY received_at, message_id)
            FROM (
                SELECT r.received_at, r.message_id, jsonb_build_object(
                    'messageId', r.message_id,
                    'externalRequestId', r.external_request_id,
                    'outcome', r.outcome,
                    'signatureValid', r.signature_valid,
                    'receivedAt', api.rfc3339(r.received_at),
                    'appliedAt', api.rfc3339(r.applied_at)
                ) AS item
                FROM payment.receipt r
                WHERE r.operation_id = v_operation_id
            ) AS receipts
        ), '[]'::jsonb),
        'decisions', coalesce((
            SELECT jsonb_agg(item ORDER BY created_at, decision_id)
            FROM (
                SELECT d.created_at, d.decision_id, jsonb_build_object(
                    'decisionId', d.decision_id,
                    'stepInstanceId', d.step_instance_id,
                    'source', d.source,
                    'principal', d.principal,
                    'outcome', d.outcome,
                    'ruleVersion', d.rule_version,
                    'createdAt', api.rfc3339(d.created_at)
                ) AS item
                FROM workflow.decision d
                WHERE d.process_id = v_process_id
            ) AS decisions
        ), '[]'::jsonb)
    );

    RETURN api.action_ok(p_context, 'FOUND', v_result);
END;
$fn$;

-------------------------------------------------------------------------------
-- Итоговая раскладка прав
-------------------------------------------------------------------------------

-- Новые функции создаются владельцем схем, поэтому ALTER DEFAULT PRIVILEGES уже снял с них
-- EXECUTE у PUBLIC. Явный REVOKE повторяет это на случай объекта, созданного иначе: забыть
-- отозвать право дороже, чем отозвать его дважды.
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA api         FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA payment     FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA workflow    FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA delivery    FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA diagnostics FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA payment  FROM PUBLIC;

-- Наблюдатель. Единственное, что ему доступно, — представления схемы autocheck: ни физических
-- отношений, ни функций приложения, ни последовательностей, ни CREATE, ни TEMP, ни членства.
DO $$
DECLARE
    v_schema text;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'autocheck_reader') THEN
        RAISE NOTICE 'autocheck_reader role is absent, observer grants skipped';
        RETURN;
    END IF;

    FOREACH v_schema IN ARRAY ARRAY['api', 'payment', 'workflow', 'delivery', 'diagnostics', 'training', 'autocheck', 'public']
    LOOP
        IF EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = v_schema) THEN
            EXECUTE pg_catalog.format('REVOKE ALL ON ALL TABLES IN SCHEMA %I FROM autocheck_reader', v_schema);
            EXECUTE pg_catalog.format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA %I FROM autocheck_reader', v_schema);
            EXECUTE pg_catalog.format('REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA %I FROM autocheck_reader', v_schema);
            EXECUTE pg_catalog.format('REVOKE ALL ON SCHEMA %I FROM autocheck_reader', v_schema);
        END IF;
    END LOOP;

    EXECUTE 'GRANT USAGE ON SCHEMA autocheck TO autocheck_reader';
    EXECUTE 'GRANT SELECT ON ALL TABLES IN SCHEMA autocheck TO autocheck_reader';

    -- Представления, добавленные будущими миграциями, обязаны быть видны наблюдателю без
    -- отдельного GRANT: иначе новая проекция молча исчезает из чёрного ящика.
    EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA autocheck '
            'GRANT SELECT ON TABLES TO autocheck_reader';
END;
$$;
