-- 016_workflow_smoke.sql
-- Встроенное идемпотентное действие для карты workflow-smoke.
--
-- Действие нарочно ничего не знает про workflow: это обычная зарегистрированная функция с той же
-- сигнатурой (jsonb, jsonb) -> jsonb, что и любое предметное действие недели 1. Ключом
-- идемпотентности предметного эффекта служит executionId из доверенного контекста — тот самый,
-- который worker сохраняет неизменным для всех повторов одного задания.

CREATE SCHEMA IF NOT EXISTS training;

COMMENT ON SCHEMA training IS 'Built-in idempotent probe action used by the workflow-smoke map';

-- Отзыв прав PUBLIC делается ПО МЕСТУ, а не один раз в 015: тот REVOKE перечисляет схемы api и
-- workflow и по построению не может покрыть схему, добавленную следующей миграцией. Полагаться
-- при этом на ALTER DEFAULT PRIVILEGES из инициализации кластера для границы доверия недостаточно —
-- по той же причине, по которой это сказано в 015.
REVOKE ALL ON SCHEMA training FROM PUBLIC;

CREATE TABLE IF NOT EXISTS training.canary_effect (
    execution_id text PRIMARY KEY,
    value        text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT clock_timestamp()
);

REVOKE ALL ON training.canary_effect FROM PUBLIC;

CREATE OR REPLACE FUNCTION training.canary_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $fn$
DECLARE
    v_execution_id text := p_context ->> 'executionId';
    v_mode         text := p_payload ->> 'mode';
    v_value        text := p_payload ->> 'value';
    v_outcome      text := 'ROUTE_SIGNAL';
    v_stored       boolean;
BEGIN
    IF coalesce(v_execution_id, '') = '' THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'training.execution_required',
            'message', 'trusted context must carry an executionId',
            'retryable', false,
            'details', '{}'::jsonb
        );
    END IF;

    -- Повторный вызов с тем же executionId не создаёт второго эффекта: ON CONFLICT DO NOTHING —
    -- это и есть доказательство того, что at-least-once доставка задания остаётся безопасной.
    INSERT INTO training.canary_effect (execution_id, value)
    VALUES (v_execution_id, coalesce(v_value, ''))
    ON CONFLICT (execution_id) DO NOTHING;

    v_stored := EXISTS (
        SELECT 1 FROM training.canary_effect WHERE execution_id = v_execution_id
    );

    IF v_mode = 'manual' THEN
        v_outcome := 'ROUTE_MANUAL';
    ELSIF v_mode = 'retry' THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'training.temporary',
            'message', 'probe action asked for a bounded retry',
            'retryable', true,
            'details', '{}'::jsonb
        );
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_outcome,
        'result', jsonb_build_object(
            'stored', v_stored,
            'echo', coalesce(v_value, ''),
            'executionId', v_execution_id
        ),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$fn$;

-- Функция объявлена SECURITY DEFINER и пишет в таблицу, поэтому отзыв идёт ПОСЛЕ её создания:
-- вызвать её должен уметь только api.invoke от имени владельца.
REVOKE EXECUTE ON FUNCTION training.canary_v1(jsonb, jsonb) FROM PUBLIC;
