-- PoolAI Release 1 M1-E3 canonical authorization and RBAC hardening.
--
-- User lifecycle and display-name changes must pass through the atomic
-- Identity update function. The API role retains only the direct User-column
-- writes required by login, session revocation, password, and TOTP use cases.

REVOKE UPDATE ON public.users FROM poolai_api;
GRANT UPDATE (
    password_hash,
    totp_secret_envelope,
    totp_last_accepted_step,
    security_stamp,
    token_version,
    failed_login_count,
    locked_until,
    last_login_at
) ON public.users TO poolai_api;

-- Creating a User assigns its one role through a direct INSERT. The role
-- trigger must still advance token_version/version after the API role loses
-- general User UPDATE, but the API must not regain direct access to those
-- columns. Run the fixed-search-path trigger as the same narrowly privileged
-- NOLOGIN owner used by the atomic Identity update entry point.
ALTER FUNCTION public.poolai_bump_role_user_version()
    SECURITY DEFINER
    SET search_path = pg_catalog, public, pg_temp;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_bump_role_user_version()
    OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_bump_role_user_version()
    FROM PUBLIC, poolai_api, poolai_worker;
RESET ROLE;

-- Auditor is a distinct authenticated role and must remain distinct in the
-- append-only audit trail rather than being collapsed into actor_type=user.
ALTER TABLE public.audit_logs
    DROP CONSTRAINT ck_audit_logs_actor_user,
    DROP CONSTRAINT ck_audit_logs_actor_type;

ALTER TABLE public.audit_logs
    ADD CONSTRAINT ck_audit_logs_actor_type CHECK (
        actor_type IN ('user', 'admin', 'operator', 'auditor', 'system', 'service')
    ),
    ADD CONSTRAINT ck_audit_logs_actor_user CHECK (
        (actor_type IN ('user', 'admin', 'operator', 'auditor')
            AND actor_user_id IS NOT NULL)
        OR actor_type IN ('system', 'service')
    );

DO $permission_audit$
DECLARE
    v_update_columns text[];
BEGIN
    SELECT array_agg(attribute.attname ORDER BY attribute.attname)
    INTO v_update_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.users'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_api',
          'public.users',
          attribute.attname,
          'UPDATE');

    IF v_update_columns IS DISTINCT FROM ARRAY[
        'failed_login_count',
        'last_login_at',
        'locked_until',
        'password_hash',
        'security_stamp',
        'token_version',
        'totp_last_accepted_step',
        'totp_secret_envelope'
    ]::text[] THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_identity_m1_e3_user_update_authority_mismatch';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE namespace.nspname = 'public'
          AND procedure.proname = 'poolai_bump_role_user_version'
          AND procedure.pronargs = 0
          AND procedure.prosecdef
          AND owner.rolname = 'poolai_runtime_owner'
          AND procedure.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_identity_m1_e3_role_trigger_authority_missing';
    END IF;
END;
$permission_audit$;
