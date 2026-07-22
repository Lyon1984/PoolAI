-- PoolAI Release 1 M1-E4 Group, Template, and canonical Subscription increment.
--
-- The shared API database role must not be able to bypass lifecycle, optimistic
-- concurrency, database-clock, or archive predicates with ad-hoc table writes.
-- All M1-E4 writes therefore enter through fixed-search-path SECURITY DEFINER
-- functions owned by the existing NOLOGIN runtime owner.

REVOKE INSERT, UPDATE ON public.groups FROM poolai_api;
REVOKE INSERT, UPDATE ON public.subscription_templates FROM poolai_api;
REVOKE INSERT, UPDATE ON public.subscriptions FROM poolai_api;

ALTER TABLE public.groups
    ADD CONSTRAINT ck_groups_m1_e4_name_length CHECK (char_length(name) <= 100),
    ADD CONSTRAINT ck_groups_m1_e4_description_length CHECK (
        description IS NULL OR char_length(description) <= 1000
    ),
    ADD CONSTRAINT ck_groups_m1_e4_activation_observed_finite CHECK (
        activation_supply_observed_at IS NULL
        OR isfinite(activation_supply_observed_at)
    ),
    ADD CONSTRAINT ck_groups_m1_e4_archive_marker CHECK (
        (status = 'archived' AND deleted_at IS NOT NULL)
        OR (status <> 'archived' AND deleted_at IS NULL)
    );

ALTER TABLE public.subscription_templates
    ADD CONSTRAINT ck_subscription_templates_m1_e4_name_length CHECK (
        char_length(name) <= 100
    ),
    ADD CONSTRAINT ck_subscription_templates_m1_e4_description_length CHECK (
        description IS NULL OR char_length(description) <= 1000
    ),
    ADD CONSTRAINT ck_subscription_templates_m1_e4_retirement_marker CHECK (
        (status = 'retired' AND deleted_at IS NOT NULL)
        OR (status <> 'retired' AND deleted_at IS NULL)
    );

ALTER TABLE public.subscriptions
    ADD CONSTRAINT ck_subscriptions_m1_e4_snapshot_length CHECK (
        char_length(template_name_snapshot) <= 100
    ),
    ADD CONSTRAINT ck_subscriptions_m1_e4_reason_length CHECK (
        char_length(change_reason) <= 500
    ),
    ADD CONSTRAINT ck_subscriptions_m1_e4_time_finite CHECK (
        isfinite(starts_at) AND isfinite(expires_at)
    );

-- Keyset indexes match the control-plane order `(created_at DESC, id DESC)`.
CREATE INDEX ix_groups_created_id
    ON public.groups(created_at DESC, id DESC);
CREATE INDEX ix_subscription_templates_created_id
    ON public.subscription_templates(created_at DESC, id DESC);
CREATE INDEX ix_subscriptions_created_id
    ON public.subscriptions(created_at DESC, id DESC);
CREATE INDEX ix_subscriptions_user_created_id
    ON public.subscriptions(user_id, created_at DESC, id DESC);
CREATE INDEX ix_subscriptions_group_created_id
    ON public.subscriptions(group_id, created_at DESC, id DESC);

-- Archive checks must not scan all historical grants or reservations.
CREATE INDEX ix_subscriptions_group_active_expiry
    ON public.subscriptions(group_id, expires_at, id)
    WHERE status = 'active';
CREATE INDEX ix_group_token_reservations_group_pending
    ON public.group_token_reservations(group_id, id)
    WHERE status = 'pending';

