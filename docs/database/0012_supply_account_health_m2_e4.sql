-- PoolAI Release 1 M2-E4 canonical Account health persistence boundary.
--
-- Redis owns the shared circuit-breaker clock and transition decision. Supply
-- persists the resulting canonical health observation through this one
-- bounded entry point. This migration performs no probe, Redis operation,
-- lifecycle transition, credential operation, or data repair.

ALTER TABLE public.accounts
    ADD CONSTRAINT ck_accounts_health_observation CHECK (
        (
            last_health_status = 'cooling'
            AND last_health_at IS NOT NULL
            AND upstream_rate_limited_until IS NOT NULL
        )
        OR (
            last_health_status IN ('healthy', 'degraded', 'unhealthy')
            AND last_health_at IS NOT NULL
            AND upstream_rate_limited_until IS NULL
        )
        OR (
            last_health_status = 'unknown'
            AND upstream_rate_limited_until IS NULL
        )
    );

-- Worker no longer owns a second, direct Account-health write protocol.
-- API and Worker receive the same SECURITY DEFINER transition ABI below.
REVOKE UPDATE (
    upstream_rate_limited_until,
    last_health_at,
    last_health_status,
    version,
    updated_at
) ON public.accounts FROM poolai_worker;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
SET LOCAL ROLE poolai_runtime_owner;

-- A Base URL path may change without rotating the credential, but moving the
-- same credential to a different normalized authority would create an
-- implicit credential-rebinding primitive. Keep this rule at the row boundary
-- so every Account writer observes it after taking the canonical row lock.
CREATE FUNCTION public.poolai_supply_guard_account_credential_authority()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_new_authority text;
    v_new_host text;
    v_new_port text;
    v_new_scheme text;
    v_old_authority text;
    v_old_host text;
    v_old_port text;
    v_old_scheme text;
    v_separator integer;
BEGIN
    IF NEW.upstream_base_url IS NOT DISTINCT FROM OLD.upstream_base_url THEN
        RETURN NEW;
    END IF;

    v_old_scheme := pg_catalog.split_part(
        OLD.upstream_base_url, '://', 1);
    v_old_authority := pg_catalog.split_part(
        pg_catalog.split_part(OLD.upstream_base_url, '://', 2),
        '/',
        1);
    IF pg_catalog.left(v_old_authority, 1) = '[' THEN
        v_separator := pg_catalog.strpos(v_old_authority, ']');
        v_old_host := pg_catalog.left(v_old_authority, v_separator);
        v_old_port := CASE
            WHEN pg_catalog.length(v_old_authority) > v_separator
                THEN pg_catalog.substr(
                    v_old_authority, v_separator + 2)
            ELSE NULL
        END;
    ELSE
        v_separator := pg_catalog.strpos(v_old_authority, ':');
        v_old_host := CASE
            WHEN v_separator > 0
                THEN pg_catalog.left(v_old_authority, v_separator - 1)
            ELSE v_old_authority
        END;
        v_old_port := CASE
            WHEN v_separator > 0
                THEN pg_catalog.substr(
                    v_old_authority, v_separator + 1)
            ELSE NULL
        END;
    END IF;
    v_old_authority :=
        pg_catalog.lower(v_old_scheme)
        || '://'
        || pg_catalog.lower(v_old_host)
        || ':'
        || coalesce(
            v_old_port,
            CASE WHEN v_old_scheme = 'http' THEN '80' ELSE '443' END);

    v_new_scheme := pg_catalog.split_part(
        NEW.upstream_base_url, '://', 1);
    v_new_authority := pg_catalog.split_part(
        pg_catalog.split_part(NEW.upstream_base_url, '://', 2),
        '/',
        1);
    IF pg_catalog.left(v_new_authority, 1) = '[' THEN
        v_separator := pg_catalog.strpos(v_new_authority, ']');
        v_new_host := pg_catalog.left(v_new_authority, v_separator);
        v_new_port := CASE
            WHEN pg_catalog.length(v_new_authority) > v_separator
                THEN pg_catalog.substr(
                    v_new_authority, v_separator + 2)
            ELSE NULL
        END;
    ELSE
        v_separator := pg_catalog.strpos(v_new_authority, ':');
        v_new_host := CASE
            WHEN v_separator > 0
                THEN pg_catalog.left(v_new_authority, v_separator - 1)
            ELSE v_new_authority
        END;
        v_new_port := CASE
            WHEN v_separator > 0
                THEN pg_catalog.substr(
                    v_new_authority, v_separator + 1)
            ELSE NULL
        END;
    END IF;
    v_new_authority :=
        pg_catalog.lower(v_new_scheme)
        || '://'
        || pg_catalog.lower(v_new_host)
        || ':'
        || coalesce(
            v_new_port,
            CASE WHEN v_new_scheme = 'http' THEN '80' ELSE '443' END);

    IF v_new_authority IS DISTINCT FROM v_old_authority
        AND NEW.credential_revision <= OLD.credential_revision THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE =
                'poolai_supply_base_url_credential_replacement_required';
    END IF;

    RETURN NEW;
