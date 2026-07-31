-- PoolAI Release 1 M3-E1 Group quota period management increment.
--
-- Migration 0007 deliberately revoked the API role's direct access to the
-- original reset/adjust functions. These two wrappers restore only the M3-E1
-- control-plane operations while preserving the shared Quota -> Group ->
-- current Period lock order and rejecting new writes after a Group is archived.
--
-- The already-approved 0002 functions remain the sole owners of quota ledger,
-- event, and outbox mutation. These wrappers add no second event path.

CREATE FUNCTION public.poolai_group_quota_adjust_total(
    p_group_id uuid,
    p_new_total_tokens numeric,
    p_expected_quota_version bigint,
    p_actor_user_id uuid,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_period_id uuid,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric,
    result_quota_version bigint,
    result_before_state jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_existing_event boolean;
    v_quota public.group_token_quotas%ROWTYPE;
    v_group_status text;
    v_group_updated_at timestamptz;
    v_period public.group_quota_periods%ROWTYPE;
    v_before_state jsonb;
BEGIN
    -- Invalid requests are delegated unchanged to the canonical function so it
    -- remains the single validator and preserves its existing error precedence.
    IF p_group_id IS NOT NULL
        AND p_new_total_tokens IS NOT NULL
        AND p_expected_quota_version IS NOT NULL
        AND p_actor_user_id IS NOT NULL
        AND p_event_id IS NOT NULL
        AND p_outbox_id IS NOT NULL
        AND p_idempotency_key IS NOT NULL
        AND btrim(p_idempotency_key) <> ''
        AND p_new_total_tokens BETWEEN 1 AND 9007199254740991
        AND p_new_total_tokens = trunc(p_new_total_tokens)
        AND p_reason IS NOT NULL
        AND btrim(p_reason) <> '' THEN
        -- This is the fixed serialization root shared with archive.
        SELECT quota.* INTO v_quota
        FROM public.group_token_quotas AS quota
        WHERE quota.group_id = p_group_id
        FOR UPDATE;

        IF FOUND THEN
            SELECT EXISTS (
                SELECT 1
                FROM public.group_quota_events AS quota_event
                WHERE quota_event.idempotency_key = p_idempotency_key
            )
            INTO v_existing_event;

            -- A committed event must be replayed (or rejected as key reuse)
            -- before current Group lifecycle or quota-version checks. Both
            -- paths take the Group fence only after the quota row; only a new
            -- key rejects the archived lifecycle state.
            IF NOT v_existing_event THEN
                SELECT current_group.status,
                       current_group.updated_at
                INTO v_group_status,
                     v_group_updated_at
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                  AND current_group.status <> 'archived'
                FOR SHARE;

                IF NOT FOUND THEN
                    PERFORM public.poolai_business_error('group_not_found_or_archived');
                END IF;
            ELSE
                SELECT current_group.status,
                       current_group.updated_at
                INTO v_group_status,
                     v_group_updated_at
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                FOR SHARE;
            END IF;

            SELECT current_period.* INTO v_period
            FROM public.group_quota_periods AS current_period
            WHERE current_period.id = v_quota.current_period_id
              AND current_period.group_id = p_group_id
            FOR UPDATE;

            IF FOUND THEN
                v_before_state := pg_catalog.jsonb_build_object(
                    'group_id', v_quota.group_id,
                    'period_id', v_period.id,
                    'status', CASE
                        WHEN v_group_status <> 'active' OR NOT v_quota.enabled
                            THEN 'disabled'
                        WHEN v_period.consumed_tokens >= v_period.total_tokens
                            THEN 'exhausted'
                        ELSE 'active'
                    END,
                    'total_tokens', v_period.total_tokens::text,
                    'consumed_tokens', v_period.consumed_tokens::text,
                    'reserved_tokens', v_period.reserved_tokens::text,
                    'remaining_tokens', public.poolai_quota_remaining(
                        v_period.total_tokens,
                        v_period.consumed_tokens,
                        v_period.reserved_tokens
                    )::text,
                    'overage_tokens', greatest(
                        v_period.consumed_tokens - v_period.total_tokens,
                        0::numeric
                    )::text,
                    'period_started_at', v_period.opened_at,
                    'period_ended_at', v_period.closed_at,
                    'version', v_quota.version,
                    'updated_at', greatest(
                        v_group_updated_at,
                        v_quota.updated_at,
                        v_period.updated_at
                    )
                );
            END IF;
        END IF;
    END IF;

    RETURN QUERY
    SELECT canonical.result_period_id,
           canonical.result_total_tokens,
           canonical.result_consumed_tokens,
           canonical.result_reserved_tokens,
           canonical.result_remaining_tokens,
           canonical.result_quota_version,
           v_before_state
    FROM public.poolai_quota_adjust_total(
        p_group_id,
        p_new_total_tokens,
        p_expected_quota_version,
        p_actor_user_id,
        p_event_id,
        p_outbox_id,
        p_idempotency_key,
        p_reason
    ) AS canonical;
END;
$function$;

CREATE FUNCTION public.poolai_group_quota_reset(
    p_group_id uuid,
    p_new_period_id uuid,
    p_new_total_tokens numeric,
    p_expected_quota_version bigint,
    p_actor_user_id uuid,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_period_id uuid,
    result_period_number bigint,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric,
    result_quota_version bigint,
    result_before_state jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_existing_event boolean;
    v_quota public.group_token_quotas%ROWTYPE;
    v_group_status text;
    v_group_updated_at timestamptz;
    v_period public.group_quota_periods%ROWTYPE;
    v_before_state jsonb;
BEGIN
    -- Invalid requests are delegated unchanged to the canonical function so it
    -- remains the single validator and preserves its existing error precedence.
    IF p_group_id IS NOT NULL
        AND p_new_period_id IS NOT NULL
        AND p_new_total_tokens IS NOT NULL
        AND p_expected_quota_version IS NOT NULL
        AND p_actor_user_id IS NOT NULL
        AND p_event_id IS NOT NULL
        AND p_outbox_id IS NOT NULL
        AND p_idempotency_key IS NOT NULL
        AND btrim(p_idempotency_key) <> ''
        AND p_new_total_tokens BETWEEN 1 AND 9007199254740991
        AND p_new_total_tokens = trunc(p_new_total_tokens)
        AND p_reason IS NOT NULL
        AND btrim(p_reason) <> '' THEN
        -- This is the fixed serialization root shared with archive.
        SELECT quota.* INTO v_quota
        FROM public.group_token_quotas AS quota
        WHERE quota.group_id = p_group_id
        FOR UPDATE;

        IF FOUND THEN
            SELECT EXISTS (
                SELECT 1
                FROM public.group_quota_events AS quota_event
                WHERE quota_event.idempotency_key = p_idempotency_key
            )
            INTO v_existing_event;

            -- A committed event must be replayed (or rejected as key reuse)
            -- before current Group lifecycle or quota-version checks. Both
            -- paths take the Group fence only after the quota row; only a new
            -- key rejects the archived lifecycle state.
            IF NOT v_existing_event THEN
                SELECT current_group.status,
                       current_group.updated_at
                INTO v_group_status,
                     v_group_updated_at
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                  AND current_group.status <> 'archived'
                FOR SHARE;

                IF NOT FOUND THEN
                    PERFORM public.poolai_business_error('group_not_found_or_archived');
                END IF;
            ELSE
                SELECT current_group.status,
                       current_group.updated_at
                INTO v_group_status,
                     v_group_updated_at
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                FOR SHARE;
            END IF;

            SELECT current_period.* INTO v_period
            FROM public.group_quota_periods AS current_period
            WHERE current_period.id = v_quota.current_period_id
              AND current_period.group_id = p_group_id
            FOR UPDATE;

            IF FOUND THEN
                v_before_state := pg_catalog.jsonb_build_object(
                    'group_id', v_quota.group_id,
                    'period_id', v_period.id,
                    'status', CASE
                        WHEN v_group_status <> 'active' OR NOT v_quota.enabled
                            THEN 'disabled'
                        WHEN v_period.consumed_tokens >= v_period.total_tokens
                            THEN 'exhausted'
                        ELSE 'active'
                    END,
                    'total_tokens', v_period.total_tokens::text,
                    'consumed_tokens', v_period.consumed_tokens::text,
                    'reserved_tokens', v_period.reserved_tokens::text,
                    'remaining_tokens', public.poolai_quota_remaining(
                        v_period.total_tokens,
                        v_period.consumed_tokens,
                        v_period.reserved_tokens
                    )::text,
                    'overage_tokens', greatest(
                        v_period.consumed_tokens - v_period.total_tokens,
                        0::numeric
                    )::text,
                    'period_started_at', v_period.opened_at,
                    'period_ended_at', v_period.closed_at,
                    'version', v_quota.version,
                    'updated_at', greatest(
                        v_group_updated_at,
                        v_quota.updated_at,
                        v_period.updated_at
                    )
                );
            END IF;
        END IF;
    END IF;

    RETURN QUERY
    SELECT canonical.result_period_id,
           canonical.result_period_number,
           canonical.result_total_tokens,
           canonical.result_consumed_tokens,
           canonical.result_reserved_tokens,
           canonical.result_remaining_tokens,
           canonical.result_quota_version,
           v_before_state
    FROM public.poolai_quota_reset(
        p_group_id,
        p_new_period_id,
        p_new_total_tokens,
        p_expected_quota_version,
        p_actor_user_id,
        p_event_id,
        p_outbox_id,
        p_idempotency_key,
        p_reason
    ) AS canonical;
END;
$function$;

-- Ownership transfer needs schema CREATE only inside this migration
-- transaction. The owner is NOLOGIN and is not inherited by runtime roles.
GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_group_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_group_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_group_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_group_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) TO poolai_api;
RESET ROLE;

-- Fail closed if owner/search-path/EXECUTE boundaries drift, if a legacy raw
-- mutation is reopened, or if direct quota-ledger DML is accidentally granted.
DO $permission_audit$
DECLARE
    v_function_signature text;
    v_function_oid oid;
    v_table_name text;
BEGIN
    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = ANY (ARRAY[
              'poolai_group_quota_adjust_total',
              'poolai_group_quota_reset'
          ])
    ) <> 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e1_entry_point_overload_forbidden';
    END IF;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_group_quota_adjust_total(uuid,numeric,bigint,uuid,uuid,uuid,text,text)',
        'public.poolai_group_quota_reset(uuid,uuid,numeric,bigint,uuid,uuid,uuid,text,text)'
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
              AND procedure.proretset
              AND pg_catalog.cardinality(procedure.proargmodes) = CASE
                  WHEN procedure.proname = 'poolai_group_quota_adjust_total'
                      THEN 15
                  ELSE 17
              END
              AND procedure.proargnames[
                  pg_catalog.array_upper(procedure.proargnames, 1)
              ] = 'result_before_state'
              AND procedure.proallargtypes[
                  pg_catalog.array_upper(procedure.proallargtypes, 1)
              ] = pg_catalog.to_regtype('pg_catalog.jsonb')
              AND procedure.proargmodes[
                  pg_catalog.array_upper(procedure.proargmodes, 1)
              ] = 't'
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
                    AND (
                        privilege.grantor <> procedure.proowner
                        OR privilege.is_grantable
                        OR privilege.grantee NOT IN (
                            procedure.proowner,
                            (
                                SELECT role.oid
                                FROM pg_catalog.pg_roles AS role
                                WHERE role.rolname = 'poolai_api'
                            )
                        )
                    )
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m3_e1_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    IF pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_initialize(uuid,uuid,numeric,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_adjust_total(uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_reset(uuid,uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE')
        OR pg_catalog.pg_has_role(
            'poolai_api', 'poolai_runtime_owner', 'MEMBER')
        OR pg_catalog.pg_has_role(
            'poolai_worker', 'poolai_runtime_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e1_legacy_or_owner_authority_forbidden';
    END IF;

    FOREACH v_table_name IN ARRAY ARRAY[
        'group_token_quotas',
        'group_quota_periods',
        'group_quota_events'
    ]
    LOOP
        IF pg_catalog.has_table_privilege(
                'poolai_api',
                pg_catalog.format('public.%I', v_table_name),
                'INSERT')
            OR pg_catalog.has_table_privilege(
                'poolai_api',
                pg_catalog.format('public.%I', v_table_name),
                'UPDATE')
            OR pg_catalog.has_table_privilege(
                'poolai_api',
                pg_catalog.format('public.%I', v_table_name),
                'DELETE')
            OR pg_catalog.has_any_column_privilege(
                'poolai_api',
                pg_catalog.format('public.%I', v_table_name),
                'INSERT')
            OR pg_catalog.has_any_column_privilege(
                'poolai_api',
                pg_catalog.format('public.%I', v_table_name),
                'UPDATE') THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m3_e1_direct_quota_write_forbidden',
                DETAIL = v_table_name;
        END IF;
    END LOOP;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e1_runtime_schema_create_forbidden';
    END IF;
END;
$permission_audit$;
