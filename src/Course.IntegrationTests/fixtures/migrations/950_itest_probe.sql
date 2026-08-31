-- Пробное действие интеграционных тестов: две версии, различимые по полю revision.
-- Отдельная схема itest намеренно не пересекается ни с предметной схемой payment, ни с
-- фикстурами автопроверки, поэтому тесты можно гонять на том же контуре.

CREATE SCHEMA IF NOT EXISTS itest;

CREATE TABLE IF NOT EXISTS itest.probe_effect (
    marker     text NOT NULL,
    revision   integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION itest.probe_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, itest
AS $$
BEGIN
    INSERT INTO itest.probe_effect(marker, revision) VALUES (p_payload ->> 'marker', 1);
    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPLIED',
        'result', jsonb_build_object('revision', 1, 'marker', p_payload ->> 'marker'),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$$;

CREATE OR REPLACE FUNCTION itest.probe_v2(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, itest
AS $$
BEGIN
    INSERT INTO itest.probe_effect(marker, revision) VALUES (p_payload ->> 'marker', 2);
    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPLIED',
        'result', jsonb_build_object('revision', 2, 'marker', p_payload ->> 'marker'),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId')
    );
END;
$$;
