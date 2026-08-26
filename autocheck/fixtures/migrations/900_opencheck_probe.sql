CREATE SCHEMA IF NOT EXISTS opencheck;

CREATE TABLE IF NOT EXISTS opencheck.canary (
    marker text PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION opencheck.probe_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, opencheck
AS $$
DECLARE
    v_mode text := p_payload ->> 'mode';
    v_value text := p_payload ->> 'value';
    v_meta jsonb := jsonb_build_object(
        'correlationId', p_context ->> 'correlationId',
        'actionVersion', 1
    );
BEGIN
    INSERT INTO opencheck.canary(marker) VALUES (v_value);

    IF v_mode = 'error' THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'probe.forced',
            'message', 'forced error',
            'retryable', false,
            'details', '{}'::jsonb,
            'meta', v_meta
        );
    ELSIF v_mode = 'unknown_outcome' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'UNDECLARED',
            'result', jsonb_build_object(
                'stored', true,
                'revision', 1,
                'principal', p_context ->> 'principal'
            ),
            'meta', v_meta
        );
    ELSIF v_mode = 'invalid_result' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'APPLIED',
            'result', jsonb_build_object(
                'stored', 'not-a-boolean',
                'revision', 1,
                'principal', p_context ->> 'principal'
            ),
            'meta', v_meta
        );
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPLIED',
        'result', jsonb_build_object(
            'stored', true,
            'revision', 1,
            'principal', p_context ->> 'principal'
        ),
        'meta', v_meta
    );
END;
$$;

CREATE OR REPLACE FUNCTION opencheck.probe_v2(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, opencheck
AS $$
DECLARE
    v_value text := p_payload ->> 'value';
BEGIN
    INSERT INTO opencheck.canary(marker) VALUES (v_value);
    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPLIED',
        'result', jsonb_build_object(
            'stored', true,
            'revision', 2,
            'principal', p_context ->> 'principal'
        ),
        'meta', jsonb_build_object(
            'correlationId', p_context ->> 'correlationId',
            'actionVersion', 2
        )
    );
END;
$$;
