-- PoolAI Release 1 M1-E5 API Key persistence and lifecycle boundary.
--
-- API Key authorization remains an application-orchestrated point-in-time
-- Subscription check. These Identity-owned entry points deliberately read and
-- lock only public.api_keys; they do not extend the ADR 0006 cross-context SQL
-- registry. Plaintext credentials never enter PostgreSQL.

REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public.api_keys FROM poolai_api;

-- Persist only the canonical CIDR representation produced by the application.
-- Bytewise comparison is the contract's ordinal order because canonical CIDRs
-- contain ASCII only. A strictly increasing sequence also rejects duplicates.
CREATE FUNCTION public.poolai_api_key_ip_acl_is_canonical(
    p_ip_acl jsonb
)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_element jsonb;
    v_entry text;
    v_network cidr;
    v_previous text;
BEGIN
    IF pg_catalog.jsonb_typeof(p_ip_acl) <> 'array' THEN
        RETURN false;
    END IF;
    IF pg_catalog.jsonb_array_length(p_ip_acl) > 50 THEN
        RETURN false;
    END IF;

    FOR v_element IN
        SELECT item.value
        FROM pg_catalog.jsonb_array_elements(p_ip_acl) AS item(value)
    LOOP
        IF pg_catalog.jsonb_typeof(v_element) <> 'string' THEN
            RETURN false;
        END IF;

        v_entry := v_element #>> '{}';
        IF v_entry IS NULL
            OR pg_catalog.char_length(v_entry) > 64
            OR pg_catalog.strpos(v_entry, '/') = 0
            OR pg_catalog.strpos(v_entry, '%') > 0 THEN
            RETURN false;
        END IF;

        BEGIN
            v_network := v_entry::cidr;
        EXCEPTION
            WHEN invalid_text_representation OR numeric_value_out_of_range THEN
                RETURN false;
        END;

        IF v_entry <> v_network::text
            OR (
                pg_catalog.family(v_network) = 6
                AND v_network <<= '::ffff:0:0/96'::cidr
            ) THEN
            RETURN false;
        END IF;

        IF v_previous IS NOT NULL
            AND pg_catalog.convert_to(v_previous, 'UTF8')
                >= pg_catalog.convert_to(v_entry, 'UTF8') THEN
            RETURN false;
        END IF;
        v_previous := v_entry;
    END LOOP;

    RETURN true;
END;
$function$;

ALTER TABLE public.api_keys
    DROP CONSTRAINT ck_api_keys_prefix,
    DROP CONSTRAINT ck_api_keys_ip_acl,
    ADD CONSTRAINT ck_api_keys_prefix CHECK (
        key_prefix ~ '^sk-[A-Za-z0-9_-]{10,21}$'
    ),
    ADD CONSTRAINT ck_api_keys_m1_e5_name_length CHECK (
        pg_catalog.char_length(name) <= 100
    ),
    ADD CONSTRAINT ck_api_keys_m1_e5_time_finite CHECK (
        (expires_at IS NULL OR pg_catalog.isfinite(expires_at))
        AND (last_used_at IS NULL OR pg_catalog.isfinite(last_used_at))
        AND (revoked_at IS NULL OR pg_catalog.isfinite(revoked_at))
        AND pg_catalog.isfinite(created_at)
        AND pg_catalog.isfinite(updated_at)
    ),
    ADD CONSTRAINT ck_api_keys_m1_e5_reason_length CHECK (
        revoke_reason IS NULL OR pg_catalog.char_length(revoke_reason) <= 500
    ),
    ADD CONSTRAINT ck_api_keys_ip_acl CHECK (
        public.poolai_api_key_ip_acl_is_canonical(ip_acl)
    );

-- Match API Key keyset lists and cover the Group foreign-key lookup without a
-- scan whose leading column is user_id.
CREATE INDEX ix_api_keys_user_created_id
    ON public.api_keys(user_id, created_at DESC, id DESC);
CREATE INDEX ix_api_keys_group_id
    ON public.api_keys(group_id, id);

