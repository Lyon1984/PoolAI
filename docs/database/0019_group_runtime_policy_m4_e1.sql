-- PoolAI Release 1 M4-E1 canonical per-Group runtime policy increment.
--
-- GroupQuota remains the sole owner of Group lifecycle/version and now also
-- owns the exact requests_per_minute policy consumed by the Gateway's
-- fail-closed Redis fixed-window admission. Existing 0007 function ABIs remain
-- available; v2 entry points add atomic RPM creation/update without widening
-- direct table permissions.

-- Schema 18 did not govern any non-empty runtime_policy representation. Even
-- a value shaped like the new schema is therefore unknown old data and must be
-- reviewed explicitly rather than adopted by coincidence. Run this preflight
-- before changing the column default or any Group row. The ALTER below already
-- requires ACCESS EXCLUSIVE; acquire it first so a concurrent legacy writer
-- cannot cross the preflight/DDL boundary.
LOCK TABLE public.groups IN ACCESS EXCLUSIVE MODE;

DO $runtime_policy_preflight$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM public.groups AS current_group
        WHERE current_group.runtime_policy <> '{}'::jsonb
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'poolai_m4_e1_existing_runtime_policy_not_empty';
    END IF;
END;
$runtime_policy_preflight$;

ALTER TABLE public.groups
    ALTER COLUMN runtime_policy
    SET DEFAULT '{"schema_version":1,"requests_per_minute":6000}'::jsonb;

-- The preflight proves every schema-18 row is the exact legacy default. Each
-- backfilled policy is a real Group representation/ETag change and therefore
-- advances version and the same row-lock-after database observation once.
UPDATE public.groups AS target
SET runtime_policy = '{"schema_version":1,"requests_per_minute":6000}'::jsonb,
    version = target.version + 1,
    updated_at = clock_timestamp()
WHERE target.runtime_policy = '{}'::jsonb;

ALTER TABLE public.groups
    ADD CONSTRAINT ck_groups_runtime_policy_m4_e1 CHECK (
        CASE
            WHEN jsonb_typeof(runtime_policy) <> 'object' THEN false
            WHEN NOT (runtime_policy ? 'schema_version') THEN false
            WHEN NOT (runtime_policy ? 'requests_per_minute') THEN false
            WHEN runtime_policy - ARRAY['schema_version', 'requests_per_minute']
                <> '{}'::jsonb THEN false
            WHEN jsonb_typeof(runtime_policy -> 'schema_version') <> 'number'
                THEN false
            WHEN jsonb_typeof(runtime_policy -> 'requests_per_minute') <> 'number'
                THEN false
            ELSE
                (runtime_policy ->> 'schema_version')::numeric = 1
                AND (runtime_policy ->> 'requests_per_minute')::numeric
                    = trunc((runtime_policy ->> 'requests_per_minute')::numeric)
                AND (runtime_policy ->> 'requests_per_minute')::numeric
                    BETWEEN 1 AND 1000000
        END
    ) NOT VALID;

ALTER TABLE public.groups
    VALIDATE CONSTRAINT ck_groups_runtime_policy_m4_e1;