END;
$function$;

COMMENT ON FUNCTION
    public.poolai_supply_guard_account_credential_authority() IS
    'Rejects an Account Base URL normalized-authority change unless the same atomic write advances the credential revision.';

-- The migration/table owner creates the trigger while the function still has
-- its creation-time PUBLIC EXECUTE. Runtime execution happens from the
-- Supply-owned SECURITY DEFINER Account entry points. Remove every external
-- direct grant immediately after binding the trigger.
RESET ROLE;
CREATE TRIGGER trg_accounts_guard_credential_authority
BEFORE UPDATE OF upstream_base_url, credential_revision
ON public.accounts
FOR EACH ROW
EXECUTE FUNCTION
    public.poolai_supply_guard_account_credential_authority();
SET LOCAL ROLE poolai_runtime_owner;

REVOKE ALL ON FUNCTION
    public.poolai_supply_guard_account_credential_authority()
FROM PUBLIC, poolai_api, poolai_worker;

CREATE FUNCTION public.poolai_supply_record_account_health(
    p_account_id uuid,
    p_health_status text,
    p_observed_at timestamptz,
    p_retry_at timestamptz,
    p_expected_account_version bigint,
    p_expected_credential_revision bigint
)
RETURNS TABLE(
    disposition text,
    was_changed boolean,
    before_health_status text,
    before_retry_at timestamptz,
    before_observed_at timestamptz,
    before_version bigint,
    current_health_status text,
    current_retry_at timestamptz,
    current_observed_at timestamptz,
    current_version bigint,
    current_account_status text,
    current_credential_revision bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_account record;
    v_now timestamptz;
BEGIN
    IF p_account_id IS NULL
        OR p_health_status IS NULL
        OR p_health_status NOT IN (
            'unknown', 'healthy', 'degraded', 'cooling', 'unhealthy'
        )
        OR p_observed_at IS NULL
        OR NOT pg_catalog.isfinite(p_observed_at)
        OR p_expected_account_version IS NULL
        OR p_expected_account_version <= 0
        OR p_expected_credential_revision IS NULL
        OR p_expected_credential_revision <= 0
        OR (
            p_health_status = 'cooling'
            AND (
                p_retry_at IS NULL
                OR NOT pg_catalog.isfinite(p_retry_at)
            )
        )
        OR (
            p_health_status <> 'cooling'
            AND p_retry_at IS NOT NULL
        ) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::text,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::bigint,
            NULL::text,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::bigint,
            NULL::text,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT
        account.status,
        account.upstream_rate_limited_until,
        account.last_health_at,
        account.last_health_status,
        account.version,
        account.credential_revision,
        account.deleted_at
    INTO v_account
    FROM public.accounts AS account
    WHERE account.id = p_account_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text,
            false,
            NULL::text,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::bigint,
            NULL::text,
            NULL::timestamptz,
            NULL::timestamptz,
            NULL::bigint,
            NULL::text,
            NULL::bigint;
        RETURN;
    END IF;

    IF v_account.status = 'retired'
        OR v_account.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'account_retired'::text,
            false,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            v_account.last_health_at,
            v_account.version,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            v_account.last_health_at,
            v_account.version,
            v_account.status,
            v_account.credential_revision;
        RETURN;
    END IF;

    -- Compare only after the row-lock wait. The observed public version and
    -- credential revision fence every lifecycle/base-url/credential mutation.
    -- An older timestamp can never overwrite a newer committed observation.
    IF v_account.version <> p_expected_account_version
        OR v_account.credential_revision <>
            p_expected_credential_revision
        OR (
            v_account.last_health_at IS NOT NULL
            AND (
                p_observed_at < v_account.last_health_at
                OR (
                    p_observed_at = v_account.last_health_at
                    AND (
                        p_health_status IS DISTINCT FROM
                            v_account.last_health_status
                        OR p_retry_at IS DISTINCT FROM
                            v_account.upstream_rate_limited_until
                    )
                )
            )
        ) THEN
        RETURN QUERY SELECT
            'stale_observation'::text,
            false,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            v_account.last_health_at,
            v_account.version,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            v_account.last_health_at,
            v_account.version,
            v_account.status,
            v_account.credential_revision;
        RETURN;
    END IF;

    -- A same-state fresh observation advances only the scheduling timestamp.
    -- It deliberately does not advance the public Account version or append an
    -- audit event, avoiding a version/audit storm during periodic health rounds.
    IF v_account.last_health_at IS NOT NULL
        AND p_health_status = v_account.last_health_status
        AND p_retry_at IS NOT DISTINCT FROM
            v_account.upstream_rate_limited_until THEN
        IF p_observed_at > v_account.last_health_at THEN
            UPDATE public.accounts AS account
            SET last_health_at = p_observed_at,
                updated_at = pg_catalog.clock_timestamp()
            WHERE account.id = p_account_id;
        END IF;

        RETURN QUERY SELECT
            'duplicate'::text,
            false,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            v_account.last_health_at,
            v_account.version,
            v_account.last_health_status,
            v_account.upstream_rate_limited_until,
            p_observed_at,
            v_account.version,
            v_account.status,
            v_account.credential_revision;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    UPDATE public.accounts AS account
    SET upstream_rate_limited_until = p_retry_at,
        last_health_at = p_observed_at,
        last_health_status = p_health_status,
        version = v_account.version + 1,
        updated_at = v_now
    WHERE account.id = p_account_id;

    RETURN QUERY SELECT
        'applied'::text,
        true,
        v_account.last_health_status,
        v_account.upstream_rate_limited_until,
        v_account.last_health_at,
        v_account.version,
        p_health_status,
        p_retry_at,
        p_observed_at,
        v_account.version + 1,
        v_account.status,
        v_account.credential_revision;
END;
$function$;

COMMENT ON FUNCTION public.poolai_supply_record_account_health(
    uuid, text, timestamptz, timestamptz, bigint, bigint
) IS
    'Supply-owned version/revision-fenced Account health transition. Rejects late observations, advances the public Account version only on a real state transition, and never changes lifecycle or credential revision.';

REVOKE ALL ON FUNCTION public.poolai_supply_record_account_health(
    uuid, text, timestamptz, timestamptz, bigint, bigint
) FROM PUBLIC, poolai_api, poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_supply_record_account_health(
    uuid, text, timestamptz, timestamptz, bigint, bigint
) TO poolai_api, poolai_worker;

RESET ROLE;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

-- Fail closed if the direct-DML, owner/search-path, or exact EXECUTE boundary
-- drifts while this migration is applied.
DO $permission_audit$
DECLARE
    v_api_oid oid;
    v_function_oid oid;
    v_guard_function_oid oid;
    v_owner_oid oid;
    v_worker_oid oid;
BEGIN
    SELECT role.oid
    INTO v_api_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';
    SELECT role.oid
    INTO v_worker_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_worker';
    SELECT role.oid
    INTO v_owner_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_runtime_owner';
    v_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_supply_record_account_health(uuid,text,timestamp with time zone,timestamp with time zone,bigint,bigint)');
    v_guard_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_supply_guard_account_credential_authority()');

    IF v_api_oid IS NULL
        OR v_worker_oid IS NULL
        OR v_owner_oid IS NULL
        OR v_function_oid IS NULL
        OR v_guard_function_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e4_health_role_or_function_missing';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE')
        OR pg_catalog.pg_has_role(
            'poolai_api', 'poolai_runtime_owner', 'MEMBER')
        OR pg_catalog.pg_has_role(
            'poolai_worker', 'poolai_runtime_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e4_health_role_boundary_missing';
    END IF;

    IF pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'upstream_rate_limited_until',
            'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'last_health_at',
            'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'last_health_status',
            'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'version',
            'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'updated_at',
            'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'UPDATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e4_health_direct_dml_boundary_missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_function_oid
          AND procedure.prosecdef
          AND procedure.provolatile = 'v'
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND procedure.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
          AND pg_catalog.has_function_privilege(
              'poolai_api', procedure.oid, 'EXECUTE')
          AND pg_catalog.has_function_privilege(
              'poolai_worker', procedure.oid, 'EXECUTE')
          AND NOT EXISTS (
              SELECT 1
              FROM pg_catalog.aclexplode(COALESCE(
                  procedure.proacl,
                  pg_catalog.acldefault(
                      'f', procedure.proowner))) AS acl
              WHERE acl.privilege_type = 'EXECUTE'
                AND (
                    acl.grantor <> procedure.proowner
                    OR acl.is_grantable
                    OR acl.grantee NOT IN (
                        v_owner_oid, v_api_oid, v_worker_oid
                    )
                )
          )
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e4_health_entry_point_boundary_missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_guard_function_oid
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
                  pg_catalog.acldefault(
                      'f', procedure.proowner))) AS acl
              WHERE acl.privilege_type = 'EXECUTE'
                AND (
                    acl.grantor <> procedure.proowner
                    OR acl.is_grantable
                    OR acl.grantee <> v_owner_oid
                )
          )
          AND EXISTS (
              SELECT 1
              FROM pg_catalog.pg_trigger AS trigger_definition
              WHERE trigger_definition.tgrelid =
                    'public.accounts'::regclass
                AND trigger_definition.tgname =
                    'trg_accounts_guard_credential_authority'
                AND trigger_definition.tgfoid = procedure.oid
                AND NOT trigger_definition.tgisinternal
                AND trigger_definition.tgenabled = 'O'
          )
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE =
                'poolai_m2_e4_credential_authority_guard_missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_constraint AS constraint_definition
        WHERE constraint_definition.conrelid = 'public.accounts'::regclass
          AND constraint_definition.conname =
              'ck_accounts_health_observation'
          AND constraint_definition.contype = 'c'
          AND constraint_definition.convalidated
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'poolai_m2_e4_health_constraint_missing';
    END IF;
END;
$permission_audit$;
