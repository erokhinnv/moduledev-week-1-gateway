-- Действие интеграционных тестов, которое честно работает дольше срока аренды test profile.
-- Восемь секунд сна при бюджете шага в пятнадцать: без продления аренды такое задание никогда не
-- завершалось — его перехватывали на середине работы. Та же схема itest, что и у пробы.

CREATE SCHEMA IF NOT EXISTS itest;

CREATE TABLE IF NOT EXISTS itest.slow_effect (
    execution_id text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION itest.slow_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, itest
AS $$
BEGIN
    PERFORM pg_sleep(8);
    INSERT INTO itest.slow_effect(execution_id) VALUES (p_context ->> 'executionId');
    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'DONE',
        'result', jsonb_build_object('executionId', p_context ->> 'executionId'),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$$;

REVOKE EXECUTE ON FUNCTION itest.slow_v1(jsonb, jsonb) FROM PUBLIC;
