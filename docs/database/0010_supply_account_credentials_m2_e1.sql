-- PoolAI Release 1 M2-E1 Account credential persistence and rewrap boundary.
--
-- Supply owns Account credential persistence and CAS. Operations may trigger
-- and fence the Worker job, but neither Operations nor Migrator receives
-- envelope keys or plaintext. The existing NOLOGIN runtime owner remains
-- unable to SELECT credential_envelope.

REVOKE INSERT, UPDATE ON public.accounts FROM poolai_api;

ALTER TABLE public.accounts
    ADD COLUMN credential_revision bigint NOT NULL DEFAULT 1,
    ADD CONSTRAINT ck_accounts_credential_revision CHECK (
        credential_revision > 0
    );

COMMENT ON COLUMN public.accounts.credential_revision IS
    'Internal credential-only CAS token. Rewrap advances it without changing the public Account version or updated_at.';

-- This validator is a storage-shape guard, not a cryptographic validator.
-- Supply must still authenticate both AEAD layers and rebuild trusted AAD
-- before create, replacement, read or rewrap.
CREATE FUNCTION public.poolai_secret_envelope_v1_is_structurally_valid(
    p_envelope jsonb
)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_binary_fields constant text[] := ARRAY[
        'wrapped_dek', 'wrap_nonce', 'wrap_tag',
        'ciphertext', 'nonce', 'tag'
    ]::text[];
    v_min_octets constant integer[] := ARRAY[
        32, 12, 16, 1, 12, 16
    ]::integer[];
    v_max_octets constant integer[] := ARRAY[
        32, 12, 16, 1048576, 12, 16
    ]::integer[];
    v_canonical text;
    v_decoded bytea;
    v_encoded text;
    v_encoded_length integer;
    v_field_count integer;
    v_index integer;
    v_padding text;
BEGIN
    IF pg_catalog.jsonb_typeof(p_envelope) <> 'object'
        OR NOT p_envelope ?& ARRAY[
            'v', 'alg', 'kid', 'wrapped_dek', 'wrap_nonce', 'wrap_tag',
            'ciphertext', 'nonce', 'tag'
        ]::text[] THEN
        RETURN false;
    END IF;

    SELECT count(*)::integer
    INTO v_field_count
    FROM pg_catalog.jsonb_object_keys(p_envelope);
    IF v_field_count <> 9
        OR pg_catalog.jsonb_typeof(p_envelope -> 'v') <> 'number'
        OR p_envelope ->> 'v' <> '1'
        OR pg_catalog.jsonb_typeof(p_envelope -> 'alg') <> 'string'
        OR p_envelope ->> 'alg' <> 'A256GCM+A256GCM-v1'
        OR pg_catalog.jsonb_typeof(p_envelope -> 'kid') <> 'string'
        OR pg_catalog.char_length(p_envelope ->> 'kid') NOT BETWEEN 1 AND 256
        OR (p_envelope ->> 'kid') !~ '[^[:space:]]'
        OR pg_catalog.strpos(p_envelope ->> 'kid', pg_catalog.chr(65533)) > 0 THEN
        RETURN false;
    END IF;

    FOR v_index IN 1..pg_catalog.array_length(v_binary_fields, 1)
    LOOP
        IF pg_catalog.jsonb_typeof(
                p_envelope -> v_binary_fields[v_index]) <> 'string' THEN
            RETURN false;
        END IF;

        v_encoded := p_envelope ->> v_binary_fields[v_index];
        v_encoded_length := pg_catalog.char_length(v_encoded);
        IF v_encoded_length < 2
            OR v_encoded_length >
                ((v_max_octets[v_index] * 8 + 5) / 6)
            OR v_encoded !~ '^[A-Za-z0-9_-]+$'
            OR v_encoded_length % 4 = 1 THEN
            RETURN false;
        END IF;

        v_padding := CASE v_encoded_length % 4
            WHEN 0 THEN ''
            WHEN 2 THEN '=='
            WHEN 3 THEN '='
            ELSE NULL
        END;
        IF v_padding IS NULL THEN
            RETURN false;
        END IF;

        BEGIN
            v_decoded := pg_catalog.decode(
                pg_catalog.translate(v_encoded, '-_', '+/') || v_padding,
                'base64');
        EXCEPTION
            WHEN OTHERS THEN
                RETURN false;
        END;

        IF pg_catalog.octet_length(v_decoded)
                NOT BETWEEN v_min_octets[v_index] AND v_max_octets[v_index] THEN
            RETURN false;
        END IF;

        v_canonical := pg_catalog.replace(
            pg_catalog.replace(
                pg_catalog.rtrim(
                    pg_catalog.translate(
                        pg_catalog.encode(v_decoded, 'base64'),
                        E'\n\r',
                        ''),
                    '='),
                '+',
                '-'),
            '/',
            '_');
        IF v_canonical <> v_encoded THEN
            RETURN false;
        END IF;
    END LOOP;

    RETURN true;
