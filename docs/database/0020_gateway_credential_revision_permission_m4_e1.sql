-- PoolAI Release 1 M4-E1 Gateway credential-revision read closure.
--
-- Migration 0010 added accounts.credential_revision after 0003 had granted the
-- co-hosted API/Gateway role an explicit Account SELECT column list. PostgreSQL
-- does not extend a column-level grant to a later column, so schema 19 cannot
-- execute either the route snapshot or credential-lease revision fence. Keep
-- the signed 0010/0019 bytes immutable and add only the missing read capability.

GRANT SELECT (credential_revision)
    ON public.accounts TO poolai_api;

-- Fail closed unless this migration leaves exactly the intended column-level
-- read boundary. It must not create a table-wide SELECT path, a write path, a
-- grant option, a new Account column, an elevated role, or role membership.
DO $permission_audit$
DECLARE
    v_account_columns text[];
    v_api_role_oid oid;
    v_api_select_columns text[];
    v_runtime_owner_role_oid oid;
    v_table_owner_oid oid;
    v_worker_role_oid oid;
BEGIN
    SELECT role.oid
    INTO v_api_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';

    SELECT role.oid
    INTO v_runtime_owner_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_runtime_owner';

    SELECT role.oid
    INTO v_worker_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_worker';

    SELECT relation.relowner
    INTO v_table_owner_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.oid = 'public.accounts'::regclass;

    IF v_api_role_oid IS NULL
        OR v_runtime_owner_role_oid IS NULL
        OR v_worker_role_oid IS NULL
        OR v_table_owner_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_role_missing';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles AS role
        WHERE role.oid IN (
            v_runtime_owner_role_oid,
            v_api_role_oid,
            v_worker_role_oid
        )
          AND (
              role.rolsuper
              OR role.rolcreaterole
              OR role.rolcreatedb
              OR role.rolreplication
              OR role.rolbypassrls
          )
    )
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_roles AS role
            WHERE role.oid = v_runtime_owner_role_oid
              AND NOT role.rolcanlogin
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_role_attributes_forbidden';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members AS membership
        WHERE membership.member IN (v_api_role_oid, v_worker_role_oid)
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_membership_forbidden';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_schema_create_forbidden';
    END IF;

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_account_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.accounts'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped;

    IF v_account_columns IS DISTINCT FROM ARRAY[
        'id',
        'provider',
        'name',
        'auth_type',
        'upstream_base_url',
        'credential_envelope',
        'credential_prefix',
        'credential_hint',
        'settings',
        'status',
        'priority',
        'weight',
        'max_concurrency',
        'upstream_rate_limited_until',
        'last_health_at',
        'last_health_status',
        'version',
        'created_at',
        'updated_at',
        'deleted_at',
        'credential_revision'
    ]::text[] THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_account_columns_drifted';
    END IF;

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_api_select_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.accounts'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_api',
          'public.accounts',
          attribute.attname,
          'SELECT');

    IF v_api_select_columns IS DISTINCT FROM v_account_columns THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_api_select_columns_missing';
    END IF;

    IF pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'SELECT WITH GRANT OPTION')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'UPDATE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'DELETE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'TRUNCATE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'REFERENCES')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'TRIGGER')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'REFERENCES') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_api_boundary_missing';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
            relation.relacl,
            pg_catalog.acldefault('r', relation.relowner))) AS privilege
        WHERE relation.oid = 'public.accounts'::regclass
          AND privilege.grantee IN (0, v_api_role_oid)
    )
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_attribute AS attribute
            CROSS JOIN LATERAL pg_catalog.aclexplode(
                attribute.attacl) AS privilege
            WHERE attribute.attrelid = 'public.accounts'::regclass
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
              AND (
                  privilege.grantee NOT IN (
                      v_runtime_owner_role_oid,
                      v_api_role_oid,
                      v_worker_role_oid
                  )
                  OR privilege.grantor <> v_table_owner_oid
                  OR privilege.is_grantable
                  OR (
                      privilege.grantee IN (v_api_role_oid, v_worker_role_oid)
                      AND privilege.privilege_type <> 'SELECT'
                  )
                  OR (
                      privilege.grantee = v_runtime_owner_role_oid
                      AND privilege.privilege_type NOT IN (
                          'SELECT', 'INSERT', 'UPDATE'
                      )
                  )
              )
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_acl_shape_forbidden';
    END IF;

    IF (
        SELECT pg_catalog.array_agg(
            COALESCE(role.rolname, 'PUBLIC') || ':'
                || privilege.privilege_type || ':'
                || privilege.is_grantable::text
            ORDER BY COALESCE(role.rolname, 'PUBLIC'),
                privilege.privilege_type, privilege.is_grantable)
        FROM pg_catalog.pg_attribute AS attribute
        CROSS JOIN LATERAL pg_catalog.aclexplode(
            attribute.attacl) AS privilege
        LEFT JOIN pg_catalog.pg_roles AS role
          ON role.oid = privilege.grantee
        WHERE attribute.attrelid = 'public.accounts'::regclass
          AND attribute.attname = 'credential_revision'
    ) IS DISTINCT FROM ARRAY[
        'poolai_api:SELECT:false',
        'poolai_runtime_owner:INSERT:false',
        'poolai_runtime_owner:SELECT:false',
        'poolai_runtime_owner:UPDATE:false',
        'poolai_worker:SELECT:false'
    ]::text[] THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m4_e1_credential_revision_grantee_drifted';
    END IF;
END;
$permission_audit$;
