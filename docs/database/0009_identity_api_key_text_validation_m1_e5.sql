-- PoolAI Release 1 M1-E5 API Key Unicode scalar text validation.
--
-- Migration 0008 is signed and immutable. This forward-only correction aligns
-- API Key names and persisted revoke/rotate reasons with the exact OpenAPI
-- scalar, control-character, and frozen White_Space contract. Legal leading
-- and trailing White_Space is preserved; no value is trimmed or rewritten.

CREATE FUNCTION public.poolai_api_key_text_is_valid(
    p_value text,
    p_max_code_points integer
)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_code_point integer;
    v_has_non_whitespace boolean := false;
    v_index integer;
    v_length integer;
BEGIN
    IF p_max_code_points NOT IN (100, 500) THEN
        RETURN false;
    END IF;

    -- PostgreSQL UTF-8 text rejects malformed byte sequences and non-scalar
    -- encodings before this function runs. char_length and substr therefore
    -- count and select Unicode scalar values, including supplementary values.
    v_length := pg_catalog.char_length(p_value);
    IF v_length < 1 OR v_length > p_max_code_points THEN
        RETURN false;
    END IF;

    FOR v_index IN 1..v_length
    LOOP
        v_code_point := pg_catalog.ascii(
            pg_catalog.substr(p_value, v_index, 1));

        IF v_code_point BETWEEN 0 AND 31
            OR v_code_point BETWEEN 127 AND 159
            OR v_code_point IN (8232, 8233)
            OR v_code_point BETWEEN 55296 AND 57343
            OR v_code_point > 1114111 THEN
            RETURN false;
        END IF;

        -- Freeze Unicode White_Space to the exact 25-scalar set used by the
        -- OpenAPI pattern; do not inherit Unicode-version-dependent regex
        -- shorthands from PostgreSQL or a runtime library.
        IF NOT (
            v_code_point BETWEEN 9 AND 13
            OR v_code_point IN (32, 133, 160, 5760)
            OR v_code_point BETWEEN 8192 AND 8202
            OR v_code_point IN (8232, 8233, 8239, 8287, 12288)
        ) THEN
            v_has_non_whitespace := true;
        END IF;
    END LOOP;

    RETURN v_has_non_whitespace;
END;
$function$;

-- Validate existing rows while replacing the length-only 0008 constraints.
-- Any illegal pre-existing value aborts the migration; values are never
-- trimmed, normalized, or silently rewritten.
ALTER TABLE public.api_keys
    DROP CONSTRAINT ck_api_keys_m1_e5_name_length,
    DROP CONSTRAINT ck_api_keys_m1_e5_reason_length,
    ADD CONSTRAINT ck_api_keys_m1_e5_name_text CHECK (
        public.poolai_api_key_text_is_valid(name, 100)
    ),
    ADD CONSTRAINT ck_api_keys_m1_e5_reason_text CHECK (
        revoke_reason IS NULL
        OR public.poolai_api_key_text_is_valid(revoke_reason, 500)
    );

-- Existing entry points are owned by the NOLOGIN runtime owner. Temporarily
-- grant schema CREATE so that owner can replace the exact signatures, then
-- revoke it before the migration transaction can commit.
GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_text_is_valid(text, integer)
    OWNER TO poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;