END;
$function$;

CREATE FUNCTION public.poolai_guard_account_credential_revision()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF NEW.credential_envelope IS NOT DISTINCT FROM OLD.credential_envelope THEN
        IF NEW.credential_revision IS DISTINCT FROM OLD.credential_revision THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'account_credential_revision_without_envelope_change';
        END IF;
        RETURN NEW;
    END IF;

    IF NOT public.poolai_secret_envelope_v1_is_structurally_valid(
            NEW.credential_envelope) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'account_credential_envelope_invalid';
    END IF;
    IF NEW.credential_revision <> OLD.credential_revision + 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'account_credential_revision_invalid';
    END IF;

    IF NEW.version = OLD.version THEN
        IF NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                OLD.credential_envelope)
            OR NEW.credential_envelope -> 'ciphertext'
                IS DISTINCT FROM OLD.credential_envelope -> 'ciphertext'
            OR NEW.credential_envelope -> 'nonce'
                IS DISTINCT FROM OLD.credential_envelope -> 'nonce'
            OR NEW.credential_envelope -> 'tag'
                IS DISTINCT FROM OLD.credential_envelope -> 'tag' THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'account_credential_rewrap_content_changed';
        END IF;

        IF (
            pg_catalog.to_jsonb(NEW)
                - 'credential_envelope'
                - 'credential_revision'
        ) IS DISTINCT FROM (
            pg_catalog.to_jsonb(OLD)
                - 'credential_envelope'
                - 'credential_revision'
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'account_credential_rewrap_scope_invalid';
        END IF;
    ELSIF NEW.version <> OLD.version + 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'account_credential_replacement_version_invalid';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_accounts_credential_revision
BEFORE UPDATE OF credential_envelope, credential_revision
ON public.accounts
FOR EACH ROW EXECUTE FUNCTION public.poolai_guard_account_credential_revision();

CREATE FUNCTION public.poolai_supply_create_account(
    p_account_id uuid,
    p_provider text,
    p_name text,
    p_upstream_base_url text,
    p_credential_envelope jsonb,
    p_credential_prefix text,
    p_credential_hint text,
    p_max_concurrency integer,
    p_priority integer,
    p_weight integer
)
RETURNS TABLE(
    disposition text,
    current_version bigint,
    current_credential_revision bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_inserted integer;
    v_now timestamptz;
BEGIN
    IF p_account_id IS NULL
        OR p_provider IS NULL
        OR p_provider NOT IN ('openai', 'openai_compatible')
        OR p_name IS NULL
        OR pg_catalog.btrim(p_name) = ''
        OR pg_catalog.char_length(p_name) > 100
        OR p_upstream_base_url IS NULL
        OR NOT (
            p_upstream_base_url ~ '^https://[^[:space:]]+$'
            OR p_upstream_base_url
                ~ '^http://(localhost|127\.0\.0\.1|\[::1\])([:/][^[:space:]]*)?$'
        )
        OR p_credential_envelope IS NULL
        OR NOT public.poolai_secret_envelope_v1_is_structurally_valid(
            p_credential_envelope)
        OR p_credential_prefix IS NULL
        OR pg_catalog.btrim(p_credential_prefix) = ''
        OR pg_catalog.char_length(p_credential_prefix) > 32
        OR (
            p_credential_hint IS NOT NULL
            AND (
                pg_catalog.btrim(p_credential_hint) = ''
                OR pg_catalog.char_length(p_credential_hint) > 128
            )
        )
        OR p_max_concurrency IS NULL
        OR p_max_concurrency NOT BETWEEN 1 AND 10000
        OR p_priority IS NULL
        OR p_priority NOT BETWEEN -100000 AND 100000
        OR p_weight IS NULL
        OR p_weight NOT BETWEEN 1 AND 100000 THEN
        RETURN QUERY SELECT
            'validation_failed'::text, NULL::bigint, NULL::bigint;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    INSERT INTO public.accounts (
        id, provider, name, auth_type, upstream_base_url,
        credential_envelope, credential_prefix, credential_hint,
        status, priority, weight, max_concurrency,
        last_health_status, version, created_at, updated_at,
        credential_revision
    ) VALUES (
        p_account_id, p_provider, p_name, 'api_key', p_upstream_base_url,
        p_credential_envelope, p_credential_prefix, p_credential_hint,
        'disabled', p_priority, p_weight, p_max_concurrency,
        'unknown', 1, v_now, v_now, 1
    )
    ON CONFLICT (id) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'conflict'::text, NULL::bigint, NULL::bigint;
        RETURN;
    END IF;

    RETURN QUERY SELECT 'created'::text, 1::bigint, 1::bigint;
END;
$function$;

CREATE FUNCTION public.poolai_supply_replace_account_credential(
    p_account_id uuid,
    p_expected_version bigint,
    p_credential_envelope jsonb,
    p_credential_prefix text,
    p_credential_hint text
)
RETURNS TABLE(
    disposition text,
    current_version bigint,
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
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_credential_envelope IS NULL
        OR NOT public.poolai_secret_envelope_v1_is_structurally_valid(
            p_credential_envelope)
        OR p_credential_prefix IS NULL
        OR pg_catalog.btrim(p_credential_prefix) = ''
        OR pg_catalog.char_length(p_credential_prefix) > 32
        OR (
            p_credential_hint IS NOT NULL
            AND (
                pg_catalog.btrim(p_credential_hint) = ''
                OR pg_catalog.char_length(p_credential_hint) > 128
            )
        ) THEN
        RETURN QUERY SELECT
            'validation_failed'::text, NULL::bigint, NULL::bigint;
        RETURN;
    END IF;

    SELECT
        account.status,
        account.version,
        account.deleted_at,
        account.credential_revision
    INTO v_account
    FROM public.accounts AS account
    WHERE account.id = p_account_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, NULL::bigint, NULL::bigint;
        RETURN;
    END IF;
    IF v_account.status = 'retired' OR v_account.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'account_retired'::text,
            v_account.version,
            v_account.credential_revision;
        RETURN;
    END IF;
    IF v_account.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            v_account.version,
            v_account.credential_revision;
        RETURN;
    END IF;

    -- This clock sample is intentionally after the Account row-lock wait.
    v_now := pg_catalog.clock_timestamp();
    UPDATE public.accounts AS account
    SET credential_envelope = p_credential_envelope,
        credential_prefix = p_credential_prefix,
        credential_hint = p_credential_hint,
        credential_revision = v_account.credential_revision + 1,
        upstream_rate_limited_until = NULL,
        last_health_at = NULL,
        last_health_status = 'unknown',
        version = v_account.version + 1,
        updated_at = v_now
    WHERE account.id = p_account_id;

    RETURN QUERY SELECT
        'replaced'::text,
        v_account.version + 1,
        v_account.credential_revision + 1;
END;
$function$;

CREATE FUNCTION public.poolai_supply_select_account_credential_rewrap_batch(
    p_after_account_id uuid,
    p_batch_size integer
)
RETURNS TABLE(
    account_id uuid,
    revision bigint,
    envelope jsonb
)
LANGUAGE plpgsql
STABLE
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF p_batch_size IS NULL OR p_batch_size NOT BETWEEN 1 AND 1000 THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'account_credential_rewrap_batch_invalid';
    END IF;

    RETURN QUERY
    SELECT
        account.id,
        account.credential_revision,
        account.credential_envelope
    FROM public.accounts AS account
    WHERE p_after_account_id IS NULL
       OR account.id > p_after_account_id
    ORDER BY account.id
    LIMIT p_batch_size;
END;
$function$;

CREATE FUNCTION public.poolai_supply_rewrap_account_credential(
    p_account_id uuid,
    p_expected_credential_revision bigint,
    p_rewrapped_envelope jsonb
)
RETURNS TABLE(
    disposition text,
    current_credential_revision bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_account record;
    v_error_message text;
BEGIN
    IF p_account_id IS NULL
        OR p_expected_credential_revision IS NULL
        OR p_expected_credential_revision <= 0
        OR p_rewrapped_envelope IS NULL
        OR NOT public.poolai_secret_envelope_v1_is_structurally_valid(
            p_rewrapped_envelope) THEN
        RETURN QUERY SELECT 'validation_failed'::text, NULL::bigint;
        RETURN;
    END IF;

    SELECT account.credential_revision
    INTO v_account
    FROM public.accounts AS account
    WHERE account.id = p_account_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::bigint;
        RETURN;
    END IF;
    IF v_account.credential_revision <> p_expected_credential_revision THEN
        RETURN QUERY SELECT
            'credential_revision_conflict'::text,
            v_account.credential_revision;
        RETURN;
    END IF;

    BEGIN
        UPDATE public.accounts AS account
        SET credential_envelope = p_rewrapped_envelope,
            credential_revision = v_account.credential_revision + 1
        WHERE account.id = p_account_id;
    EXCEPTION
        WHEN SQLSTATE 'P0001' THEN
            GET STACKED DIAGNOSTICS v_error_message = MESSAGE_TEXT;
            IF v_error_message IN (
                'account_credential_revision_without_envelope_change',
                'account_credential_rewrap_content_changed',
                'account_credential_rewrap_scope_invalid'
            ) THEN
                RETURN QUERY SELECT
                    'content_mismatch'::text,
                    v_account.credential_revision;
                RETURN;
            END IF;
            RAISE;
    END;

    RETURN QUERY SELECT
        'rewrapped'::text,
        v_account.credential_revision + 1;
END;
$function$;

-- The function owner can create or replace a credential without reading it.
-- The existing UPDATE(id) grant remains the key-only row-lock capability used
-- by quota and these Supply entry points.
GRANT SELECT (version, credential_revision)
    ON public.accounts TO poolai_runtime_owner;
GRANT INSERT (
    id, provider, name, auth_type, upstream_base_url,
    credential_envelope, credential_prefix, credential_hint,
    status, priority, weight, max_concurrency,
    last_health_status, version, created_at, updated_at,
    credential_revision
) ON public.accounts TO poolai_runtime_owner;
GRANT UPDATE (
    credential_envelope, credential_prefix, credential_hint,
    upstream_rate_limited_until, last_health_at, last_health_status,
    version, updated_at, credential_revision
) ON public.accounts TO poolai_runtime_owner;

-- Worker already reads id and credential_envelope for Supply Health. The
-- credential-only revision is its sole additional direct read.
GRANT SELECT (credential_revision) ON public.accounts TO poolai_worker;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_guard_group_supply_configuration()
    SECURITY DEFINER;
ALTER FUNCTION public.poolai_guard_group_supply_configuration()
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_validate_group_account_binding()
    SECURITY DEFINER;
ALTER FUNCTION public.poolai_validate_group_account_binding()
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_secret_envelope_v1_is_structurally_valid(jsonb)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_guard_account_credential_revision()
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_create_account(
    uuid, text, text, text, jsonb, text, text,
    integer, integer, integer
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_replace_account_credential(
    uuid, bigint, jsonb, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_select_account_credential_rewrap_batch(
    uuid, integer
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_rewrap_account_credential(
    uuid, bigint, jsonb
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION
    public.poolai_guard_group_supply_configuration()
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION
    public.poolai_validate_group_account_binding()
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION
    public.poolai_secret_envelope_v1_is_structurally_valid(jsonb)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION
    public.poolai_guard_account_credential_revision()
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_create_account(
    uuid, text, text, text, jsonb, text, text,
    integer, integer, integer
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_replace_account_credential(
    uuid, bigint, jsonb, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION
    public.poolai_supply_select_account_credential_rewrap_batch(uuid, integer)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_rewrap_account_credential(
    uuid, bigint, jsonb
) FROM PUBLIC, poolai_api, poolai_worker;

GRANT EXECUTE ON FUNCTION public.poolai_supply_create_account(
    uuid, text, text, text, jsonb, text, text,
    integer, integer, integer
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_replace_account_credential(
    uuid, bigint, jsonb, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION
    public.poolai_supply_select_account_credential_rewrap_batch(uuid, integer)
    TO poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_supply_rewrap_account_credential(
    uuid, bigint, jsonb
) TO poolai_worker;
RESET ROLE;

-- Fail closed on DML, credential-read, owner, search-path, trigger or EXECUTE
-- drift. This audit deliberately allows poolai_api to keep its existing
-- credential SELECT for the co-hosted Gateway use case.
DO $permission_audit$
DECLARE
    v_api_role_oid oid;
    v_function_oid oid;
    v_function_signature text;
    v_insert_columns text[];
    v_select_columns text[];
    v_update_columns text[];
    v_worker_role_oid oid;
BEGIN
    SELECT role.oid
    INTO v_api_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';
    SELECT role.oid
    INTO v_worker_role_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_worker';
    IF v_api_role_oid IS NULL OR v_worker_role_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_runtime_role_missing';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles AS role
        WHERE role.rolname IN (
            'poolai_runtime_owner', 'poolai_api', 'poolai_worker')
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
            WHERE role.rolname = 'poolai_runtime_owner'
              AND NOT role.rolcanlogin
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_runtime_role_attributes_forbidden';
    END IF;

    IF pg_catalog.pg_has_role(
            'poolai_api', 'poolai_runtime_owner', 'MEMBER')
        OR pg_catalog.pg_has_role(
            'poolai_worker', 'poolai_runtime_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_runtime_role_membership_forbidden';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_runtime_schema_create_forbidden';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.accounts', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'UPDATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_direct_account_write_forbidden';
    END IF;

    IF pg_catalog.has_column_privilege(
            'poolai_runtime_owner',
            'public.accounts',
            'credential_envelope',
            'SELECT')
        OR NOT pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'credential_envelope',
            'SELECT')
        OR NOT pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'credential_revision',
            'SELECT')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'credential_envelope',
            'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.accounts',
            'credential_revision',
            'UPDATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_credential_column_boundary_missing';
    END IF;

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_select_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.accounts'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.accounts',
          attribute.attname,
          'SELECT');

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_insert_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.accounts'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.accounts',
          attribute.attname,
          'INSERT');

    SELECT COALESCE(
               pg_catalog.array_agg(
                   attribute.attname::text ORDER BY attribute.attnum),
               ARRAY[]::text[])
    INTO v_update_columns
    FROM pg_catalog.pg_attribute AS attribute
    WHERE attribute.attrelid = 'public.accounts'::regclass
      AND attribute.attnum > 0
      AND NOT attribute.attisdropped
      AND pg_catalog.has_column_privilege(
          'poolai_runtime_owner',
          'public.accounts',
          attribute.attname,
          'UPDATE');

    IF v_select_columns IS DISTINCT FROM ARRAY[
            'id', 'provider', 'status', 'upstream_rate_limited_until',
            'last_health_status', 'version', 'deleted_at',
            'credential_revision'
        ]::text[]
        OR v_insert_columns IS DISTINCT FROM ARRAY[
            'id', 'provider', 'name', 'auth_type', 'upstream_base_url',
            'credential_envelope', 'credential_prefix', 'credential_hint',
            'status', 'priority', 'weight', 'max_concurrency',
            'last_health_status', 'version', 'created_at', 'updated_at',
            'credential_revision'
        ]::text[]
        OR v_update_columns IS DISTINCT FROM ARRAY[
            'id', 'credential_envelope', 'credential_prefix',
            'credential_hint', 'upstream_rate_limited_until',
            'last_health_at', 'last_health_status', 'version', 'updated_at',
            'credential_revision'
        ]::text[]
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.accounts', 'SELECT')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.accounts', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_runtime_owner', 'public.accounts', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.accounts',
            'SELECT WITH GRANT OPTION')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.accounts',
            'INSERT WITH GRANT OPTION')
        OR pg_catalog.has_any_column_privilege(
            'poolai_runtime_owner', 'public.accounts',
            'UPDATE WITH GRANT OPTION') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_runtime_owner_column_boundary_missing';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = ANY (ARRAY[
              'poolai_supply_create_account',
              'poolai_supply_replace_account_credential',
              'poolai_supply_select_account_credential_rewrap_batch',
              'poolai_supply_rewrap_account_credential'
          ])
    ) <> 4 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_entry_point_overload_forbidden';
    END IF;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_supply_create_account(uuid,text,text,text,jsonb,text,text,integer,integer,integer)',
        'public.poolai_supply_replace_account_credential(uuid,bigint,jsonb,text,text)'
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
                      pg_catalog.acldefault('f', procedure.proowner))) AS acl
                  WHERE acl.privilege_type = 'EXECUTE'
                    AND (
                        acl.grantor <> procedure.proowner
                        OR acl.grantee NOT IN (
                            procedure.proowner, v_api_role_oid
                        )
                        OR (
                            acl.grantee = v_api_role_oid
                            AND acl.is_grantable
                        )
                    )
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m2_e1_api_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_supply_select_account_credential_rewrap_batch(uuid,integer)',
        'public.poolai_supply_rewrap_account_credential(uuid,bigint,jsonb)'
    ]
    LOOP
        v_function_oid := pg_catalog.to_regprocedure(v_function_signature);
        IF v_function_oid IS NULL OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = procedure.proowner
            WHERE procedure.oid = v_function_oid
              AND procedure.prosecdef = (
                  procedure.proname =
                      'poolai_supply_rewrap_account_credential')
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND procedure.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND pg_catalog.has_function_privilege(
                  'poolai_worker', procedure.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_api', procedure.oid, 'EXECUTE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      procedure.proacl,
                      pg_catalog.acldefault('f', procedure.proowner))) AS acl
                  WHERE acl.privilege_type = 'EXECUTE'
                    AND (
                        acl.grantor <> procedure.proowner
                        OR acl.grantee NOT IN (
                            procedure.proowner, v_worker_role_oid
                        )
                        OR (
                            acl.grantee = v_worker_role_oid
                            AND acl.is_grantable
                        )
                    )
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m2_e1_worker_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_trigger AS trigger
        JOIN pg_catalog.pg_proc AS procedure
          ON procedure.oid = trigger.tgfoid
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE trigger.tgrelid = 'public.accounts'::regclass
          AND trigger.tgname = 'tr_accounts_credential_revision'
          AND NOT trigger.tgisinternal
          AND procedure.proname =
              'poolai_guard_account_credential_revision'
          AND procedure.prosecdef
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND procedure.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_credential_trigger_boundary_missing';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS function
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = function.proowner
        WHERE function.oid IN (
                  'public.poolai_guard_group_supply_configuration()'::regprocedure,
                  'public.poolai_validate_group_account_binding()'::regprocedure
              )
          AND function.prosecdef
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND function.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
          AND NOT pg_catalog.has_function_privilege(
              'poolai_api', function.oid, 'EXECUTE')
          AND NOT pg_catalog.has_function_privilege(
              'poolai_worker', function.oid, 'EXECUTE')
    ) <> 2 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_supply_guard_boundary_missing';
    END IF;

    v_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_secret_envelope_v1_is_structurally_valid(jsonb)');
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
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e1_envelope_validator_boundary_missing';
    END IF;
END;
$permission_audit$;