CREATE OR REPLACE FUNCTION public.poolai_group_create_v2(
    p_group_id uuid,
    p_name text,
    p_description text,
    p_requests_per_minute integer,
    p_period_id uuid,
    p_total_tokens numeric,
    p_actor_user_id uuid,
    p_quota_event_id uuid,
    p_outbox_id uuid,
    p_quota_idempotency_key text,
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
    v_now timestamptz;
    v_inserted integer;
    v_requests_per_minute integer;
BEGIN
    IF p_group_id IS NULL
        OR p_period_id IS NULL
        OR p_actor_user_id IS NULL
        OR p_quota_event_id IS NULL
        OR p_outbox_id IS NULL
        OR p_name IS NULL
        OR btrim(p_name) = ''
        OR char_length(p_name) > 100
        OR (p_description IS NOT NULL AND char_length(p_description) > 1000)
        OR (p_requests_per_minute IS NOT NULL
            AND p_requests_per_minute NOT BETWEEN 1 AND 1000000)
        OR p_reason IS NULL
        OR btrim(p_reason) = ''
        OR char_length(p_reason) > 500
        OR p_quota_idempotency_key IS NULL
        OR btrim(p_quota_idempotency_key) = '' THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_requests_per_minute := coalesce(p_requests_per_minute, 6000);
    v_now := clock_timestamp();
    INSERT INTO public.groups (
        id, name, description, status, runtime_policy,
        version, created_at, updated_at
    ) VALUES (
        p_group_id, p_name, p_description, 'disabled',
        jsonb_build_object(
            'schema_version', 1,
            'requests_per_minute', v_requests_per_minute
        ),
        1, v_now, v_now
    )
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT 'conflict'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- Preserve the 0007 Group -> quota initialization order. The surrounding
    -- application Unit of Work rolls the Group insert back on any quota error.
    PERFORM public.poolai_quota_initialize(
        p_group_id,
        p_period_id,
        p_total_tokens,
        p_quota_event_id,
        p_outbox_id,
        p_actor_user_id,
        p_quota_idempotency_key,
        p_reason
    );

    RETURN QUERY SELECT 'created'::text, true, NULL::jsonb, 1::bigint;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_group_update_v2(
    p_group_id uuid,
    p_expected_version bigint,
    p_set_name boolean,
    p_name text,
    p_set_description boolean,
    p_description text,
    p_set_requests_per_minute boolean,
    p_requests_per_minute integer,
    p_status text,
    p_reason text,
    p_supply_readiness_token text,
    p_supply_observed_at timestamptz
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
    v_group record;
    v_name text;
    v_description text;
    v_requests_per_minute integer;
    v_runtime_policy jsonb;
    v_status text;
    v_supply_token text;
    v_supply_observed_at timestamptz;
    v_deleted_at timestamptz;
    v_now timestamptz;
    v_changed boolean;
    v_before_state jsonb;
BEGIN
    IF p_group_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_set_name IS NULL
        OR p_set_description IS NULL
        OR p_set_requests_per_minute IS NULL
        OR (NOT p_set_name AND p_name IS NOT NULL)
        OR (p_set_name AND (
            p_name IS NULL OR btrim(p_name) = '' OR char_length(p_name) > 100
        ))
        OR (NOT p_set_description AND p_description IS NOT NULL)
        OR (p_set_description AND p_description IS NOT NULL
            AND char_length(p_description) > 1000)
        OR (NOT p_set_requests_per_minute AND p_requests_per_minute IS NOT NULL)
        OR (p_set_requests_per_minute AND (
            p_requests_per_minute IS NULL
            OR p_requests_per_minute NOT BETWEEN 1 AND 1000000
        ))
        OR (p_status IS NOT NULL AND p_status NOT IN ('active', 'disabled', 'archived'))
        OR ((p_status IS NOT NULL OR p_set_requests_per_minute) AND (
            p_reason IS NULL OR btrim(p_reason) = '' OR char_length(p_reason) > 500
        ))
        OR ((p_supply_readiness_token IS NULL)
            <> (p_supply_observed_at IS NULL))
        OR (p_supply_observed_at IS NOT NULL
            AND NOT isfinite(p_supply_observed_at)) THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- Keep the exact 0007 Quota -> Group archive lock order.
    IF p_status = 'archived' THEN
        PERFORM quota.group_id
        FROM public.group_token_quotas AS quota
        WHERE quota.group_id = p_group_id
        FOR UPDATE;
    END IF;

    SELECT current_group.id,
           current_group.name,
           current_group.description,
           current_group.runtime_policy,
           current_group.status,
           current_group.activation_supply_readiness_token,
           current_group.activation_supply_observed_at,
           current_group.version,
           current_group.created_at,
           current_group.updated_at,
           current_group.deleted_at
    INTO v_group
    FROM public.groups AS current_group
    WHERE current_group.id = p_group_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    v_requests_per_minute :=
        (v_group.runtime_policy ->> 'requests_per_minute')::integer;
    v_before_state := jsonb_build_object(
        'id', v_group.id,
        'name', v_group.name,
        'description', v_group.description,
        'requests_per_minute', v_requests_per_minute,
        'status', v_group.status,
        'version', v_group.version,
        'created_at', v_group.created_at,
        'updated_at', v_group.updated_at
    );
    IF v_group.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_group.version;
        RETURN;
    END IF;
    IF v_group.status = 'archived' THEN
        RETURN QUERY SELECT
            'invalid_transition'::text, false, v_before_state, v_group.version;
        RETURN;
    END IF;

    v_status := coalesce(p_status, v_group.status);
    IF (v_group.status = 'disabled' AND v_status NOT IN ('disabled', 'active', 'archived'))
        OR (v_group.status = 'active' AND v_status NOT IN ('active', 'disabled')) THEN
        RETURN QUERY SELECT
            'invalid_transition'::text, false, v_before_state, v_group.version;
        RETURN;
    END IF;

    IF v_group.status = 'disabled' AND v_status = 'active' THEN
        IF p_supply_readiness_token IS NULL
            OR p_supply_observed_at IS NULL THEN
            RETURN QUERY SELECT
                'validation_failed'::text, false, v_before_state, v_group.version;
            RETURN;
        END IF;
        v_supply_token := p_supply_readiness_token;
        v_supply_observed_at := p_supply_observed_at;
    ELSE
        IF p_supply_readiness_token IS NOT NULL OR p_supply_observed_at IS NOT NULL THEN
            RETURN QUERY SELECT
                'invalid_transition'::text, false, v_before_state, v_group.version;
            RETURN;
        END IF;
        v_supply_token := v_group.activation_supply_readiness_token;
        v_supply_observed_at := v_group.activation_supply_observed_at;
    END IF;

    IF v_status = 'archived' THEN
        IF EXISTS (
            SELECT 1
            FROM public.group_token_reservations AS reservation
            WHERE reservation.group_id = p_group_id
              AND reservation.status = 'pending'
        ) OR EXISTS (
            SELECT 1
            FROM public.subscriptions AS subscription
            WHERE subscription.group_id = p_group_id
              AND subscription.status = 'active'
              AND subscription.expires_at > v_now
        ) THEN
            RETURN QUERY SELECT
                'archive_blocked'::text, false, v_before_state, v_group.version;
            RETURN;
        END IF;
    END IF;

    v_name := CASE WHEN p_set_name THEN p_name ELSE v_group.name END;
    v_description := CASE
        WHEN p_set_description THEN p_description
        ELSE v_group.description
    END;
    v_requests_per_minute := CASE
        WHEN p_set_requests_per_minute THEN p_requests_per_minute
        ELSE v_requests_per_minute
    END;
    v_runtime_policy := jsonb_build_object(
        'schema_version', 1,
        'requests_per_minute', v_requests_per_minute
    );
    v_deleted_at := CASE WHEN v_status = 'archived' THEN v_now ELSE NULL END;
    v_changed := v_name IS DISTINCT FROM v_group.name
        OR v_description IS DISTINCT FROM v_group.description
        OR v_runtime_policy IS DISTINCT FROM v_group.runtime_policy
        OR v_status IS DISTINCT FROM v_group.status
        OR v_supply_token IS DISTINCT FROM v_group.activation_supply_readiness_token
        OR v_supply_observed_at IS DISTINCT FROM v_group.activation_supply_observed_at
        OR v_deleted_at IS DISTINCT FROM v_group.deleted_at;

    IF NOT v_changed THEN
        RETURN QUERY SELECT 'updated'::text, false, v_before_state, v_group.version;
        RETURN;
    END IF;

    UPDATE public.groups AS target
    SET name = v_name,
        description = v_description,
        runtime_policy = v_runtime_policy,
        status = v_status,
        activation_supply_readiness_token = v_supply_token,
        activation_supply_observed_at = v_supply_observed_at,
        version = v_group.version + 1,
        updated_at = v_now,
        deleted_at = v_deleted_at
    WHERE target.id = p_group_id;

    RETURN QUERY SELECT 'updated'::text, true, v_before_state, v_group.version + 1;
END;
$function$;

-- Extend only the NOLOGIN function owner's existing Group column allowlist.
-- The API and Worker retain no direct Group write permission.
GRANT SELECT (runtime_policy) ON public.groups TO poolai_runtime_owner;
GRANT INSERT (runtime_policy) ON public.groups TO poolai_runtime_owner;
GRANT UPDATE (runtime_policy) ON public.groups TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_create_v2(
    uuid, text, text, integer, uuid, numeric, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_update_v2(
    uuid, bigint, boolean, text, boolean, text, boolean, integer,
    text, text, text, timestamptz
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_group_create_v2(
    uuid, text, text, integer, uuid, numeric, uuid, uuid, uuid, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_group_update_v2(
    uuid, bigint, boolean, text, boolean, text, boolean, integer,
    text, text, text, timestamptz
) FROM PUBLIC, poolai_api, poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_group_create_v2(
    uuid, text, text, integer, uuid, numeric, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_group_update_v2(
    uuid, bigint, boolean, text, boolean, text, boolean, integer,
    text, text, text, timestamptz
) TO poolai_api;
RESET ROLE;

DO $permission_audit$
DECLARE
    v_function_signature text;
    v_function_oid oid;
BEGIN
    IF pg_catalog.has_table_privilege('poolai_api', 'public.groups', 'INSERT')
        OR pg_catalog.has_table_privilege('poolai_api', 'public.groups', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.groups', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.groups', 'UPDATE')
        OR pg_catalog.has_table_privilege('poolai_worker', 'public.groups', 'INSERT')
        OR pg_catalog.has_table_privilege('poolai_worker', 'public.groups', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_worker', 'public.groups', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_worker', 'public.groups', 'UPDATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_group_direct_table_write_forbidden';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = ANY (ARRAY[
              'poolai_group_create_v2',
              'poolai_group_update_v2'
          ])
    ) <> 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_group_entry_point_overload_forbidden';
    END IF;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_group_create_v2(uuid,text,text,integer,uuid,numeric,uuid,uuid,uuid,text,text)',
        'public.poolai_group_update_v2(uuid,bigint,boolean,text,boolean,text,boolean,integer,text,text,text,timestamptz)'
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
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      procedure.proacl,
                      pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND privilege.grantee = 0
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m4_e1_group_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    IF NOT pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.groups', 'runtime_policy', 'SELECT')
        OR NOT pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.groups', 'runtime_policy', 'INSERT')
        OR NOT pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.groups', 'runtime_policy', 'UPDATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_group_owner_boundary_missing';
    END IF;
END;
$permission_audit$;