CREATE OR REPLACE FUNCTION public.poolai_group_create(
    p_group_id uuid,
    p_name text,
    p_description text,
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
        OR p_reason IS NULL
        OR btrim(p_reason) = ''
        OR char_length(p_reason) > 500
        OR p_quota_idempotency_key IS NULL
        OR btrim(p_quota_idempotency_key) = '' THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    INSERT INTO public.groups (
        id, name, description, status, version, created_at, updated_at
    ) VALUES (
        p_group_id, p_name, p_description, 'disabled', 1, v_now, v_now
    )
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT 'conflict'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- Reuse the already-approved quota state machine. Any failure rolls the
    -- Group insert back with the same surrounding application Unit of Work.
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

CREATE OR REPLACE FUNCTION public.poolai_group_update(
    p_group_id uuid,
    p_expected_version bigint,
    p_set_name boolean,
    p_name text,
    p_set_description boolean,
    p_description text,
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
        OR (NOT p_set_name AND p_name IS NOT NULL)
        OR (p_set_name AND (
            p_name IS NULL OR btrim(p_name) = '' OR char_length(p_name) > 100
        ))
        OR (NOT p_set_description AND p_description IS NOT NULL)
        OR (p_set_description AND p_description IS NOT NULL
            AND char_length(p_description) > 1000)
        OR (p_status IS NOT NULL AND p_status NOT IN ('active', 'disabled', 'archived'))
        OR (p_status IS NOT NULL AND (
            p_reason IS NULL OR btrim(p_reason) = '' OR char_length(p_reason) > 500
        ))
        OR ((p_supply_readiness_token IS NULL)
            <> (p_supply_observed_at IS NULL))
        OR (p_supply_observed_at IS NOT NULL
            AND NOT isfinite(p_supply_observed_at)) THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- Quota reserve/reset/adjust use the quota row as their first lock. Archive
    -- must take the same first lock before it can lock Group or inspect pending
    -- reservations; doing this from an ordinary Group UPDATE trigger would
    -- invert the order and create a deadlock cycle.
    IF p_status = 'archived' THEN
        PERFORM quota.group_id
        FROM public.group_token_quotas AS quota
        WHERE quota.group_id = p_group_id
        FOR UPDATE;
    END IF;

    SELECT current_group.id,
           current_group.name,
           current_group.description,
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
    -- This single observation is taken after the Group lock (and the Quota
    -- lock for archive) and drives audit, archive, and update timestamps.
    v_now := clock_timestamp();
    v_before_state := jsonb_build_object(
        'id', v_group.id,
        'name', v_group.name,
        'description', v_group.description,
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
    v_deleted_at := CASE WHEN v_status = 'archived' THEN v_now ELSE NULL END;
    v_changed := v_name IS DISTINCT FROM v_group.name
        OR v_description IS DISTINCT FROM v_group.description
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

CREATE OR REPLACE FUNCTION public.poolai_subscription_template_create(
    p_template_id uuid,
    p_group_id uuid,
    p_name text,
    p_description text,
    p_default_duration_days integer
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
    v_group_status text;
    v_now timestamptz;
    v_inserted integer;
BEGIN
    IF p_template_id IS NULL
        OR p_group_id IS NULL
        OR p_name IS NULL
        OR btrim(p_name) = ''
        OR char_length(p_name) > 100
        OR (p_description IS NOT NULL AND char_length(p_description) > 1000)
        OR p_default_duration_days IS NULL
        OR p_default_duration_days NOT BETWEEN 1 AND 3650 THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_group.status
    INTO v_group_status
    FROM public.groups AS current_group
    WHERE current_group.id = p_group_id
    FOR SHARE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    IF v_group_status = 'archived' THEN
        RETURN QUERY SELECT 'group_archived'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    INSERT INTO public.subscription_templates (
        id, group_id, name, description, default_duration_days,
        status, version, created_at, updated_at
    ) VALUES (
        p_template_id, p_group_id, p_name, p_description, p_default_duration_days,
        'active', 1, v_now, v_now
    )
    ON CONFLICT DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    RETURN QUERY SELECT
        CASE WHEN v_inserted = 1 THEN 'created' ELSE 'conflict' END::text,
        v_inserted = 1,
        NULL::jsonb,
        CASE WHEN v_inserted = 1 THEN 1::bigint ELSE NULL::bigint END;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_subscription_template_update(
    p_template_id uuid,
    p_expected_version bigint,
    p_set_name boolean,
    p_name text,
    p_set_description boolean,
    p_description text,
    p_set_duration boolean,
    p_default_duration_days integer,
    p_status text,
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
    v_template public.subscription_templates%ROWTYPE;
    v_group_id uuid;
    v_group_status text;
    v_name text;
    v_description text;
    v_duration integer;
    v_status text;
    v_now timestamptz;
    v_changed boolean;
    v_before_state jsonb;
BEGIN
    IF p_template_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_set_name IS NULL
        OR p_set_description IS NULL
        OR p_set_duration IS NULL
        OR (NOT p_set_name AND p_name IS NOT NULL)
        OR (p_set_name AND (
            p_name IS NULL OR btrim(p_name) = '' OR char_length(p_name) > 100
        ))
        OR (NOT p_set_description AND p_description IS NOT NULL)
        OR (p_set_description AND p_description IS NOT NULL
            AND char_length(p_description) > 1000)
        OR (NOT p_set_duration AND p_default_duration_days IS NOT NULL)
        OR (p_set_duration AND (
            p_default_duration_days IS NULL
            OR p_default_duration_days NOT BETWEEN 1 AND 3650
        ))
        OR (p_status IS NOT NULL AND p_status NOT IN ('active', 'disabled'))
        OR (p_status IS NOT NULL AND (
            p_reason IS NULL OR btrim(p_reason) = '' OR char_length(p_reason) > 500
        )) THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT candidate.group_id
    INTO v_group_id
    FROM public.subscription_templates AS candidate
    WHERE candidate.id = p_template_id;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- All Template and SubscriptionAccess writes use Group -> Template ->
    -- Subscription order. Template update takes the conflicting Group lock
    -- before its target Template so two same-Group cross-renames cannot each
    -- hold one Template row while waiting on the other's unique-index tuple.
    -- Group archive therefore fences new writes without a lock-order cycle.
    SELECT current_group.status
    INTO v_group_status
    FROM public.groups AS current_group
    WHERE current_group.id = v_group_id
    FOR UPDATE;

    IF NOT FOUND OR v_group_status = 'archived' THEN
        RETURN QUERY SELECT 'group_archived'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_template.*
    INTO v_template
    FROM public.subscription_templates AS current_template
    WHERE current_template.id = p_template_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    v_before_state := jsonb_build_object(
        'id', v_template.id,
        'group_id', v_template.group_id,
        'name', v_template.name,
        'description', v_template.description,
        'default_duration_days', v_template.default_duration_days,
        'status', v_template.status,
        'version', v_template.version,
        'created_at', v_template.created_at,
        'updated_at', v_template.updated_at
    );
    IF v_template.group_id <> v_group_id THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_template.version;
        RETURN;
    END IF;
    IF v_template.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_template.version;
        RETURN;
    END IF;
    IF v_template.status = 'retired' THEN
        RETURN QUERY SELECT
            'invalid_transition'::text, false, v_before_state, v_template.version;
        RETURN;
    END IF;

    v_name := CASE WHEN p_set_name THEN p_name ELSE v_template.name END;
    v_description := CASE
        WHEN p_set_description THEN p_description
        ELSE v_template.description
    END;
    v_duration := CASE
        WHEN p_set_duration THEN p_default_duration_days
        ELSE v_template.default_duration_days
    END;
    v_status := coalesce(p_status, v_template.status);
    v_changed := v_name IS DISTINCT FROM v_template.name
        OR v_description IS DISTINCT FROM v_template.description
        OR v_duration IS DISTINCT FROM v_template.default_duration_days
        OR v_status IS DISTINCT FROM v_template.status;

    IF NOT v_changed THEN
        RETURN QUERY SELECT 'updated'::text, false, NULL::jsonb, v_template.version;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    BEGIN
        UPDATE public.subscription_templates AS target
        SET name = v_name,
            description = v_description,
            default_duration_days = v_duration,
            status = v_status,
            version = v_template.version + 1,
            updated_at = v_now
        WHERE target.id = p_template_id;
    EXCEPTION
        WHEN unique_violation THEN
            RETURN QUERY SELECT
                'conflict'::text, false, v_before_state, v_template.version;
            RETURN;
    END;

    RETURN QUERY SELECT 'updated'::text, true, v_before_state, v_template.version + 1;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_subscription_template_retire(
    p_template_id uuid,
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
    v_template public.subscription_templates%ROWTYPE;
    v_group_id uuid;
    v_group_status text;
    v_now timestamptz;
    v_before_state jsonb;
BEGIN
    IF p_template_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_reason IS NULL
        OR btrim(p_reason) = ''
        OR char_length(p_reason) > 500 THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT candidate.group_id
    INTO v_group_id
    FROM public.subscription_templates AS candidate
    WHERE candidate.id = p_template_id;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_group.status
    INTO v_group_status
    FROM public.groups AS current_group
    WHERE current_group.id = v_group_id
    FOR SHARE;
    IF NOT FOUND OR v_group_status = 'archived' THEN
        RETURN QUERY SELECT 'group_archived'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_template.*
    INTO v_template
    FROM public.subscription_templates AS current_template
    WHERE current_template.id = p_template_id
    FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    v_before_state := jsonb_build_object(
        'id', v_template.id,
        'group_id', v_template.group_id,
        'name', v_template.name,
        'description', v_template.description,
        'default_duration_days', v_template.default_duration_days,
        'status', v_template.status,
        'version', v_template.version,
        'created_at', v_template.created_at,
        'updated_at', v_template.updated_at
    );
    IF v_template.group_id <> v_group_id
        OR v_template.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_template.version;
        RETURN;
    END IF;
    IF v_template.status = 'retired' THEN
        RETURN QUERY SELECT
            'invalid_transition'::text, false, v_before_state, v_template.version;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    UPDATE public.subscription_templates AS target
    SET status = 'retired',
        version = v_template.version + 1,
        updated_at = v_now,
        deleted_at = v_now
    WHERE target.id = p_template_id;

    RETURN QUERY SELECT 'retired'::text, true, v_before_state, v_template.version + 1;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_subscription_assign(
    p_subscription_id uuid,
    p_user_id uuid,
    p_template_id uuid,
    p_starts_at timestamptz,
    p_expires_at timestamptz,
    p_assigned_by uuid,
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
    v_group_id uuid;
    v_group_status text;
    v_template_name text;
    v_template_status text;
    v_duration integer;
    v_now timestamptz;
    v_starts_at timestamptz;
    v_expires_at timestamptz;
    v_inserted integer;
BEGIN
    IF p_subscription_id IS NULL
        OR p_user_id IS NULL
        OR p_template_id IS NULL
        OR p_assigned_by IS NULL
        OR p_reason IS NULL
        OR btrim(p_reason) = ''
        OR char_length(p_reason) > 500
        OR (p_starts_at IS NOT NULL AND NOT isfinite(p_starts_at))
        OR (p_expires_at IS NOT NULL AND NOT isfinite(p_expires_at)) THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT candidate.group_id
    INTO v_group_id
    FROM public.subscription_templates AS candidate
    WHERE candidate.id = p_template_id;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_group.status
    INTO v_group_status
    FROM public.groups AS current_group
    WHERE current_group.id = v_group_id
    FOR SHARE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    IF v_group_status <> 'active' THEN
        RETURN QUERY SELECT 'group_disabled'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_template.name,
           current_template.status,
           current_template.default_duration_days
    INTO v_template_name, v_template_status, v_duration
    FROM public.subscription_templates AS current_template
    WHERE current_template.id = p_template_id
      AND current_template.group_id = v_group_id
    FOR SHARE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    IF v_template_status <> 'active' THEN
        RETURN QUERY SELECT 'template_disabled'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    v_now := clock_timestamp();
    v_starts_at := coalesce(p_starts_at, v_now);
    v_expires_at := coalesce(
        p_expires_at,
        v_starts_at + make_interval(days => v_duration)
    );
    IF v_expires_at <= v_starts_at THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    BEGIN
        INSERT INTO public.subscriptions (
            id, user_id, group_id, template_id, template_name_snapshot,
            status, starts_at, expires_at, source, assigned_by, change_reason,
            version, created_at, updated_at
        ) VALUES (
            p_subscription_id, p_user_id, v_group_id, p_template_id, v_template_name,
            'active', v_starts_at, v_expires_at, 'admin', p_assigned_by, p_reason,
            1, v_now, v_now
        )
        ON CONFLICT ON CONSTRAINT uq_subscriptions_user_group DO NOTHING;
        GET DIAGNOSTICS v_inserted = ROW_COUNT;
    EXCEPTION
        WHEN unique_violation OR foreign_key_violation THEN
            RETURN QUERY SELECT 'conflict'::text, false, NULL::jsonb, NULL::bigint;
            RETURN;
    END;

    IF v_inserted = 0 THEN
        RETURN QUERY SELECT
            'subscription_conflict'::text,
            false,
            NULL::jsonb,
            NULL::bigint;
        RETURN;
    END IF;

    RETURN QUERY SELECT 'created'::text, true, NULL::jsonb, 1::bigint;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_subscription_update(
    p_subscription_id uuid,
    p_expected_version bigint,
    p_set_starts_at boolean,
    p_starts_at timestamptz,
    p_set_expires_at boolean,
    p_expires_at timestamptz,
    p_status text,
    p_allow_revoked_regrant boolean,
    p_assigned_by uuid,
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
    v_subscription public.subscriptions%ROWTYPE;
    v_group_id uuid;
    v_group_status text;
    v_status text;
    v_starts_at timestamptz;
    v_expires_at timestamptz;
    v_assigned_by uuid;
    v_now timestamptz;
    v_changed boolean;
    v_before_state jsonb;
BEGIN
    IF p_subscription_id IS NULL
        OR p_expected_version IS NULL
        OR p_expected_version <= 0
        OR p_set_starts_at IS NULL
        OR p_set_expires_at IS NULL
        OR (NOT p_set_starts_at AND p_starts_at IS NOT NULL)
        OR (p_set_starts_at AND p_starts_at IS NULL)
        OR (NOT p_set_expires_at AND p_expires_at IS NOT NULL)
        OR (p_set_expires_at AND p_expires_at IS NULL)
        OR (p_status IS NOT NULL AND p_status NOT IN ('active', 'suspended', 'revoked'))
        OR p_allow_revoked_regrant IS NULL
        OR p_assigned_by IS NULL
        OR p_reason IS NULL
        OR btrim(p_reason) = ''
        OR char_length(p_reason) > 500
        OR (p_starts_at IS NOT NULL AND NOT isfinite(p_starts_at))
        OR (p_expires_at IS NOT NULL AND NOT isfinite(p_expires_at)) THEN
        RETURN QUERY SELECT 'validation_failed'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    -- Locate without a row lock, then acquire the canonical Group fence before
    -- the Subscription row. Re-read and verify the identity after both locks.
    SELECT candidate.group_id
    INTO v_group_id
    FROM public.subscriptions AS candidate
    WHERE candidate.id = p_subscription_id;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_group.status
    INTO v_group_status
    FROM public.groups AS current_group
    WHERE current_group.id = v_group_id
    FOR SHARE;
    IF NOT FOUND OR v_group_status = 'archived' THEN
        RETURN QUERY SELECT 'group_archived'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;

    SELECT current_subscription.*
    INTO v_subscription
    FROM public.subscriptions AS current_subscription
    WHERE current_subscription.id = p_subscription_id
    FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false, NULL::jsonb, NULL::bigint;
        RETURN;
    END IF;
    -- Observe time only after both the Group fence and Subscription row lock.
    v_now := clock_timestamp();
    v_before_state := jsonb_build_object(
        'id', v_subscription.id,
        'user_id', v_subscription.user_id,
        'group_id', v_subscription.group_id,
        'template_id', v_subscription.template_id,
        'plan_name', v_subscription.template_name_snapshot,
        'starts_at', v_subscription.starts_at,
        'expires_at', v_subscription.expires_at,
        'status', v_subscription.status,
        'effective_status', CASE
            WHEN v_subscription.status = 'revoked' THEN 'revoked'
            WHEN v_subscription.status = 'suspended' THEN 'suspended'
            WHEN v_now < v_subscription.starts_at THEN 'scheduled'
            WHEN v_now >= v_subscription.expires_at THEN 'expired'
            ELSE 'active'
        END,
        'assigned_by', v_subscription.assigned_by,
        'version', v_subscription.version,
        'created_at', v_subscription.created_at,
        'updated_at', v_subscription.updated_at
    );
    IF v_subscription.group_id <> v_group_id
        OR v_subscription.version <> p_expected_version THEN
        RETURN QUERY SELECT
            'version_conflict'::text, false, v_before_state, v_subscription.version;
        RETURN;
    END IF;

    v_status := coalesce(p_status, v_subscription.status);
    v_starts_at := CASE
        WHEN p_set_starts_at THEN p_starts_at
        ELSE v_subscription.starts_at
    END;
    v_expires_at := CASE
        WHEN p_set_expires_at THEN p_expires_at
        ELSE v_subscription.expires_at
    END;
    v_assigned_by := v_subscription.assigned_by;

    IF v_expires_at <= v_starts_at THEN
        RETURN QUERY SELECT
            'validation_failed'::text, false, v_before_state, v_subscription.version;
        RETURN;
    END IF;

    IF v_subscription.status = 'active' THEN
        IF v_status = 'active' THEN
            IF p_set_starts_at
                OR (p_set_expires_at AND v_expires_at < v_subscription.expires_at) THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
        ELSIF v_status IN ('suspended', 'revoked') THEN
            IF p_set_starts_at OR p_set_expires_at THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
        ELSE
            RETURN QUERY SELECT
                'invalid_transition'::text, false,
                v_before_state, v_subscription.version;
            RETURN;
        END IF;
    ELSIF v_subscription.status = 'suspended' THEN
        IF v_status = 'suspended' THEN
            IF p_set_starts_at
                OR (p_set_expires_at AND v_expires_at < v_subscription.expires_at) THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
        ELSIF v_status = 'active' THEN
            IF p_set_expires_at AND v_expires_at < v_subscription.expires_at THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
            IF NOT (v_starts_at <= v_now AND v_now < v_expires_at) THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
        ELSIF v_status = 'revoked' THEN
            IF p_set_starts_at OR p_set_expires_at THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
        ELSE
            RETURN QUERY SELECT
                'invalid_transition'::text, false,
                v_before_state, v_subscription.version;
            RETURN;
        END IF;
    ELSIF v_subscription.status = 'revoked' THEN
        IF v_status = 'active' THEN
            IF NOT p_allow_revoked_regrant
                OR NOT p_set_starts_at
                OR NOT p_set_expires_at THEN
                RETURN QUERY SELECT
                    'invalid_transition'::text, false,
                    v_before_state, v_subscription.version;
                RETURN;
            END IF;
            v_assigned_by := p_assigned_by;
        ELSIF v_status = 'revoked' AND NOT p_set_starts_at AND NOT p_set_expires_at THEN
            NULL;
        ELSE
            RETURN QUERY SELECT
                'invalid_transition'::text, false,
                v_before_state, v_subscription.version;
            RETURN;
        END IF;
    ELSE
        RETURN QUERY SELECT
            'invalid_transition'::text, false,
            v_before_state, v_subscription.version;
        RETURN;
    END IF;

    v_changed := v_status IS DISTINCT FROM v_subscription.status
        OR v_starts_at IS DISTINCT FROM v_subscription.starts_at
        OR v_expires_at IS DISTINCT FROM v_subscription.expires_at
        OR v_assigned_by IS DISTINCT FROM v_subscription.assigned_by;
    IF NOT v_changed THEN
        RETURN QUERY SELECT
            'updated'::text, false, NULL::jsonb, v_subscription.version;
        RETURN;
    END IF;

    BEGIN
        UPDATE public.subscriptions AS target
        SET status = v_status,
            starts_at = v_starts_at,
            expires_at = v_expires_at,
            assigned_by = v_assigned_by,
            change_reason = p_reason,
            version = v_subscription.version + 1,
            updated_at = v_now
        WHERE target.id = p_subscription_id;
    EXCEPTION
        WHEN foreign_key_violation THEN
            RETURN QUERY SELECT
                'conflict'::text, false, v_before_state, v_subscription.version;
            RETURN;
    END;

    RETURN QUERY SELECT
        'updated'::text, true, v_before_state, v_subscription.version + 1;
END;
$function$;

-- The function owner receives only the columns required by these entry points.
GRANT SELECT (
    id, name, description, status,
    activation_supply_readiness_token, activation_supply_observed_at,
    version, created_at, updated_at, deleted_at
) ON public.groups TO poolai_runtime_owner;
GRANT INSERT (
    id, name, description, status, version, created_at, updated_at
) ON public.groups TO poolai_runtime_owner;
GRANT UPDATE (
    name, description, status,
    activation_supply_readiness_token, activation_supply_observed_at,
    version, updated_at, deleted_at
) ON public.groups TO poolai_runtime_owner;

GRANT SELECT (
    id, group_id, name, description, default_duration_days,
    status, version, created_at, updated_at, deleted_at
) ON public.subscription_templates TO poolai_runtime_owner;
GRANT INSERT (
    id, group_id, name, description, default_duration_days,
    status, version, created_at, updated_at
) ON public.subscription_templates TO poolai_runtime_owner;
GRANT UPDATE (
    name, description, default_duration_days, status,
    version, updated_at, deleted_at
) ON public.subscription_templates TO poolai_runtime_owner;

GRANT SELECT (
    id, user_id, group_id, template_id, template_name_snapshot,
    status, starts_at, expires_at, source, assigned_by, change_reason,
    version, created_at, updated_at
) ON public.subscriptions TO poolai_runtime_owner;
GRANT INSERT (
    id, user_id, group_id, template_id, template_name_snapshot,
    status, starts_at, expires_at, source, assigned_by, change_reason,
    version, created_at, updated_at
) ON public.subscriptions TO poolai_runtime_owner;
GRANT UPDATE (
    status, starts_at, expires_at, assigned_by, change_reason,
    version, updated_at
) ON public.subscriptions TO poolai_runtime_owner;

-- The pre-existing Group activation trigger is an invoker trigger and reads
-- the configured Channel mapping while the Group entry point runs as the
-- NOLOGIN owner. Keep that cross-context exception read-only and column-bound.
GRANT SELECT (model_rules) ON public.channels TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_create(
    uuid, text, text, uuid, numeric, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_group_update(
    uuid, bigint, boolean, text, boolean, text, text, text, text, timestamptz
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_subscription_template_create(
    uuid, uuid, text, text, integer
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_subscription_template_update(
    uuid, bigint, boolean, text, boolean, text, boolean, integer, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_subscription_template_retire(
    uuid, bigint, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_subscription_assign(
    uuid, uuid, uuid, timestamptz, timestamptz, uuid, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_subscription_update(
    uuid, bigint, boolean, timestamptz, boolean, timestamptz,
    text, boolean, uuid, text
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
-- Group creation is now the sole runtime initialization path. Leaving the
-- earlier Group -> Quota entry point callable beside Quota -> Group archive
-- would permit a deadlock cycle on an already initialized Group.
REVOKE EXECUTE ON FUNCTION public.poolai_quota_initialize(
    uuid, uuid, numeric, uuid, uuid, uuid, text, text
) FROM poolai_api;
REVOKE EXECUTE ON FUNCTION public.poolai_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) FROM poolai_api;
REVOKE EXECUTE ON FUNCTION public.poolai_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) FROM poolai_api;
REVOKE ALL ON FUNCTION public.poolai_group_create(
    uuid, text, text, uuid, numeric, uuid, uuid, uuid, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_group_update(
    uuid, bigint, boolean, text, boolean, text, text, text, text, timestamptz
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_subscription_template_create(
    uuid, uuid, text, text, integer
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_subscription_template_update(
    uuid, bigint, boolean, text, boolean, text, boolean, integer, text, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_subscription_template_retire(
    uuid, bigint, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_subscription_assign(
    uuid, uuid, uuid, timestamptz, timestamptz, uuid, text
) FROM PUBLIC, poolai_api, poolai_worker;
REVOKE ALL ON FUNCTION public.poolai_subscription_update(
    uuid, bigint, boolean, timestamptz, boolean, timestamptz,
    text, boolean, uuid, text
) FROM PUBLIC, poolai_api, poolai_worker;

GRANT EXECUTE ON FUNCTION public.poolai_group_create(
    uuid, text, text, uuid, numeric, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_group_update(
    uuid, bigint, boolean, text, boolean, text, text, text, text, timestamptz
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_subscription_template_create(
    uuid, uuid, text, text, integer
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_subscription_template_update(
    uuid, bigint, boolean, text, boolean, text, boolean, integer, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_subscription_template_retire(
    uuid, bigint, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_subscription_assign(
    uuid, uuid, uuid, timestamptz, timestamptz, uuid, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_subscription_update(
    uuid, bigint, boolean, timestamptz, boolean, timestamptz,
    text, boolean, uuid, text
) TO poolai_api;
RESET ROLE;

-- Fail the migration if a future grant silently re-opens a table write or if
-- any M1-E4 entry point loses its owner/search-path/allowlist boundary.
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
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.subscription_templates', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.subscription_templates', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.subscription_templates', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.subscription_templates', 'UPDATE')
        OR pg_catalog.has_table_privilege('poolai_api', 'public.subscriptions', 'INSERT')
        OR pg_catalog.has_table_privilege('poolai_api', 'public.subscriptions', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.subscriptions', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.subscriptions', 'UPDATE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_initialize(uuid,uuid,numeric,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_reset(uuid,uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE')
        OR pg_catalog.has_function_privilege(
            'poolai_api',
            'public.poolai_quota_adjust_total(uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure,
            'EXECUTE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e4_direct_table_write_forbidden';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = ANY (ARRAY[
              'poolai_group_create',
              'poolai_group_update',
              'poolai_subscription_template_create',
              'poolai_subscription_template_update',
              'poolai_subscription_template_retire',
              'poolai_subscription_assign',
              'poolai_subscription_update'
          ])
    ) <> 7 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m1_e4_entry_point_overload_forbidden';
    END IF;

    FOREACH v_function_signature IN ARRAY ARRAY[
        'public.poolai_group_create(uuid,text,text,uuid,numeric,uuid,uuid,uuid,text,text)',
        'public.poolai_group_update(uuid,bigint,boolean,text,boolean,text,text,text,text,timestamptz)',
        'public.poolai_subscription_template_create(uuid,uuid,text,text,integer)',
        'public.poolai_subscription_template_update(uuid,bigint,boolean,text,boolean,text,boolean,integer,text,text)',
        'public.poolai_subscription_template_retire(uuid,bigint,text)',
        'public.poolai_subscription_assign(uuid,uuid,uuid,timestamptz,timestamptz,uuid,text)',
        'public.poolai_subscription_update(uuid,bigint,boolean,timestamptz,boolean,timestamptz,text,boolean,uuid,text)'
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
                MESSAGE = 'poolai_m1_e4_entry_point_boundary_missing',
                DETAIL = v_function_signature;
        END IF;
    END LOOP;
END;
$permission_audit$;
