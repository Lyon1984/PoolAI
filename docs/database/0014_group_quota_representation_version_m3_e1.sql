-- PoolAI Release 1 M3-E1 Group quota representation-version correction.
--
-- Migration 0013 exposed quota management through the signed 0002 ledger
-- functions, but the public strong ETag is the stable quota-row version while
-- current-period counter changes previously advanced only the period version.
-- This forward migration establishes one representation epoch, then keeps the
-- quota version synchronized with true consumed/reserved changes on the current
-- period. Group lifecycle/name/version remain a separate representation and do
-- not participate in the quota body, ETag, status, or updated_at.

-- Invalidate every validator issued under the pre-0014 representation rules.
-- The table lock excludes quota writers before Group and current Period rows are
-- frozen in the established Quota -> Group -> Period order. All existing rows
-- advance exactly once; bigint exhaustion fails the whole migration atomically.
DO $semantic_epoch$
DECLARE
    v_expected_rows bigint;
    v_updated_rows bigint;
BEGIN
    LOCK TABLE public.group_token_quotas IN SHARE ROW EXCLUSIVE MODE;

    PERFORM 1
    FROM public.groups AS current_group
    JOIN public.group_token_quotas AS quota
      ON quota.group_id = current_group.id
    ORDER BY quota.group_id
    FOR SHARE OF current_group;

    PERFORM 1
    FROM public.group_quota_periods AS current_period
    JOIN public.group_token_quotas AS quota
      ON quota.current_period_id = current_period.id
     AND quota.group_id = current_period.group_id
    ORDER BY quota.group_id
    FOR SHARE OF current_period;

    IF EXISTS (
        SELECT 1
        FROM public.group_token_quotas AS quota
        WHERE quota.version = 9223372036854775807
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '22003',
            MESSAGE = 'group_quota_representation_version_epoch_overflow';
    END IF;

    SELECT pg_catalog.count(*)
    INTO v_expected_rows
    FROM public.group_token_quotas;

    UPDATE public.group_token_quotas AS quota
    SET version = quota.version + 1,
        updated_at = greatest(
            quota.updated_at,
            current_period.updated_at,
            current_group.updated_at
        )
    FROM public.group_quota_periods AS current_period,
         public.groups AS current_group
    WHERE current_period.id = quota.current_period_id
      AND current_period.group_id = quota.group_id
      AND current_group.id = quota.group_id;

    GET DIAGNOSTICS v_updated_rows = ROW_COUNT;
    IF v_updated_rows <> v_expected_rows THEN
        RAISE EXCEPTION USING
            ERRCODE = '23503',
            MESSAGE = 'group_quota_representation_epoch_incomplete';
    END IF;
END;
$semantic_epoch$;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
SET LOCAL ROLE poolai_runtime_owner;

-- Every signed counter-mutating 0002 function locks the stable quota row before
-- the period row. The trigger therefore reuses that lock without introducing a
-- second ordering root. A late mutation of a closed period intentionally finds
-- no current-period match and cannot invalidate the current quota resource.
CREATE FUNCTION public.poolai_bump_current_quota_representation_version()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF TG_OP <> 'UPDATE'
        OR TG_NARGS <> 0
        OR TG_TABLE_SCHEMA <> 'public'
        OR TG_TABLE_NAME <> 'group_quota_periods' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'group_quota_representation_trigger_binding_invalid';
    END IF;

    IF NEW.consumed_tokens IS NOT DISTINCT FROM OLD.consumed_tokens
        AND NEW.reserved_tokens IS NOT DISTINCT FROM OLD.reserved_tokens THEN
        RETURN NULL;
    END IF;

    UPDATE public.group_token_quotas AS quota
    SET version = quota.version + 1,
        updated_at = greatest(quota.updated_at, NEW.updated_at)
    WHERE quota.group_id = NEW.group_id
      AND quota.current_period_id = NEW.id;

    RETURN NULL;
END;
$function$;

-- Forward-replace the 0013 wrapper without changing its signature. The Group
-- row remains an archive fence and lock-order participant only; it is no longer
-- part of the public quota representation.
CREATE OR REPLACE FUNCTION public.poolai_group_quota_adjust_total(
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
    v_period public.group_quota_periods%ROWTYPE;
    v_before_state jsonb;
BEGIN
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

            IF NOT v_existing_event THEN
                PERFORM 1
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                  AND current_group.status <> 'archived'
                FOR SHARE;

                IF NOT FOUND THEN
                    PERFORM public.poolai_business_error(
                        'group_not_found_or_archived');
                END IF;
            ELSE
                PERFORM 1
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
                        WHEN NOT v_quota.enabled THEN 'disabled'
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
                    'updated_at', v_quota.updated_at
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

CREATE OR REPLACE FUNCTION public.poolai_group_quota_reset(
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
    v_period public.group_quota_periods%ROWTYPE;
    v_before_state jsonb;
BEGIN
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

            IF NOT v_existing_event THEN
                PERFORM 1
                FROM public.groups AS current_group
                WHERE current_group.id = p_group_id
                  AND current_group.status <> 'archived'
                FOR SHARE;

                IF NOT FOUND THEN
                    PERFORM public.poolai_business_error(
                        'group_not_found_or_archived');
                END IF;
            ELSE
                PERFORM 1
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
                        WHEN NOT v_quota.enabled THEN 'disabled'
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
                    'updated_at', v_quota.updated_at
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

RESET ROLE;

CREATE TRIGGER tr_group_quota_period_counter_representation_version
AFTER UPDATE OF consumed_tokens, reserved_tokens
ON public.group_quota_periods
FOR EACH ROW
EXECUTE FUNCTION public.poolai_bump_current_quota_representation_version();

SET LOCAL ROLE poolai_runtime_owner;

REVOKE ALL ON FUNCTION
    public.poolai_bump_current_quota_representation_version()
FROM PUBLIC, poolai_api, poolai_worker;
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
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

-- Freeze the corrected ABI, trigger binding, and least-privilege boundary.
DO $permission_audit$
DECLARE
    v_function_oid oid;
    v_function_signature text;
    v_table_name text;
    v_trigger_function_oid oid;
BEGIN
    IF (
        SELECT pg_catalog.count(*)
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
            MESSAGE = 'poolai_m3_e1_representation_wrapper_overload_forbidden';
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
                MESSAGE = 'poolai_m3_e1_representation_wrapper_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    v_trigger_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_bump_current_quota_representation_version()');
    IF v_trigger_function_oid IS NULL OR NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_trigger_function_oid
          AND procedure.prorettype = 'pg_catalog.trigger'::regtype
          AND procedure.prosecdef
          AND procedure.provolatile = 'v'
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
                    OR privilege.is_grantable
                    OR privilege.grantee <> procedure.proowner
                )
          )
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e1_representation_trigger_function_boundary_missing';
    END IF;

    IF (
        SELECT pg_catalog.count(*)
        FROM pg_catalog.pg_trigger AS trigger
        WHERE NOT trigger.tgisinternal
          AND trigger.tgname =
              'tr_group_quota_period_counter_representation_version'
          AND trigger.tgrelid = 'public.group_quota_periods'::regclass
          AND trigger.tgfoid = v_trigger_function_oid
          AND trigger.tgtype = 17
          AND trigger.tgnargs = 0
          AND trigger.tgattr::text = '5 6'
    ) <> 1 OR (
        SELECT pg_catalog.count(*)
        FROM pg_catalog.pg_trigger AS trigger
        WHERE NOT trigger.tgisinternal
          AND trigger.tgfoid = v_trigger_function_oid
    ) <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e1_representation_trigger_binding_missing';
    END IF;

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
            MESSAGE = 'poolai_m3_e1_representation_legacy_authority_forbidden';
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
                MESSAGE = 'poolai_m3_e1_representation_direct_write_forbidden',
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
            MESSAGE = 'poolai_m3_e1_representation_schema_create_forbidden';
    END IF;
END;
$permission_audit$;