CREATE FUNCTION public.poolai_api_key_create(
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
        OR pg_catalog.btrim(p_name) = ''
        OR pg_catalog.char_length(p_name) > 100
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

CREATE FUNCTION public.poolai_api_key_update(
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
            OR pg_catalog.btrim(p_name) = ''
            OR pg_catalog.char_length(p_name) > 100
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

CREATE FUNCTION public.poolai_api_key_revoke(
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
        OR pg_catalog.btrim(p_reason) = ''
        OR pg_catalog.char_length(p_reason) > 500 THEN
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

CREATE FUNCTION public.poolai_api_key_rotate(
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
        OR pg_catalog.btrim(p_reason) = ''
        OR pg_catalog.char_length(p_reason) > 500 THEN
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

-- The NOLOGIN function owner receives only the columns needed by the four
-- entry points. Credential digests and pepper versions remain write-only.
GRANT SELECT (
    id, user_id, group_id, name, key_prefix, status, expires_at, ip_acl,
    last_used_at, revoked_at, revoke_reason, version, created_at, updated_at
) ON public.api_keys TO poolai_runtime_owner;
GRANT INSERT (
    id, user_id, group_id, name, key_prefix, secret_hash, pepper_version,
    status, expires_at, ip_acl, version, created_at, updated_at
) ON public.api_keys TO poolai_runtime_owner;
GRANT UPDATE (
    name, status, expires_at, ip_acl, revoked_at, revoke_reason,
    version, updated_at
) ON public.api_keys TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_ip_acl_is_canonical(jsonb)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_create(
    uuid, uuid, uuid, text, text, bytea, smallint, timestamptz, jsonb
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_update(
    uuid, uuid, uuid, bigint, text, boolean, text, boolean, text,
    boolean, timestamptz, boolean, jsonb
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_revoke(uuid, uuid, bigint, text)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_api_key_rotate(
    uuid, uuid, uuid, bigint, uuid, text, bytea, smallint, text
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_api_key_ip_acl_is_canonical(jsonb)
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

-- Fail closed if a later grant re-opens table writes, exposes credential
-- material to the NOLOGIN owner, or changes an entry-point boundary.
DO $permission_audit$
DECLARE
    v_function_signature text;
    v_function_oid oid;
    v_api_role_oid oid;
    v_select_columns text[];
    v_insert_columns text[];
    v_update_columns text[];
BEGIN
    SELECT role.oid
    INTO v_api_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';
    IF v_api_role_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_api_role_missing';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_runtime_schema_create_forbidden';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_api', 'public.api_keys', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.api_keys', 'UPDATE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.api_keys', 'DELETE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.api_keys', 'TRUNCATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.api_keys', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.api_keys', 'UPDATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_direct_api_key_write_forbidden';
    END IF;

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_select_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.api_keys'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.api_keys',
          attribute.attname,
          'SELECT');

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_insert_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.api_keys'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.api_keys',
          attribute.attname,
          'INSERT');

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_update_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.api_keys'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.api_keys',
          attribute.attname,
          'UPDATE');

    IF v_select_columns IS DISTINCT FROM ARRAY[
            'id', 'user_id', 'group_id', 'name', 'key_prefix', 'status',
            'expires_at', 'ip_acl', 'last_used_at', 'revoked_at',
            'revoke_reason', 'version', 'created_at', 'updated_at'
        ]::text[]
        OR v_insert_columns IS DISTINCT FROM ARRAY[
            'id', 'user_id', 'group_id', 'name', 'key_prefix', 'secret_hash',
            'pepper_version', 'status', 'expires_at', 'ip_acl', 'version',
            'created_at', 'updated_at'
        ]::text[]
        OR v_update_columns IS DISTINCT FROM ARRAY[
            'id', 'name', 'status', 'expires_at', 'ip_acl', 'revoked_at',
            'revoke_reason', 'version', 'updated_at'
        ]::text[]
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'SELECT')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'UPDATE')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'DELETE')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'TRUNCATE')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'REFERENCES')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'TRIGGER')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'MAINTAIN')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.api_keys',
            'SELECT WITH GRANT OPTION')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.api_keys',
            'INSERT WITH GRANT OPTION')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.api_keys',
            'UPDATE WITH GRANT OPTION') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_runtime_owner_column_boundary_missing';
    END IF;

    IF pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'secret_hash', 'SELECT')
        OR pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.api_keys', 'pepper_version', 'SELECT')
        OR pg_catalog.has_column_privilege(
            'poolai_worker', 'public.api_keys', 'secret_hash', 'SELECT')
        OR pg_catalog.has_column_privilege(
            'poolai_worker', 'public.api_keys', 'pepper_version', 'SELECT') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e5_credential_read_boundary_missing';
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
            MESSAGE = 'poolai_m1_e5_entry_point_overload_forbidden';
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
                MESSAGE = 'poolai_m1_e5_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    v_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_api_key_ip_acl_is_canonical(jsonb)');
    IF v_function_oid IS NULL OR NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_function_oid
          AND NOT procedure.prosecdef
          AND procedure.provolatile = 'i'
          AND procedure.proisstrict
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
            MESSAGE = 'poolai_m1_e5_cidr_validator_boundary_missing';
    END IF;
END;
$permission_audit$;
