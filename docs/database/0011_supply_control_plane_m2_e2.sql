-- PoolAI Release 1 M2-E2 Supply control-plane boundary.
--
-- This forward-only migration keeps 0001-0010 byte immutable. It closes the
-- Account Base URL grammar approved by ADR 0010, adds narrow Channel/Account/
-- Group Supply Configuration mutation entry points, and exposes one redacted
-- database readiness observation. It performs no data repair and no network,
-- DNS, redirect, credential-decryption, or upstream operation.

CREATE FUNCTION public.poolai_supply_base_url_is_valid(p_value text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
    SELECT
        pg_catalog.char_length(p_value) <= 2048
        AND p_value ~ '^(?:https://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:/[^\s?#]*)?$';
$function$;

CREATE FUNCTION public.poolai_supply_model_rules_are_valid(p_model_rules jsonb)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_key text;
    v_value text;
BEGIN
    IF pg_catalog.jsonb_typeof(p_model_rules) <> 'object'
        OR p_model_rules = '{}'::jsonb THEN
        RETURN false;
    END IF;

    FOR v_key IN
        SELECT rule.key
        FROM pg_catalog.jsonb_each(p_model_rules) AS rule
    LOOP
        IF pg_catalog.jsonb_typeof(p_model_rules -> v_key) <> 'string' THEN
            RETURN false;
        END IF;
        v_value := p_model_rules ->> v_key;
        IF pg_catalog.char_length(v_key) NOT BETWEEN 1 AND 200
            OR pg_catalog.char_length(v_value) NOT BETWEEN 1 AND 200 THEN
            RETURN false;
        END IF;
    END LOOP;

    RETURN true;
END;
$function$;

CREATE FUNCTION public.poolai_supply_capabilities_are_valid(p_capabilities jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
    SELECT
        pg_catalog.jsonb_typeof(p_capabilities) = 'object'
        AND p_capabilities ?& ARRAY[
            'responses', 'chat_completions', 'function_tools', 'streaming'
        ]::text[]
        AND p_capabilities - ARRAY[
            'responses', 'chat_completions', 'function_tools', 'streaming'
        ]::text[] = '{}'::jsonb
        AND pg_catalog.jsonb_typeof(p_capabilities -> 'responses') = 'boolean'
        AND pg_catalog.jsonb_typeof(
            p_capabilities -> 'chat_completions') = 'boolean'
        AND pg_catalog.jsonb_typeof(
            p_capabilities -> 'function_tools') = 'boolean'
        AND pg_catalog.jsonb_typeof(p_capabilities -> 'streaming') = 'boolean';
$function$;

CREATE FUNCTION public.poolai_supply_binding_arrays_are_valid(
    p_account_ids uuid[],
    p_priority_overrides integer[],
    p_weight_overrides integer[],
    p_enabled boolean[]
)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_cardinality integer;
    v_index integer;
    v_unique_count integer;
BEGIN
    IF p_account_ids IS NULL
        OR p_priority_overrides IS NULL
        OR p_weight_overrides IS NULL
        OR p_enabled IS NULL THEN
        RETURN false;
    END IF;

    v_cardinality := pg_catalog.cardinality(p_account_ids);
    IF pg_catalog.cardinality(p_priority_overrides) <> v_cardinality
        OR pg_catalog.cardinality(p_weight_overrides) <> v_cardinality
        OR pg_catalog.cardinality(p_enabled) <> v_cardinality
        OR COALESCE(pg_catalog.array_ndims(p_account_ids), 1) <> 1
        OR COALESCE(
            pg_catalog.array_ndims(p_priority_overrides), 1) <> 1
        OR COALESCE(
            pg_catalog.array_ndims(p_weight_overrides), 1) <> 1
        OR COALESCE(pg_catalog.array_ndims(p_enabled), 1) <> 1
        OR COALESCE(
            pg_catalog.array_lower(p_account_ids, 1), 1) <> 1
        OR COALESCE(
            pg_catalog.array_lower(p_priority_overrides, 1), 1) <> 1
        OR COALESCE(
            pg_catalog.array_lower(p_weight_overrides, 1), 1) <> 1
        OR COALESCE(
            pg_catalog.array_lower(p_enabled, 1), 1) <> 1
        OR pg_catalog.array_position(p_account_ids, NULL) IS NOT NULL
        OR pg_catalog.array_position(p_enabled, NULL) IS NOT NULL THEN
        RETURN false;
    END IF;

    SELECT pg_catalog.count(DISTINCT account_id)::integer
    INTO v_unique_count
    FROM pg_catalog.unnest(p_account_ids) AS account_id;
    IF v_unique_count <> v_cardinality THEN
        RETURN false;
    END IF;

    FOR v_index IN 1..v_cardinality
    LOOP
        IF (
                p_priority_overrides[v_index] IS NOT NULL
                AND p_priority_overrides[v_index]
                    NOT BETWEEN -100000 AND 100000
            )
            OR (
                p_weight_overrides[v_index] IS NOT NULL
                AND p_weight_overrides[v_index]
                    NOT BETWEEN 1 AND 100000
            ) THEN
            RETURN false;
        END IF;
    END LOOP;

    RETURN true;
END;
$function$;

-- Adding validated constraints is also the read-only preflight. Any legacy row
-- outside the approved grammar aborts this migration atomically; no row is
-- trimmed, normalized, disabled, retired, or otherwise repaired here.
ALTER TABLE public.accounts
    DROP CONSTRAINT ck_accounts_base_url,
    DROP CONSTRAINT ck_accounts_name,
    DROP CONSTRAINT ck_accounts_max_concurrency,
    DROP CONSTRAINT ck_accounts_deleted_status,
    ADD CONSTRAINT ck_accounts_base_url CHECK (
        public.poolai_supply_base_url_is_valid(upstream_base_url)
    ),
    ADD CONSTRAINT ck_accounts_name CHECK (
        pg_catalog.btrim(name) <> ''
        AND pg_catalog.char_length(name) <= 100
    ),
    ADD CONSTRAINT ck_accounts_max_concurrency CHECK (
        max_concurrency BETWEEN 1 AND 10000
    ),
    ADD CONSTRAINT ck_accounts_deleted_status CHECK (
        (status = 'retired') = (deleted_at IS NOT NULL)
    );

ALTER TABLE public.channels
    DROP CONSTRAINT ck_channels_name,
    DROP CONSTRAINT ck_channels_model_rules,
    DROP CONSTRAINT ck_channels_capabilities,
    DROP CONSTRAINT ck_channels_deleted_status,
    ADD CONSTRAINT ck_channels_name CHECK (
        pg_catalog.btrim(name) <> ''
        AND pg_catalog.char_length(name) <= 100
    ),
    ADD CONSTRAINT ck_channels_model_rules CHECK (
        public.poolai_supply_model_rules_are_valid(model_rules)
    ),
    ADD CONSTRAINT ck_channels_capabilities CHECK (
        public.poolai_supply_capabilities_are_valid(capabilities)
    ),
    ADD CONSTRAINT ck_channels_deleted_status CHECK (
        (status = 'retired') = (deleted_at IS NOT NULL)
    );

CREATE INDEX ix_accounts_created_id
    ON public.accounts(created_at DESC, id DESC);
CREATE INDEX ix_channels_created_id
    ON public.channels(created_at DESC, id DESC);

-- Rebuild the signed M2-E1 create ABI in place so direct database callers get
-- the same validation_failed outcome as the public Application boundary.
GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
SET LOCAL ROLE poolai_runtime_owner;
CREATE OR REPLACE FUNCTION public.poolai_supply_create_account(
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
        OR NOT public.poolai_supply_base_url_is_valid(p_upstream_base_url)
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
RESET ROLE;

CREATE FUNCTION public.poolai_supply_update_account(
    p_account_id uuid,
    p_expected_version bigint,
    p_name_specified boolean,
    p_name text,
    p_base_url_specified boolean,
    p_upstream_base_url text,
    p_credential_specified boolean,
    p_credential_envelope jsonb,
    p_credential_prefix text,
    p_credential_hint text,
    p_status_specified boolean,
    p_status text,
    p_max_concurrency_specified boolean,
    p_max_concurrency integer,
    p_priority_specified boolean,
    p_priority integer,
    p_weight_specified boolean,
    p_weight integer,
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
    v_account record;
    v_before jsonb;
    v_changed boolean;
    v_now timestamptz;
BEGIN
    IF p_account_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_name_specified IS NULL
        OR p_base_url_specified IS NULL
        OR p_credential_specified IS NULL
        OR p_status_specified IS NULL
        OR p_max_concurrency_specified IS NULL
        OR p_priority_specified IS NULL
        OR p_weight_specified IS NULL
        OR NOT (
            p_name_specified
            OR p_base_url_specified
            OR p_credential_specified
            OR p_status_specified
            OR p_max_concurrency_specified
            OR p_priority_specified
            OR p_weight_specified
        )
        OR (
            p_name_specified
            AND (
                p_name IS NULL
                OR pg_catalog.btrim(p_name) = ''
                OR pg_catalog.char_length(p_name) > 100
            )
        )
        OR (NOT p_name_specified AND p_name IS NOT NULL)
        OR (
            p_base_url_specified
            AND (
                p_upstream_base_url IS NULL
                OR NOT public.poolai_supply_base_url_is_valid(
                    p_upstream_base_url)
            )
        )
        OR (
            NOT p_base_url_specified
            AND p_upstream_base_url IS NOT NULL
        )
        OR (
            p_credential_specified
            AND (
                p_credential_envelope IS NULL
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
            )
        )
        OR (
            NOT p_credential_specified
            AND (
                p_credential_envelope IS NOT NULL
                OR p_credential_prefix IS NOT NULL
                OR p_credential_hint IS NOT NULL
            )
        )
        OR (
            p_status_specified
            AND (
                p_status IS NULL
                OR p_status NOT IN ('active', 'disabled')
            )
        )
        OR (NOT p_status_specified AND p_status IS NOT NULL)
        OR (
            p_max_concurrency_specified
            AND (
                p_max_concurrency IS NULL
                OR p_max_concurrency NOT BETWEEN 1 AND 10000
            )
        )
        OR (
            NOT p_max_concurrency_specified
            AND p_max_concurrency IS NOT NULL
        )
        OR (
            p_priority_specified
            AND (
                p_priority IS NULL
                OR p_priority NOT BETWEEN -100000 AND 100000
            )
        )
        OR (NOT p_priority_specified AND p_priority IS NOT NULL)
        OR (
            p_weight_specified
            AND (
                p_weight IS NULL
                OR p_weight NOT BETWEEN 1 AND 100000
            )
        )
        OR (NOT p_weight_specified AND p_weight IS NOT NULL)
        OR (
            (p_credential_specified OR p_status_specified)
            AND (
                p_reason IS NULL
                OR pg_catalog.btrim(p_reason) = ''
                OR pg_catalog.char_length(p_reason) > 500
            )
        )
        OR (
            p_reason IS NOT NULL
            AND (
                pg_catalog.btrim(p_reason) = ''
                OR pg_catalog.char_length(p_reason) > 500
            )
        ) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT
        account.id,
        account.provider,
        account.name,
        account.upstream_base_url,
        account.credential_prefix,
        account.status,
        account.upstream_rate_limited_until,
        account.last_health_at,
        account.last_health_status,
        account.max_concurrency,
        account.priority,
        account.weight,
        account.version,
        account.credential_revision,
        account.created_at,
        account.updated_at,
        account.deleted_at
    INTO v_account
    FROM public.accounts AS account
    WHERE account.id = p_account_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_before := pg_catalog.jsonb_build_object(
        'id', v_account.id,
        'provider', v_account.provider,
        'name', v_account.name,
        'upstream_base_url', v_account.upstream_base_url,
        'credential_prefix', v_account.credential_prefix,
        'status', v_account.status,
        'last_health_status', v_account.last_health_status,
        'upstream_rate_limited_until',
            v_account.upstream_rate_limited_until,
        'last_health_at', v_account.last_health_at,
        'max_concurrency', v_account.max_concurrency,
        'priority', v_account.priority,
        'weight', v_account.weight,
        'version', v_account.version,
        'created_at', v_account.created_at,
        'updated_at', v_account.updated_at
    );

    IF v_account.status = 'retired'
        OR v_account.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'account_retired'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;
    IF v_account.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;

    v_changed :=
        (p_name_specified AND p_name IS DISTINCT FROM v_account.name)
        OR (
            p_base_url_specified
            AND p_upstream_base_url
                IS DISTINCT FROM v_account.upstream_base_url
        )
        OR p_credential_specified
        OR (
            p_status_specified
            AND p_status IS DISTINCT FROM v_account.status
        )
        OR (
            p_max_concurrency_specified
            AND p_max_concurrency
                IS DISTINCT FROM v_account.max_concurrency
        )
        OR (
            p_priority_specified
            AND p_priority IS DISTINCT FROM v_account.priority
        )
        OR (
            p_weight_specified
            AND p_weight IS DISTINCT FROM v_account.weight
        );

    IF NOT v_changed THEN
        RETURN QUERY SELECT
            'updated'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;

    -- Sample the persisted timestamp only after the Account row-lock wait.
    v_now := pg_catalog.clock_timestamp();
    -- Keep the non-credential branch free of any credential_envelope
    -- expression. PostgreSQL checks column privileges statically for an
    -- UPDATE statement, even when a CASE branch is unreachable; the function
    -- owner intentionally cannot SELECT credential material.
    IF p_credential_specified THEN
        UPDATE public.accounts AS account
        SET name = CASE
                WHEN p_name_specified THEN p_name
                ELSE account.name
            END,
            upstream_base_url = CASE
                WHEN p_base_url_specified THEN p_upstream_base_url
                ELSE account.upstream_base_url
            END,
            credential_envelope = p_credential_envelope,
            credential_prefix = p_credential_prefix,
            credential_hint = p_credential_hint,
            credential_revision = v_account.credential_revision + 1,
            status = CASE
                WHEN p_status_specified THEN p_status
                ELSE account.status
            END,
            max_concurrency = CASE
                WHEN p_max_concurrency_specified THEN p_max_concurrency
                ELSE account.max_concurrency
            END,
            priority = CASE
                WHEN p_priority_specified THEN p_priority
                ELSE account.priority
            END,
            weight = CASE
                WHEN p_weight_specified THEN p_weight
                ELSE account.weight
            END,
            upstream_rate_limited_until = NULL,
            last_health_at = NULL,
            last_health_status = 'unknown',
            version = v_account.version + 1,
            updated_at = v_now
        WHERE account.id = p_account_id;
    ELSE
        UPDATE public.accounts AS account
        SET name = CASE
                WHEN p_name_specified THEN p_name
                ELSE account.name
            END,
            upstream_base_url = CASE
                WHEN p_base_url_specified THEN p_upstream_base_url
                ELSE account.upstream_base_url
            END,
            status = CASE
                WHEN p_status_specified THEN p_status
                ELSE account.status
            END,
            max_concurrency = CASE
                WHEN p_max_concurrency_specified THEN p_max_concurrency
                ELSE account.max_concurrency
            END,
            priority = CASE
                WHEN p_priority_specified THEN p_priority
                ELSE account.priority
            END,
            weight = CASE
                WHEN p_weight_specified THEN p_weight
                ELSE account.weight
            END,
            upstream_rate_limited_until = CASE
                WHEN p_base_url_specified
                    AND p_upstream_base_url
                        IS DISTINCT FROM v_account.upstream_base_url
                    THEN NULL
                ELSE account.upstream_rate_limited_until
            END,
            last_health_at = CASE
                WHEN p_base_url_specified
                    AND p_upstream_base_url
                        IS DISTINCT FROM v_account.upstream_base_url
                    THEN NULL
                ELSE account.last_health_at
            END,
            last_health_status = CASE
                WHEN p_base_url_specified
                    AND p_upstream_base_url
                        IS DISTINCT FROM v_account.upstream_base_url
                    THEN 'unknown'
                ELSE account.last_health_status
            END,
            version = v_account.version + 1,
            updated_at = v_now
        WHERE account.id = p_account_id;
    END IF;

    RETURN QUERY SELECT
        'updated'::text,
        true,
        v_before,
        v_account.version + 1;
END;
$function$;

CREATE FUNCTION public.poolai_supply_retire_account(
    p_account_id uuid,
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
    v_account record;
    v_before jsonb;
    v_now timestamptz;
BEGIN
    IF p_account_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_reason IS NULL
        OR pg_catalog.btrim(p_reason) = ''
        OR pg_catalog.char_length(p_reason) > 500 THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    -- Retirement always locks the Account before inspecting enabled bindings.
    -- Binding create/re-enable takes FOR SHARE on this same Account, so both
    -- commit orders are deterministic without locking a child row here.
    SELECT
        account.id,
        account.provider,
        account.name,
        account.upstream_base_url,
        account.credential_prefix,
        account.status,
        account.upstream_rate_limited_until,
        account.last_health_at,
        account.last_health_status,
        account.max_concurrency,
        account.priority,
        account.weight,
        account.version,
        account.created_at,
        account.updated_at,
        account.deleted_at
    INTO v_account
    FROM public.accounts AS account
    WHERE account.id = p_account_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_before := pg_catalog.jsonb_build_object(
        'id', v_account.id,
        'provider', v_account.provider,
        'name', v_account.name,
        'upstream_base_url', v_account.upstream_base_url,
        'credential_prefix', v_account.credential_prefix,
        'status', v_account.status,
        'last_health_status', v_account.last_health_status,
        'upstream_rate_limited_until',
            v_account.upstream_rate_limited_until,
        'last_health_at', v_account.last_health_at,
        'max_concurrency', v_account.max_concurrency,
        'priority', v_account.priority,
        'weight', v_account.weight,
        'version', v_account.version,
        'created_at', v_account.created_at,
        'updated_at', v_account.updated_at
    );

    IF v_account.status = 'retired'
        OR v_account.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'account_retired'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;
    IF v_account.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.group_accounts AS binding
        WHERE binding.account_id = p_account_id
          AND binding.is_enabled
    ) THEN
        RETURN QUERY SELECT
            'account_in_use'::text,
            false,
            v_before,
            v_account.version;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    UPDATE public.accounts AS account
    SET status = 'retired',
        deleted_at = v_now,
        version = v_account.version + 1,
        updated_at = v_now
    WHERE account.id = p_account_id;

    RETURN QUERY SELECT
        'retired'::text,
        true,
        v_before,
        v_account.version + 1;
END;
$function$;

CREATE FUNCTION public.poolai_supply_create_channel(
    p_channel_id uuid,
    p_provider text,
    p_name text,
    p_model_rules jsonb,
    p_capabilities jsonb
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
    v_inserted integer;
    v_now timestamptz;
BEGIN
    IF p_channel_id IS NULL
        OR p_provider IS NULL
        OR p_provider NOT IN ('openai', 'openai_compatible')
        OR p_name IS NULL
        OR pg_catalog.btrim(p_name) = ''
        OR pg_catalog.char_length(p_name) > 100
        OR p_model_rules IS NULL
        OR NOT public.poolai_supply_model_rules_are_valid(p_model_rules)
        OR p_capabilities IS NULL
        OR NOT public.poolai_supply_capabilities_are_valid(
            p_capabilities) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    INSERT INTO public.channels (
        id, provider, name, model_rules, capabilities,
        status, version, created_at, updated_at
    ) VALUES (
        p_channel_id, p_provider, p_name, p_model_rules, p_capabilities,
        'disabled', 1, v_now, v_now
    )
    ON CONFLICT (id) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'conflict'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    RETURN QUERY SELECT
        'created'::text, true, NULL::jsonb, 1::bigint;
END;
$function$;

CREATE FUNCTION public.poolai_supply_update_channel(
    p_channel_id uuid,
    p_expected_version bigint,
    p_name_specified boolean,
    p_name text,
    p_status_specified boolean,
    p_status text,
    p_model_rules_specified boolean,
    p_model_rules jsonb,
    p_capabilities_specified boolean,
    p_capabilities jsonb,
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
    v_before jsonb;
    v_changed boolean;
    v_channel record;
    v_now timestamptz;
BEGIN
    IF p_channel_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_name_specified IS NULL
        OR p_status_specified IS NULL
        OR p_model_rules_specified IS NULL
        OR p_capabilities_specified IS NULL
        OR NOT (
            p_name_specified
            OR p_status_specified
            OR p_model_rules_specified
            OR p_capabilities_specified
        )
        OR (
            p_name_specified
            AND (
                p_name IS NULL
                OR pg_catalog.btrim(p_name) = ''
                OR pg_catalog.char_length(p_name) > 100
            )
        )
        OR (NOT p_name_specified AND p_name IS NOT NULL)
        OR (
            p_status_specified
            AND (
                p_status IS NULL
                OR p_status NOT IN ('active', 'disabled')
            )
        )
        OR (NOT p_status_specified AND p_status IS NOT NULL)
        OR (
            p_model_rules_specified
            AND (
                p_model_rules IS NULL
                OR NOT public.poolai_supply_model_rules_are_valid(
                    p_model_rules)
            )
        )
        OR (
            NOT p_model_rules_specified
            AND p_model_rules IS NOT NULL
        )
        OR (
            p_capabilities_specified
            AND (
                p_capabilities IS NULL
                OR NOT public.poolai_supply_capabilities_are_valid(
                    p_capabilities)
            )
        )
        OR (
            NOT p_capabilities_specified
            AND p_capabilities IS NOT NULL
        )
        OR (
            p_status_specified
            AND (
                p_reason IS NULL
                OR pg_catalog.btrim(p_reason) = ''
                OR pg_catalog.char_length(p_reason) > 500
            )
        )
        OR (
            p_reason IS NOT NULL
            AND (
                pg_catalog.btrim(p_reason) = ''
                OR pg_catalog.char_length(p_reason) > 500
            )
        ) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT
        channel.id,
        channel.provider,
        channel.name,
        channel.model_rules,
        channel.capabilities,
        channel.status,
        channel.version,
        channel.created_at,
        channel.updated_at,
        channel.deleted_at
    INTO v_channel
    FROM public.channels AS channel
    WHERE channel.id = p_channel_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_before := pg_catalog.jsonb_build_object(
        'id', v_channel.id,
        'provider', v_channel.provider,
        'name', v_channel.name,
        'model_rules', v_channel.model_rules,
        'capabilities', v_channel.capabilities,
        'status', v_channel.status,
        'version', v_channel.version,
        'created_at', v_channel.created_at,
        'updated_at', v_channel.updated_at
    );

    IF v_channel.status = 'retired'
        OR v_channel.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'channel_retired'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;
    IF v_channel.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;

    v_changed :=
        (p_name_specified AND p_name IS DISTINCT FROM v_channel.name)
        OR (
            p_status_specified
            AND p_status IS DISTINCT FROM v_channel.status
        )
        OR (
            p_model_rules_specified
            AND p_model_rules IS DISTINCT FROM v_channel.model_rules
        )
        OR (
            p_capabilities_specified
            AND p_capabilities IS DISTINCT FROM v_channel.capabilities
        );

    IF NOT v_changed THEN
        RETURN QUERY SELECT
            'updated'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    UPDATE public.channels AS channel
    SET name = CASE
            WHEN p_name_specified THEN p_name
            ELSE channel.name
        END,
        status = CASE
            WHEN p_status_specified THEN p_status
            ELSE channel.status
        END,
        model_rules = CASE
            WHEN p_model_rules_specified THEN p_model_rules
            ELSE channel.model_rules
        END,
        capabilities = CASE
            WHEN p_capabilities_specified THEN p_capabilities
            ELSE channel.capabilities
        END,
        version = v_channel.version + 1,
        updated_at = v_now
    WHERE channel.id = p_channel_id;

    RETURN QUERY SELECT
        'updated'::text,
        true,
        v_before,
        v_channel.version + 1;
END;
$function$;

CREATE FUNCTION public.poolai_supply_retire_channel(
    p_channel_id uuid,
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
    v_before jsonb;
    v_channel record;
    v_now timestamptz;
BEGIN
    IF p_channel_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_reason IS NULL
        OR pg_catalog.btrim(p_reason) = ''
        OR pg_catalog.char_length(p_reason) > 500 THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT
        channel.id,
        channel.provider,
        channel.name,
        channel.model_rules,
        channel.capabilities,
        channel.status,
        channel.version,
        channel.created_at,
        channel.updated_at,
        channel.deleted_at
    INTO v_channel
    FROM public.channels AS channel
    WHERE channel.id = p_channel_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_before := pg_catalog.jsonb_build_object(
        'id', v_channel.id,
        'provider', v_channel.provider,
        'name', v_channel.name,
        'model_rules', v_channel.model_rules,
        'capabilities', v_channel.capabilities,
        'status', v_channel.status,
        'version', v_channel.version,
        'created_at', v_channel.created_at,
        'updated_at', v_channel.updated_at
    );

    IF v_channel.status = 'retired'
        OR v_channel.deleted_at IS NOT NULL THEN
        RETURN QUERY SELECT
            'channel_retired'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;
    IF v_channel.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.group_supply_configurations AS configuration
        WHERE configuration.channel_id = p_channel_id
    ) THEN
        RETURN QUERY SELECT
            'channel_in_use'::text,
            false,
            v_before,
            v_channel.version;
        RETURN;
    END IF;

    v_now := pg_catalog.clock_timestamp();
    UPDATE public.channels AS channel
    SET status = 'retired',
        deleted_at = v_now,
        version = v_channel.version + 1,
        updated_at = v_now
    WHERE channel.id = p_channel_id;

    RETURN QUERY SELECT
        'retired'::text,
        true,
        v_before,
        v_channel.version + 1;
END;
$function$;

CREATE FUNCTION public.poolai_supply_create_group_configuration(
    p_group_id uuid,
    p_channel_id uuid,
    p_account_ids uuid[],
    p_priority_overrides integer[],
    p_weight_overrides integer[],
    p_enabled boolean[]
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
    v_account_count integer;
    v_account_id uuid;
    v_channel_deleted_at timestamptz;
    v_channel_provider text;
    v_channel_status text;
    v_index integer;
    v_inserted integer;
    v_version bigint;
BEGIN
    IF p_group_id IS NULL
        OR NOT public.poolai_supply_binding_arrays_are_valid(
            p_account_ids,
            p_priority_overrides,
            p_weight_overrides,
            p_enabled) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    IF p_channel_id IS NOT NULL THEN
        SELECT channel.provider, channel.status, channel.deleted_at
        INTO v_channel_provider, v_channel_status, v_channel_deleted_at
        FROM public.channels AS channel
        WHERE channel.id = p_channel_id
        FOR SHARE;
        IF NOT FOUND
            OR v_channel_status = 'retired'
            OR v_channel_deleted_at IS NOT NULL THEN
            RETURN QUERY SELECT
                'validation_failed'::text,
                false,
                NULL::jsonb,
                NULL::bigint;
            RETURN;
        END IF;
    END IF;

    -- UUID order is the single lock order for every multi-Account command.
    PERFORM account.id
    FROM public.accounts AS account
    WHERE account.id = ANY(p_account_ids)
    ORDER BY account.id
    FOR SHARE;

    SELECT pg_catalog.count(*)::integer
    INTO v_account_count
    FROM public.accounts AS account
    WHERE account.id = ANY(p_account_ids)
      AND account.status <> 'retired'
      AND account.deleted_at IS NULL
      AND (
          p_channel_id IS NULL
          OR account.provider = v_channel_provider
      );
    IF v_account_count <> pg_catalog.cardinality(p_account_ids) THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    INSERT INTO public.group_supply_configurations (
        group_id, channel_id
    ) VALUES (
        p_group_id, p_channel_id
    )
    ON CONFLICT (group_id) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'conflict'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    FOR v_account_id IN
        SELECT pg_catalog.unnest(p_account_ids)
        ORDER BY 1
    LOOP
        v_index := pg_catalog.array_position(p_account_ids, v_account_id);
        INSERT INTO public.group_accounts (
            group_id,
            account_id,
            priority_override,
            weight_override,
            is_enabled
        ) VALUES (
            p_group_id,
            v_account_id,
            p_priority_overrides[v_index],
            p_weight_overrides[v_index],
            p_enabled[v_index]
        );
    END LOOP;

    SELECT configuration.version
    INTO v_version
    FROM public.group_supply_configurations AS configuration
    WHERE configuration.group_id = p_group_id;

    RETURN QUERY SELECT
        'created'::text, true, NULL::jsonb, v_version;
END;
$function$;

CREATE FUNCTION public.poolai_supply_patch_group_configuration(
    p_group_id uuid,
    p_expected_version bigint,
    p_channel_specified boolean,
    p_channel_id uuid,
    p_bindings_specified boolean,
    p_account_ids uuid[],
    p_priority_overrides integer[],
    p_weight_overrides integer[],
    p_enabled boolean[],
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
    v_account_count integer;
    v_account_id uuid;
    v_before jsonb;
    v_bindings jsonb;
    v_changed boolean := false;
    v_channel_deleted_at timestamptz;
    v_channel_provider text;
    v_channel_status text;
    v_configuration record;
    v_effective_channel_id uuid;
    v_index integer;
    v_now timestamptz;
    v_row_count integer;
    v_version bigint;
BEGIN
    IF p_group_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_channel_specified IS NULL
        OR p_bindings_specified IS NULL
        OR NOT (p_channel_specified OR p_bindings_specified)
        OR (
            NOT p_channel_specified
            AND p_channel_id IS NOT NULL
        )
        OR (
            p_bindings_specified
            AND NOT public.poolai_supply_binding_arrays_are_valid(
                p_account_ids,
                p_priority_overrides,
                p_weight_overrides,
                p_enabled)
        )
        OR (
            NOT p_bindings_specified
            AND (
                p_account_ids IS NOT NULL
                OR p_priority_overrides IS NOT NULL
                OR p_weight_overrides IS NOT NULL
                OR p_enabled IS NOT NULL
            )
        )
        OR p_reason IS NULL
        OR pg_catalog.btrim(p_reason) = ''
        OR pg_catalog.char_length(p_reason) > 500 THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    -- The Aggregate root is always the first row lock for an existing
    -- Configuration command. Every sanctioned binding writer follows it.
    SELECT
        configuration.group_id,
        configuration.channel_id,
        configuration.version,
        configuration.created_at,
        configuration.updated_at
    INTO v_configuration
    FROM public.group_supply_configurations AS configuration
    WHERE configuration.group_id = p_group_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT COALESCE(
        pg_catalog.jsonb_agg(
            pg_catalog.jsonb_build_object(
                'account_id', binding.account_id,
                'priority_override', binding.priority_override,
                'weight_override', binding.weight_override,
                'enabled', binding.is_enabled
            )
            ORDER BY binding.account_id
        ),
        '[]'::jsonb
    )
    INTO v_bindings
    FROM public.group_accounts AS binding
    WHERE binding.group_id = p_group_id;

    v_before := pg_catalog.jsonb_build_object(
        'group_id', v_configuration.group_id,
        'channel_id', v_configuration.channel_id,
        'account_bindings', v_bindings,
        'version', v_configuration.version,
        'created_at', v_configuration.created_at,
        'updated_at', v_configuration.updated_at
    );

    IF v_configuration.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text,
            false,
            v_before,
            v_configuration.version;
        RETURN;
    END IF;

    v_effective_channel_id := CASE
        WHEN p_channel_specified THEN p_channel_id
        ELSE v_configuration.channel_id
    END;

    IF v_effective_channel_id IS NOT NULL THEN
        SELECT channel.provider, channel.status, channel.deleted_at
        INTO v_channel_provider, v_channel_status, v_channel_deleted_at
        FROM public.channels AS channel
        WHERE channel.id = v_effective_channel_id
        FOR SHARE;
        IF NOT FOUND
            OR v_channel_status = 'retired'
            OR v_channel_deleted_at IS NOT NULL THEN
            RETURN QUERY SELECT
                'validation_failed'::text,
                false,
                v_before,
                v_configuration.version;
            RETURN;
        END IF;
    END IF;

    IF p_bindings_specified THEN
        PERFORM account.id
        FROM public.accounts AS account
        WHERE account.id = ANY(p_account_ids)
        ORDER BY account.id
        FOR SHARE;

        SELECT pg_catalog.count(*)::integer
        INTO v_account_count
        FROM public.accounts AS account
        WHERE account.id = ANY(p_account_ids)
          AND (
              (
                  account.status <> 'retired'
                  AND account.deleted_at IS NULL
                  AND (
                      v_effective_channel_id IS NULL
                      OR account.provider = v_channel_provider
                  )
              )
              OR (
                  NOT p_enabled[
                      pg_catalog.array_position(
                          p_account_ids, account.id)
                  ]
                  AND EXISTS (
                      SELECT 1
                      FROM public.group_accounts AS existing_binding
                      WHERE existing_binding.group_id = p_group_id
                        AND existing_binding.account_id = account.id
                  )
              )
          );
        IF v_account_count <> pg_catalog.cardinality(p_account_ids) THEN
            RETURN QUERY SELECT
                'validation_failed'::text,
                false,
                v_before,
                v_configuration.version;
            RETURN;
        END IF;
    ELSIF p_channel_specified
        AND p_channel_id IS DISTINCT FROM v_configuration.channel_id
        AND v_effective_channel_id IS NOT NULL THEN
        -- A channel-only switch is allowed only if every retained enabled
        -- binding already matches. Lock Accounts in UUID order before checking.
        PERFORM account.id
        FROM public.group_accounts AS binding
        JOIN public.accounts AS account
          ON account.id = binding.account_id
        WHERE binding.group_id = p_group_id
          AND binding.is_enabled
        ORDER BY account.id
        FOR SHARE OF binding, account;

        IF EXISTS (
            SELECT 1
            FROM public.group_accounts AS binding
            JOIN public.accounts AS account
              ON account.id = binding.account_id
            WHERE binding.group_id = p_group_id
              AND binding.is_enabled
              AND (
                  account.status = 'retired'
                  OR account.deleted_at IS NOT NULL
                  OR account.provider <> v_channel_provider
              )
        ) THEN
            RETURN QUERY SELECT
                'validation_failed'::text,
                false,
                v_before,
                v_configuration.version;
            RETURN;
        END IF;
    END IF;

    v_now := pg_catalog.clock_timestamp();

    -- If both Channel and bindings change, first disable retained enabled rows.
    -- This makes provider A -> B a single atomic command while preserving the
    -- immutable binding history and the existing provider guard.
    IF p_bindings_specified
        AND p_channel_specified
        AND p_channel_id IS DISTINCT FROM v_configuration.channel_id THEN
        FOR v_account_id IN
            SELECT binding.account_id
            FROM public.group_accounts AS binding
            WHERE binding.group_id = p_group_id
              AND binding.is_enabled
            ORDER BY binding.account_id
        LOOP
            UPDATE public.group_accounts AS binding
            SET is_enabled = false,
                updated_at = v_now
            WHERE binding.group_id = p_group_id
              AND binding.account_id = v_account_id
              AND binding.is_enabled;
            GET DIAGNOSTICS v_row_count = ROW_COUNT;
            v_changed := v_changed OR v_row_count > 0;
        END LOOP;
    END IF;

    IF p_channel_specified
        AND p_channel_id IS DISTINCT FROM v_configuration.channel_id THEN
        UPDATE public.group_supply_configurations AS configuration
        SET channel_id = p_channel_id,
            version = configuration.version + 1
        WHERE configuration.group_id = p_group_id;
        v_changed := true;
    END IF;

    IF p_bindings_specified THEN
        -- Omitted bindings are retained as disabled history, never deleted.
        FOR v_account_id IN
            SELECT binding.account_id
            FROM public.group_accounts AS binding
            WHERE binding.group_id = p_group_id
              AND binding.is_enabled
              AND NOT binding.account_id = ANY(p_account_ids)
            ORDER BY binding.account_id
        LOOP
            UPDATE public.group_accounts AS binding
            SET is_enabled = false,
                updated_at = v_now
            WHERE binding.group_id = p_group_id
              AND binding.account_id = v_account_id
              AND binding.is_enabled;
            GET DIAGNOSTICS v_row_count = ROW_COUNT;
            v_changed := v_changed OR v_row_count > 0;
        END LOOP;

        FOR v_account_id IN
            SELECT pg_catalog.unnest(p_account_ids)
            ORDER BY 1
        LOOP
            v_index := pg_catalog.array_position(
                p_account_ids, v_account_id);
            INSERT INTO public.group_accounts AS binding (
                group_id,
                account_id,
                priority_override,
                weight_override,
                is_enabled
            ) VALUES (
                p_group_id,
                v_account_id,
                p_priority_overrides[v_index],
                p_weight_overrides[v_index],
                p_enabled[v_index]
            )
            ON CONFLICT (group_id, account_id) DO UPDATE
            SET priority_override = EXCLUDED.priority_override,
                weight_override = EXCLUDED.weight_override,
                is_enabled = EXCLUDED.is_enabled,
                updated_at = v_now
            WHERE binding.priority_override
                    IS DISTINCT FROM EXCLUDED.priority_override
               OR binding.weight_override
                    IS DISTINCT FROM EXCLUDED.weight_override
               OR binding.is_enabled
                    IS DISTINCT FROM EXCLUDED.is_enabled;
            GET DIAGNOSTICS v_row_count = ROW_COUNT;
            v_changed := v_changed OR v_row_count > 0;
        END LOOP;
    END IF;

    SELECT configuration.version
    INTO v_version
    FROM public.group_supply_configurations AS configuration
    WHERE configuration.group_id = p_group_id;

    RETURN QUERY SELECT
        'updated'::text,
        v_changed,
        v_before,
        v_version;
END;
$function$;

CREATE FUNCTION public.poolai_supply_observe_group_readiness(
    p_group_id uuid
)
RETURNS TABLE(
    disposition text,
    configuration_version bigint,
    observed_at timestamptz,
    canonical_snapshot jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_configuration_version bigint;
    v_disposition text;
    v_observed_at timestamptz;
    v_snapshot jsonb;
BEGIN
    IF p_group_id IS NULL THEN
        RETURN QUERY SELECT
            'not_found'::text,
            NULL::bigint,
            pg_catalog.clock_timestamp(),
            NULL::jsonb;
        RETURN;
    END IF;

    -- One SELECT supplies one MVCC statement snapshot and one materialized
    -- database clock. The JSON intentionally excludes credential material,
    -- credential metadata, Base URL, Account name, and settings.
    SELECT
        configuration.version,
        observation.observed_at,
        CASE
            WHEN channel.id IS NOT NULL
                AND channel.status = 'active'
                AND channel.deleted_at IS NULL
                AND COALESCE(
                    pg_catalog.bool_or(
                        binding.is_enabled
                        AND account.status = 'active'
                        AND account.deleted_at IS NULL
                        AND account.provider = channel.provider
                        AND account.last_health_status
                            IN ('healthy', 'degraded')
                        AND (
                            account.upstream_rate_limited_until IS NULL
                            OR account.upstream_rate_limited_until
                                <= observation.observed_at
                        )
                    ),
                    false
                )
                THEN 'ready'
            ELSE 'not_ready'
        END,
        pg_catalog.jsonb_build_object(
            'v', 1,
            'group_id', configuration.group_id,
            'configuration_version', configuration.version,
            'channel', CASE
                WHEN channel.id IS NULL THEN NULL::jsonb
                ELSE pg_catalog.jsonb_build_object(
                    'id', channel.id,
                    'provider', channel.provider,
                    'status', channel.status,
                    'deleted', channel.deleted_at IS NOT NULL,
                    'version', channel.version,
                    'model_rules', channel.model_rules,
                    'capabilities', channel.capabilities
                )
            END,
            'bindings', COALESCE(
                pg_catalog.jsonb_agg(
                    pg_catalog.jsonb_build_object(
                        'account_id', binding.account_id,
                        'enabled', binding.is_enabled,
                        'priority_override', binding.priority_override,
                        'weight_override', binding.weight_override,
                        'account_provider', account.provider,
                        'account_status', account.status,
                        'account_deleted', account.deleted_at IS NOT NULL,
                        'account_version', account.version,
                        'health_status', account.last_health_status,
                        'cooldown_until',
                            account.upstream_rate_limited_until,
                        'eligible',
                            binding.is_enabled
                            AND channel.id IS NOT NULL
                            AND channel.status = 'active'
                            AND channel.deleted_at IS NULL
                            AND account.status = 'active'
                            AND account.deleted_at IS NULL
                            AND account.provider = channel.provider
                            AND account.last_health_status
                                IN ('healthy', 'degraded')
                            AND (
                                account.upstream_rate_limited_until IS NULL
                                OR account.upstream_rate_limited_until
                                    <= observation.observed_at
                            )
                    )
                    ORDER BY binding.account_id
                ) FILTER (WHERE binding.account_id IS NOT NULL),
                '[]'::jsonb
            ),
            'ready',
                channel.id IS NOT NULL
                AND channel.status = 'active'
                AND channel.deleted_at IS NULL
                AND COALESCE(
                    pg_catalog.bool_or(
                        binding.is_enabled
                        AND account.status = 'active'
                        AND account.deleted_at IS NULL
                        AND account.provider = channel.provider
                        AND account.last_health_status
                            IN ('healthy', 'degraded')
                        AND (
                            account.upstream_rate_limited_until IS NULL
                            OR account.upstream_rate_limited_until
                                <= observation.observed_at
                        )
                    ),
                    false
                )
        )
    INTO
        v_configuration_version,
        v_observed_at,
        v_disposition,
        v_snapshot
    FROM (
        SELECT pg_catalog.clock_timestamp() AS observed_at
    ) AS observation
    JOIN public.group_supply_configurations AS configuration
      ON configuration.group_id = p_group_id
    LEFT JOIN public.channels AS channel
      ON channel.id = configuration.channel_id
    LEFT JOIN public.group_accounts AS binding
      ON binding.group_id = configuration.group_id
    LEFT JOIN public.accounts AS account
      ON account.id = binding.account_id
    GROUP BY
        configuration.group_id,
        configuration.version,
        channel.id,
        channel.provider,
        channel.status,
        channel.deleted_at,
        channel.version,
        channel.model_rules,
        channel.capabilities,
        observation.observed_at;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'not_found'::text,
            NULL::bigint,
            pg_catalog.clock_timestamp(),
            NULL::jsonb;
        RETURN;
    END IF;

    RETURN QUERY SELECT
        v_disposition,
        v_configuration_version,
        v_observed_at,
        v_snapshot;
END;
$function$;

-- Direct API writes are replaced by the bounded functions above. Reads remain
-- separately granted for the Admin query surfaces and the co-hosted Gateway.
REVOKE INSERT, UPDATE ON public.channels FROM poolai_api;
REVOKE INSERT (group_id, channel_id)
    ON public.group_supply_configurations FROM poolai_api;
REVOKE UPDATE (channel_id, version, updated_at)
    ON public.group_supply_configurations FROM poolai_api;
REVOKE INSERT (
    group_id, account_id, priority_override, weight_override, is_enabled
) ON public.group_accounts FROM poolai_api;
REVOKE UPDATE (
    priority_override, weight_override, is_enabled, updated_at
) ON public.group_accounts FROM poolai_api;

-- Exact runtime-owner capabilities. No DELETE, credential-envelope SELECT,
-- table-wide DML, schema CREATE after transfer, or grant option is introduced.
GRANT SELECT (
    name, auth_type, upstream_base_url, credential_prefix,
    last_health_at, max_concurrency, priority, weight, created_at, updated_at
) ON public.accounts TO poolai_runtime_owner;
GRANT UPDATE (
    name, upstream_base_url, status, max_concurrency,
    priority, weight, deleted_at
) ON public.accounts TO poolai_runtime_owner;

GRANT SELECT (
    name, model_rules, capabilities, version, created_at, updated_at
) ON public.channels TO poolai_runtime_owner;
GRANT INSERT (
    id, provider, name, model_rules, capabilities,
    status, version, created_at, updated_at
) ON public.channels TO poolai_runtime_owner;
GRANT UPDATE (
    name, model_rules, capabilities, status, version, updated_at, deleted_at
) ON public.channels TO poolai_runtime_owner;

GRANT SELECT (created_at, updated_at)
    ON public.group_supply_configurations TO poolai_runtime_owner;
GRANT INSERT (group_id, channel_id)
    ON public.group_supply_configurations TO poolai_runtime_owner;
GRANT UPDATE (channel_id, version, updated_at)
    ON public.group_supply_configurations TO poolai_runtime_owner;

GRANT SELECT (
    priority_override, weight_override, created_at, updated_at
) ON public.group_accounts TO poolai_runtime_owner;
GRANT INSERT (
    group_id, account_id, priority_override, weight_override, is_enabled
) ON public.group_accounts TO poolai_runtime_owner;
GRANT UPDATE (
    priority_override, weight_override, is_enabled, updated_at
) ON public.group_accounts TO poolai_runtime_owner;

ALTER FUNCTION public.poolai_supply_base_url_is_valid(text)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_model_rules_are_valid(jsonb)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_capabilities_are_valid(jsonb)
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_binding_arrays_are_valid(
    uuid[], integer[], integer[], boolean[]
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_update_account(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb, text, text,
    boolean, text, boolean, integer, boolean, integer, boolean, integer, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_retire_account(
    uuid, bigint, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_create_channel(
    uuid, text, text, jsonb, jsonb
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_update_channel(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb,
    boolean, jsonb, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_retire_channel(
    uuid, bigint, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_create_group_configuration(
    uuid, uuid, uuid[], integer[], integer[], boolean[]
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_patch_group_configuration(
    uuid, bigint, boolean, uuid, boolean,
    uuid[], integer[], integer[], boolean[], text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_supply_observe_group_readiness(uuid)
    OWNER TO poolai_runtime_owner;

ALTER FUNCTION public.poolai_reject_supply_provider_change()
    SECURITY DEFINER;
ALTER FUNCTION public.poolai_reject_supply_provider_change()
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_guard_supply_retirement()
    SECURITY DEFINER;
ALTER FUNCTION public.poolai_guard_supply_retirement()
    OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_bump_group_supply_configuration_version()
    SECURITY DEFINER;
ALTER FUNCTION public.poolai_bump_group_supply_configuration_version()
    OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_supply_base_url_is_valid(text)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_model_rules_are_valid(jsonb)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_capabilities_are_valid(jsonb)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_binding_arrays_are_valid(
    uuid[], integer[], integer[], boolean[]
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_update_account(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb, text, text,
    boolean, text, boolean, integer, boolean, integer, boolean, integer, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_retire_account(
    uuid, bigint, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_create_channel(
    uuid, text, text, jsonb, jsonb
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_update_channel(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb,
    boolean, jsonb, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_retire_channel(
    uuid, bigint, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_create_group_configuration(
    uuid, uuid, uuid[], integer[], integer[], boolean[]
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_patch_group_configuration(
    uuid, bigint, boolean, uuid, boolean,
    uuid[], integer[], integer[], boolean[], text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_supply_observe_group_readiness(uuid)
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_reject_supply_provider_change()
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_guard_supply_retirement()
    FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION
    public.poolai_bump_group_supply_configuration_version()
    FROM PUBLIC, poolai_api, poolai_worker;

-- Account health updates re-evaluate the Account CHECK constraint, so Worker
-- receives EXECUTE only on the pure immutable Base URL predicate.
GRANT EXECUTE ON FUNCTION public.poolai_supply_base_url_is_valid(text)
    TO poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_supply_create_account(
    uuid, text, text, text, jsonb, text, text,
    integer, integer, integer
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_update_account(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb, text, text,
    boolean, text, boolean, integer, boolean, integer, boolean, integer, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_retire_account(
    uuid, bigint, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_create_channel(
    uuid, text, text, jsonb, jsonb
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_update_channel(
    uuid, bigint, boolean, text, boolean, text, boolean, jsonb,
    boolean, jsonb, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_retire_channel(
    uuid, bigint, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_create_group_configuration(
    uuid, uuid, uuid[], integer[], integer[], boolean[]
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_patch_group_configuration(
    uuid, bigint, boolean, uuid, boolean,
    uuid[], integer[], integer[], boolean[], text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_supply_observe_group_readiness(uuid)
    TO poolai_api;
RESET ROLE;

-- Fail closed if role, direct-DML, owner/search-path, helper or EXECUTE
-- boundaries drift from the M2-E2 contract.
DO $permission_audit$
DECLARE
    v_api_oid oid;
    v_function_oid oid;
    v_signature text;
BEGIN
    SELECT role.oid
    INTO v_api_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';
    IF v_api_oid IS NULL OR NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles AS role
        WHERE role.rolname = 'poolai_worker'
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e2_runtime_role_missing';
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
            MESSAGE = 'poolai_m2_e2_role_boundary_missing';
    END IF;

    IF pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.accounts', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.channels', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.channels', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api',
            'public.group_supply_configurations',
            'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api',
            'public.group_supply_configurations',
            'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.group_accounts', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.group_accounts', 'UPDATE')
        OR pg_catalog.has_column_privilege(
            'poolai_runtime_owner',
            'public.accounts',
            'credential_envelope',
            'SELECT') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e2_direct_dml_boundary_missing';
    END IF;

    FOREACH v_signature IN ARRAY ARRAY[
        'public.poolai_supply_update_account(uuid,bigint,boolean,text,boolean,text,boolean,jsonb,text,text,boolean,text,boolean,integer,boolean,integer,boolean,integer,text)',
        'public.poolai_supply_retire_account(uuid,bigint,text)',
        'public.poolai_supply_create_channel(uuid,text,text,jsonb,jsonb)',
        'public.poolai_supply_update_channel(uuid,bigint,boolean,text,boolean,text,boolean,jsonb,boolean,jsonb,text)',
        'public.poolai_supply_retire_channel(uuid,bigint,text)',
        'public.poolai_supply_create_group_configuration(uuid,uuid,uuid[],integer[],integer[],boolean[])',
        'public.poolai_supply_patch_group_configuration(uuid,bigint,boolean,uuid,boolean,uuid[],integer[],integer[],boolean[],text)',
        'public.poolai_supply_observe_group_readiness(uuid)'
    ]
    LOOP
        v_function_oid := pg_catalog.to_regprocedure(v_signature);
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
                      pg_catalog.acldefault(
                          'f', procedure.proowner))) AS acl
                  WHERE acl.privilege_type = 'EXECUTE'
                    AND (
                        acl.grantor <> procedure.proowner
                        OR acl.is_grantable
                        OR acl.grantee NOT IN (
                            procedure.proowner, v_api_oid
                        )
                    )
              )
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_m2_e2_api_entry_point_boundary_missing',
                DETAIL = v_signature;
        END IF;
    END LOOP;

    IF NOT pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_supply_base_url_is_valid(text)',
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_supply_base_url_is_valid(text)',
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_supply_model_rules_are_valid(jsonb)',
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_supply_capabilities_are_valid(jsonb)',
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_supply_binding_arrays_are_valid(uuid[],integer[],integer[],boolean[])',
            'EXECUTE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m2_e2_helper_boundary_missing';
    END IF;
END;
$permission_audit$;