CREATE OR REPLACE FUNCTION public.poolai_api_key_create(
    p_api_key_id uuid,
    p_user_id uuid,
    p_group_id uuid,
    p_name text,
    p_key_prefix text,
    p_secret_hash bytea,
    p_pepper_version smallint,
    p_expires_at timestamptz,
    p_ip_acl jsonb
)
RETURNS TABLE(
    disposition text,
    was_changed boolean,
    before_state jsonb,
    current_version bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_now timestamptz;
    v_inserted integer;
BEGIN
    IF p_api_key_id IS NULL
        OR p_user_id IS NULL
        OR p_group_id IS NULL
        OR p_name IS NULL
        OR NOT public.poolai_api_key_text_is_valid(p_name, 100)
        OR p_key_prefix IS NULL
        OR p_key_prefix !~ '^sk-[A-Za-z0-9_-]{10,21}$'
        OR p_secret_hash IS NULL
        OR pg_catalog.octet_length(p_secret_hash) <> 32
        OR p_pepper_version IS NULL
        OR p_pepper_version <= 0
        OR (p_expires_at IS NOT NULL AND NOT pg_catalog.isfinite(p_expires_at))
        OR p_ip_acl IS NULL
        OR NOT public.poolai_api_key_ip_acl_is_canonical(p_ip_acl) THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    IF p_expires_at IS NOT NULL AND p_expires_at <= v_now THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    BEGIN
        INSERT INTO public.api_keys (
            id, user_id, group_id, name, key_prefix, secret_hash,
            pepper_version, status, expires_at, ip_acl, version,
            created_at, updated_at
        ) VALUES (
            p_api_key_id, p_user_id, p_group_id, p_name, p_key_prefix,
            p_secret_hash, p_pepper_version, 'active', p_expires_at,
            p_ip_acl, 1, v_now, v_now
        )
        ON CONFLICT DO NOTHING;
        GET DIAGNOSTICS v_inserted = ROW_COUNT;
    EXCEPTION
        WHEN foreign_key_violation THEN
            RETURN QUERY SELECT
                'conflict'::text, false, NULL::jsonb, NULL::bigint;
            RETURN;
    END;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'conflict'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    RETURN QUERY SELECT 'created'::text, true, NULL::jsonb, 1::bigint;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_api_key_update(
    p_api_key_id uuid,
    p_user_id uuid,
    p_expected_group_id uuid,
    p_expected_version bigint,
    p_expected_effective_status text,
    p_set_name boolean,
    p_name text,
    p_set_status boolean,
    p_status text,
    p_set_expires_at boolean,
    p_expires_at timestamptz,
    p_set_ip_acl boolean,
    p_ip_acl jsonb
)
RETURNS TABLE(
    disposition text,
    was_changed boolean,
    before_state jsonb,
    current_version bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_key record;
    v_name text;
    v_status text;
    v_expires_at timestamptz;
    v_ip_acl jsonb;
    v_now timestamptz;
    v_effective_status text;
    v_changed boolean;
    v_before_state jsonb;
BEGIN
    IF p_api_key_id IS NULL
        OR p_user_id IS NULL
        OR p_expected_group_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_expected_effective_status IS NULL
        OR p_expected_effective_status NOT IN (
            'active', 'disabled', 'expired', 'revoked'
        )
        OR p_set_name IS NULL
        OR p_set_status IS NULL
        OR p_set_expires_at IS NULL
        OR p_set_ip_acl IS NULL
        OR NOT (p_set_name OR p_set_status OR p_set_expires_at OR p_set_ip_acl)
        OR (p_set_name AND (
            p_name IS NULL
            OR NOT public.poolai_api_key_text_is_valid(p_name, 100)
        ))
        OR (NOT p_set_name AND p_name IS NOT NULL)
        OR (p_set_status AND p_status NOT IN ('active', 'disabled'))
        OR (p_set_status AND p_status IS NULL)
        OR (NOT p_set_status AND p_status IS NOT NULL)
        OR (p_set_expires_at AND p_expires_at IS NOT NULL
            AND NOT pg_catalog.isfinite(p_expires_at))
        OR (NOT p_set_expires_at AND p_expires_at IS NOT NULL)
        OR (p_set_ip_acl AND (
            p_ip_acl IS NULL
            OR NOT public.poolai_api_key_ip_acl_is_canonical(p_ip_acl)
        ))
        OR (NOT p_set_ip_acl AND p_ip_acl IS NOT NULL) THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT
        current_key.id,
        current_key.user_id,
        current_key.group_id,
        current_key.name,
        current_key.key_prefix,
        current_key.status,
        current_key.expires_at,
        current_key.ip_acl,
        current_key.last_used_at,
        current_key.revoked_at,
        current_key.revoke_reason,
        current_key.version,
        current_key.created_at,
        current_key.updated_at
    INTO v_key
    FROM public.api_keys AS current_key
    WHERE current_key.id = p_api_key_id
      AND current_key.user_id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- This clock sample must remain after the row-lock wait.
    v_now := pg_catalog.clock_timestamp();
    v_effective_status := CASE
        WHEN v_key.status = 'revoked' THEN 'revoked'
        WHEN v_key.status = 'disabled' THEN 'disabled'
        WHEN v_key.expires_at IS NOT NULL AND v_now >= v_key.expires_at
            THEN 'expired'
        ELSE 'active'
    END;
    v_before_state := pg_catalog.jsonb_build_object(
        'id', v_key.id,
        'user_id', v_key.user_id,
        'group_id', v_key.group_id,
        'name', v_key.name,
        'prefix', v_key.key_prefix,
        'status', v_key.status,
        'effective_status', v_effective_status,
        'expires_at', v_key.expires_at,
        'allowed_cidrs', v_key.ip_acl,
        'last_used_at', v_key.last_used_at,
        'revoked_at', v_key.revoked_at,
        'revoke_reason', v_key.revoke_reason,
        'version', v_key.version,
        'created_at', v_key.created_at,
        'updated_at', v_key.updated_at
    );

    IF v_key.status = 'revoked' THEN
        RETURN QUERY SELECT
            'api_key_revoked'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;
    IF v_key.group_id <> p_expected_group_id THEN
        RETURN QUERY SELECT
            'resource_conflict'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;
    IF v_key.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;
    -- Natural expiration does not advance version. Reject lifecycle drift so
    -- Orchestration must reload the Snapshot and, for restoration, repeat the
    -- point-in-time Subscription gate before retrying.
    IF v_effective_status <> p_expected_effective_status THEN
        RETURN QUERY SELECT
            'resource_conflict'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;

    v_name := CASE WHEN p_set_name THEN p_name ELSE v_key.name END;
    v_status := CASE WHEN p_set_status THEN p_status ELSE v_key.status END;
    v_expires_at := CASE
        WHEN p_set_expires_at THEN p_expires_at
        ELSE v_key.expires_at
    END;
    v_ip_acl := CASE WHEN p_set_ip_acl THEN p_ip_acl ELSE v_key.ip_acl END;

    -- Enabling a disabled Key cannot produce an already-expired active Key.
    -- The required Subscription check occurred before this Identity UoW.
    IF v_key.status = 'disabled'
        AND v_status = 'active'
        AND v_expires_at IS NOT NULL
        AND v_expires_at <= v_now THEN
        RETURN QUERY SELECT
            'resource_conflict'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;

    v_changed := v_name IS DISTINCT FROM v_key.name
        OR v_status IS DISTINCT FROM v_key.status
        OR v_expires_at IS DISTINCT FROM v_key.expires_at
        OR v_ip_acl IS DISTINCT FROM v_key.ip_acl;
    IF NOT v_changed THEN
        RETURN QUERY SELECT
            'updated'::text, false, NULL::jsonb, v_key.version;
        RETURN;
    END IF;

    UPDATE public.api_keys AS target
    SET name = v_name,
        status = v_status,
        expires_at = v_expires_at,
        ip_acl = v_ip_acl,
        version = v_key.version + 1,
        updated_at = v_now
    WHERE target.id = p_api_key_id;

    RETURN QUERY SELECT
        'updated'::text, true, v_before_state, v_key.version + 1;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_api_key_revoke(
    p_api_key_id uuid,
    p_user_id uuid,
    p_expected_version bigint,
    p_reason text
)
RETURNS TABLE(
    disposition text,
    was_changed boolean,
    before_state jsonb,
    current_version bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_key record;
    v_now timestamptz;
    v_before_state jsonb;
BEGIN
    IF p_api_key_id IS NULL
        OR p_user_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_reason IS NULL
        OR NOT public.poolai_api_key_text_is_valid(p_reason, 500) THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT
        current_key.id,
        current_key.user_id,
        current_key.group_id,
        current_key.name,
        current_key.key_prefix,
        current_key.status,
        current_key.expires_at,
        current_key.ip_acl,
        current_key.last_used_at,
        current_key.revoked_at,
        current_key.revoke_reason,
        current_key.version,
        current_key.created_at,
        current_key.updated_at
    INTO v_key
    FROM public.api_keys AS current_key
    WHERE current_key.id = p_api_key_id
      AND current_key.user_id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    v_before_state := pg_catalog.jsonb_build_object(
        'id', v_key.id,
        'user_id', v_key.user_id,
        'group_id', v_key.group_id,
        'name', v_key.name,
        'prefix', v_key.key_prefix,
        'status', v_key.status,
        'effective_status', CASE
            WHEN v_key.status = 'revoked' THEN 'revoked'
            WHEN v_key.status = 'disabled' THEN 'disabled'
            WHEN v_key.expires_at IS NOT NULL AND v_now >= v_key.expires_at
                THEN 'expired'
            ELSE 'active'
        END,
        'expires_at', v_key.expires_at,
        'allowed_cidrs', v_key.ip_acl,
        'last_used_at', v_key.last_used_at,
        'revoked_at', v_key.revoked_at,
        'revoke_reason', v_key.revoke_reason,
        'version', v_key.version,
        'created_at', v_key.created_at,
        'updated_at', v_key.updated_at
    );

    IF v_key.status = 'revoked' THEN
        RETURN QUERY SELECT
            'api_key_revoked'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;
    IF v_key.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_key.version;
        RETURN;
    END IF;

    UPDATE public.api_keys AS target
    SET status = 'revoked',
        revoked_at = v_now,
        revoke_reason = p_reason,
        version = v_key.version + 1,
        updated_at = v_now
    WHERE target.id = p_api_key_id;

    RETURN QUERY SELECT
        'revoked'::text, true, v_before_state, v_key.version + 1;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_api_key_rotate(
    p_api_key_id uuid,
    p_user_id uuid,
    p_expected_group_id uuid,
    p_expected_version bigint,
    p_new_api_key_id uuid,
    p_new_key_prefix text,
    p_new_secret_hash bytea,
    p_new_pepper_version smallint,
    p_reason text
)
RETURNS TABLE(
    disposition text,
    was_changed boolean,
    before_state jsonb,
    old_current_version bigint,
    new_api_key_id uuid,
    new_current_version bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_key record;
    v_now timestamptz;
    v_before_state jsonb;
    v_inserted integer;
BEGIN
    IF p_api_key_id IS NULL
        OR p_user_id IS NULL
        OR p_expected_group_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_new_api_key_id IS NULL
        OR p_new_api_key_id = p_api_key_id
        OR p_new_key_prefix IS NULL
        OR p_new_key_prefix !~ '^sk-[A-Za-z0-9_-]{10,21}$'
        OR p_new_secret_hash IS NULL
        OR pg_catalog.octet_length(p_new_secret_hash) <> 32
        OR p_new_pepper_version IS NULL
        OR p_new_pepper_version <= 0
        OR p_reason IS NULL
        OR NOT public.poolai_api_key_text_is_valid(p_reason, 500) THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, NULL::jsonb,
            NULL::bigint, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;

    SELECT
        current_key.id,
        current_key.user_id,
        current_key.group_id,
        current_key.name,
        current_key.key_prefix,
        current_key.status,
        current_key.expires_at,
        current_key.ip_acl,
        current_key.last_used_at,
        current_key.revoked_at,
        current_key.revoke_reason,
        current_key.version,
        current_key.created_at,
        current_key.updated_at
    INTO v_key
    FROM public.api_keys AS current_key
    WHERE current_key.id = p_api_key_id
      AND current_key.user_id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb,
            NULL::bigint, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;

    -- Expiration is evaluated only after any lock wait has completed.
    v_now := pg_catalog.clock_timestamp();
    v_before_state := pg_catalog.jsonb_build_object(
        'id', v_key.id,
        'user_id', v_key.user_id,
        'group_id', v_key.group_id,
        'name', v_key.name,
        'prefix', v_key.key_prefix,
        'status', v_key.status,
        'effective_status', CASE
            WHEN v_key.status = 'revoked' THEN 'revoked'
            WHEN v_key.status = 'disabled' THEN 'disabled'
            WHEN v_key.expires_at IS NOT NULL AND v_now >= v_key.expires_at
                THEN 'expired'
            ELSE 'active'
        END,
        'expires_at', v_key.expires_at,
        'allowed_cidrs', v_key.ip_acl,
        'last_used_at', v_key.last_used_at,
        'revoked_at', v_key.revoked_at,
        'revoke_reason', v_key.revoke_reason,
        'version', v_key.version,
        'created_at', v_key.created_at,
        'updated_at', v_key.updated_at
    );

    IF v_key.status = 'revoked' THEN
        RETURN QUERY SELECT
            'api_key_revoked'::text, false, v_before_state,
            v_key.version, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;
    IF v_key.group_id <> p_expected_group_id THEN
        RETURN QUERY SELECT
            'resource_conflict'::text, false, v_before_state,
            v_key.version, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;
    IF v_key.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state,
            v_key.version, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;
    IF v_key.expires_at IS NOT NULL AND v_key.expires_at <= v_now THEN
        RETURN QUERY SELECT
            'resource_conflict'::text, false, v_before_state,
            v_key.version, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;

    INSERT INTO public.api_keys (
        id, user_id, group_id, name, key_prefix, secret_hash,
        pepper_version, status, expires_at, ip_acl, version,
        created_at, updated_at
    ) VALUES (
        p_new_api_key_id, v_key.user_id, v_key.group_id, v_key.name,
        p_new_key_prefix, p_new_secret_hash, p_new_pepper_version,
        'active', v_key.expires_at, v_key.ip_acl, 1, v_now, v_now
    )
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'conflict'::text, false, v_before_state,
            v_key.version, NULL::uuid, NULL::bigint;
        RETURN;
    END IF;

    UPDATE public.api_keys AS target
    SET status = 'revoked',
        revoked_at = v_now,
        revoke_reason = p_reason,
        version = v_key.version + 1,
        updated_at = v_now
    WHERE target.id = p_api_key_id;

    RETURN QUERY SELECT
        'rotated'::text, true, v_before_state,
        v_key.version + 1, p_new_api_key_id, 1::bigint;
END;
$function$;

REVOKE ALL ON FUNCTION public.poolai_api_key_text_is_valid(text, integer)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_api_key_create(
    uuid, uuid, uuid, text, text, bytea, smallint, timestamptz, jsonb
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_api_key_update(
    uuid, uuid, uuid, bigint, text, boolean, text, boolean, text,
    boolean, timestamptz, boolean, jsonb
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_api_key_revoke(uuid, uuid, bigint, text)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_api_key_rotate(
    uuid, uuid, uuid, bigint, uuid, text, bytea, smallint, text
) FROM PUBLIC, poolai_api, poolai_worker;

GRANT EXECUTE ON FUNCTION public.poolai_api_key_create(
    uuid, uuid, uuid, text, text, bytea, smallint, timestamptz, jsonb
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_api_key_update(
    uuid, uuid, uuid, bigint, text, boolean, text, boolean, text,
    boolean, timestamptz, boolean, jsonb
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_api_key_revoke(uuid, uuid, bigint, text)
    TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_api_key_rotate(
    uuid, uuid, uuid, bigint, uuid, text, bytea, smallint, text
) TO poolai_api;

RESET ROLE;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

-- Fail closed if validator ownership/ACL, the replacement entry-point ABI, or
-- either table constraint drifts before this migration commits.
DO $permission_audit$
DECLARE
    v_api_role_oid oid;
    v_function_oid oid;
    v_function_signature text;
BEGIN
    SELECT role.oid
    INTO v_api_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';
    IF v_api_role_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_api_role_missing';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_runtime_schema_create_forbidden';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_constraint AS constraint_definition
        WHERE constraint_definition.conrelid = 'public.api_keys'::regclass
          AND constraint_definition.conname IN (
              'ck_api_keys_m1_e5_name_length',
              'ck_api_keys_m1_e5_reason_length'
          )
    ) OR (
        SELECT count(*)
        FROM pg_catalog.pg_constraint AS constraint_definition
        WHERE constraint_definition.conrelid = 'public.api_keys'::regclass
          AND constraint_definition.contype = 'c'
          AND (
              (
                  constraint_definition.conname =
                      'ck_api_keys_m1_e5_name_text'
                  AND pg_catalog.strpos(
                      pg_catalog.pg_get_expr(
                          constraint_definition.conbin,
                          constraint_definition.conrelid),
                      'poolai_api_key_text_is_valid(name, 100)') > 0
              )
              OR (
                  constraint_definition.conname =
                      'ck_api_keys_m1_e5_reason_text'
                  AND pg_catalog.strpos(
                      pg_catalog.pg_get_expr(
                          constraint_definition.conbin,
                          constraint_definition.conrelid),
                      'poolai_api_key_text_is_valid(revoke_reason, 500)') > 0
              )
          )
    ) <> 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_constraint_boundary_missing';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = 'poolai_api_key_text_is_valid'
    ) <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_validator_overload_forbidden';
    END IF;

    v_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_api_key_text_is_valid(text,integer)');
    IF v_function_oid IS NULL OR NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_function_oid
          AND NOT procedure.prosecdef
          AND procedure.provolatile = 'i'
          AND procedure.proisstrict
          AND procedure.proparallel = 's'
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND procedure.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
          AND NOT pg_catalog.has_function_privilege(
              'poolai_api', procedure.oid, 'EXECUTE')
          AND NOT pg_catalog.has_function_privilege(
              'poolai_worker', procedure.oid, 'EXECUTE')
          AND NOT EXISTS (
              SELECT 1
              FROM pg_catalog.aclexplode(COALESCE(
                  procedure.proacl,
                  pg_catalog.acldefault('f', procedure.proowner))) AS privilege
              WHERE privilege.privilege_type = 'EXECUTE'
                AND (
                    privilege.grantor <> procedure.proowner
                    OR privilege.grantee <> procedure.proowner
                )
          )
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_validator_boundary_missing';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = ANY (ARRAY[
              'poolai_api_key_create',
              'poolai_api_key_update',
              'poolai_api_key_revoke',
              'poolai_api_key_rotate'
          ])
    ) <> 4 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_text_entry_point_overload_forbidden';
    END IF;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_api_key_create(uuid,uuid,uuid,text,text,bytea,smallint,timestamptz,jsonb)',
        'public.poolai_api_key_update(uuid,uuid,uuid,bigint,text,boolean,text,boolean,text,boolean,timestamptz,boolean,jsonb)',
        'public.poolai_api_key_revoke(uuid,uuid,bigint,text)',
        'public.poolai_api_key_rotate(uuid,uuid,uuid,bigint,uuid,text,bytea,smallint,text)'
    ]
    LOOP
        v_function_oid := pg_catalog.to_regprocedure(v_function_signature);
        IF v_function_oid IS NULL OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = procedure.proowner
            WHERE procedure.oid = v_function_oid
              AND procedure.prosecdef
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND procedure.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND pg_catalog.has_function_privilege(
                  'poolai_api', procedure.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_worker', procedure.oid, 'EXECUTE')
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      procedure.proacl,
                      pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND privilege.grantee = v_api_role_oid
                    AND privilege.grantor = procedure.proowner
                    AND NOT privilege.is_grantable
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      procedure.proacl,
                      pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND (
                        privilege.grantor <> procedure.proowner
                        OR privilege.grantee NOT IN (
                            procedure.proowner, v_api_role_oid
                        )
                        OR (
                            privilege.grantee = v_api_role_oid
                            AND privilege.is_grantable
                        )
                    )
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m1_e5_text_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;
END;
$permission_audit$;
