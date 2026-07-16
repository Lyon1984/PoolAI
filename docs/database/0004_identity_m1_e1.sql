-- PoolAI Release 1 M1-E1 Identity runtime and observability increment.
--
-- Role replacement is exposed only through one SECURITY DEFINER entry point.
-- The function owns the fixed lock order, expected-version predicate, last-active-
-- administrator invariant, and the command's single security-version transition.

REVOKE DELETE ON public.user_roles FROM poolai_api;
REVOKE UPDATE ON public.user_roles FROM poolai_api;

-- A marker can suppress the legacy role trigger only while the NOLOGIN function
-- owner is executing the atomic Identity function. poolai_api cannot spoof this
-- path by setting the same transaction-local configuration value.
CREATE OR REPLACE FUNCTION public.poolai_bump_role_user_version()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF TG_OP = 'UPDATE'
        AND current_user = 'poolai_runtime_owner'
        AND session_user <> current_user
        AND NEW.user_id = OLD.user_id
        AND current_setting('poolai.identity_role_user_id', true) = OLD.user_id::text THEN
        RETURN NEW;
    END IF;

    IF TG_OP IN ('DELETE', 'UPDATE') THEN
        UPDATE public.users
        SET token_version = token_version + 1,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE id = OLD.user_id;
    END IF;
    IF TG_OP = 'INSERT' OR (TG_OP = 'UPDATE' AND NEW.user_id IS DISTINCT FROM OLD.user_id) THEN
        UPDATE public.users
        SET token_version = token_version + 1,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE id = NEW.user_id;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE OR REPLACE FUNCTION public.poolai_identity_update_user(
    p_user_id uuid,
    p_expected_version bigint,
    p_display_name text,
    p_status text,
    p_role_id uuid,
    p_assigned_by uuid
)
RETURNS TABLE(disposition text, was_changed boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_display_name text;
    v_status text;
    v_version bigint;
    v_token_version bigint;
    v_deleted_at timestamptz;
    v_role_id uuid;
    v_effective_display_name text;
    v_effective_status text;
    v_effective_role_id uuid;
    v_user_changed boolean;
    v_role_changed boolean;
    v_removes_active_admin boolean;
    v_active_admin_count bigint;
BEGIN
    IF p_user_id IS NULL OR p_assigned_by IS NULL OR p_expected_version <= 0 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'identity_update_arguments_invalid';
    END IF;
    IF p_status IS NOT NULL AND p_status NOT IN ('active', 'disabled') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'identity_update_status_invalid';
    END IF;
    IF p_role_id IS NOT NULL AND p_role_id NOT IN (
        '01900000-0000-7000-8000-000000000001'::uuid,
        '01900000-0000-7000-8000-000000000002'::uuid,
        '01900000-0000-7000-8000-000000000003'::uuid,
        '01900000-0000-7000-8000-000000000004'::uuid
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'identity_update_role_invalid';
    END IF;

    -- Serialize commands that can remove the last active administrator before
    -- taking row locks. Every caller then follows User -> UserRole lock order.
    IF p_status IS NOT NULL OR p_role_id IS NOT NULL THEN
        PERFORM pg_advisory_xact_lock(5786931235259499597);
    END IF;

    SELECT u.display_name, u.status, u.version, u.token_version, u.deleted_at
    INTO v_display_name, v_status, v_version, v_token_version, v_deleted_at
    FROM public.users AS u
    WHERE u.id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, false;
        RETURN;
    END IF;

    SELECT ur.role_id
    INTO v_role_id
    FROM public.user_roles AS ur
    WHERE ur.user_id = p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'identity_role_assignment_missing';
    END IF;

    IF v_version <> p_expected_version THEN
        RETURN QUERY SELECT 'version_conflict'::text, false;
        RETURN;
    END IF;

    v_effective_display_name := coalesce(p_display_name, v_display_name);
    v_effective_status := coalesce(p_status, v_status);
    v_effective_role_id := coalesce(p_role_id, v_role_id);
    v_user_changed := v_effective_display_name IS DISTINCT FROM v_display_name
        OR v_effective_status IS DISTINCT FROM v_status;
    v_role_changed := v_effective_role_id IS DISTINCT FROM v_role_id;
    v_removes_active_admin := v_role_id = '01900000-0000-7000-8000-000000000001'::uuid
        AND v_status = 'active'
        AND v_deleted_at IS NULL
        AND (v_effective_role_id <> '01900000-0000-7000-8000-000000000001'::uuid
            OR v_effective_status <> 'active');

    IF v_removes_active_admin THEN
        SELECT count(*)
        INTO v_active_admin_count
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        WHERE u.status = 'active'
          AND u.deleted_at IS NULL
          AND ur.role_id = '01900000-0000-7000-8000-000000000001'::uuid;
        IF v_active_admin_count <= 1 THEN
            RETURN QUERY SELECT 'last_active_admin'::text, false;
            RETURN;
        END IF;
    END IF;

    IF NOT v_user_changed AND NOT v_role_changed THEN
        RETURN QUERY SELECT 'updated'::text, false;
        RETURN;
    END IF;

    PERFORM set_config('poolai.identity_role_user_id', p_user_id::text, true);
    UPDATE public.users
    SET display_name = v_effective_display_name,
        status = v_effective_status,
        token_version = CASE
            WHEN v_role_changed THEN v_token_version + 1
            ELSE v_token_version
        END,
        version = v_version + 1,
        updated_at = clock_timestamp()
    WHERE id = p_user_id;

    IF v_role_changed THEN
        UPDATE public.user_roles
        SET role_id = v_effective_role_id,
            assigned_by = p_assigned_by,
            assigned_at = clock_timestamp()
        WHERE user_id = p_user_id;
    END IF;
    PERFORM set_config('poolai.identity_role_user_id', '', true);

    RETURN QUERY SELECT 'updated'::text, true;
EXCEPTION WHEN OTHERS THEN
    PERFORM set_config('poolai.identity_role_user_id', '', true);
    RAISE;
END;
$function$;

-- Durable failure facts make retry/dead counters recoverable after a Worker
-- process exits immediately after committing an outbox transition.
ALTER TABLE public.email_outbox
    ADD COLUMN last_failure_class text;

ALTER TABLE public.email_outbox
    ADD CONSTRAINT ck_email_outbox_failure_class CHECK (
        last_failure_class IS NULL OR last_failure_class IN (
            'network', 'dns', 'tls', 'timeout', 'smtp_4xx', 'smtp_5xx',
            'smtp_capability', 'smtp_protocol', 'invalid_recipient',
            'invalid_message', 'invalid_template', 'invalid_envelope'
        )
    );

CREATE TABLE public.email_outbox_delivery_failures (
    email_id        uuid NOT NULL,
    lock_generation bigint NOT NULL,
    attempt         integer NOT NULL,
    failure_class   text NOT NULL,
    outcome         text NOT NULL,
    terminal_reason text,
    occurred_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (email_id, lock_generation, attempt),
    CONSTRAINT fk_email_outbox_delivery_failures_email
        FOREIGN KEY (email_id) REFERENCES public.email_outbox(id) ON DELETE RESTRICT,
    CONSTRAINT ck_email_outbox_delivery_failures_generation CHECK (lock_generation > 0),
    CONSTRAINT ck_email_outbox_delivery_failures_attempt CHECK (attempt > 0),
    CONSTRAINT ck_email_outbox_delivery_failures_class CHECK (failure_class IN (
        'network', 'dns', 'tls', 'timeout', 'smtp_4xx', 'smtp_5xx',
        'smtp_capability', 'smtp_protocol', 'invalid_recipient',
        'invalid_message', 'invalid_template', 'invalid_envelope'
    )),
    CONSTRAINT ck_email_outbox_delivery_failures_outcome CHECK (outcome IN ('retry', 'dead')),
    CONSTRAINT ck_email_outbox_delivery_failures_terminal CHECK (
        (outcome = 'retry' AND terminal_reason IS NULL)
        OR (outcome = 'dead' AND terminal_reason IS NOT NULL
            AND btrim(terminal_reason) = terminal_reason
            AND length(terminal_reason) BETWEEN 1 AND 64)
    )
);

-- The shared NOLOGIN owner receives only the extra columns needed by the
-- Identity function. It gains neither credential reads, DELETE, nor DDL.
GRANT SELECT (display_name, version, token_version, deleted_at)
    ON public.users TO poolai_runtime_owner;
GRANT SELECT (role_id) ON public.user_roles TO poolai_runtime_owner;
GRANT UPDATE (display_name, status, token_version, version, updated_at)
    ON public.users TO poolai_runtime_owner;
GRANT UPDATE (role_id, assigned_by, assigned_at)
    ON public.user_roles TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_identity_update_user(
    uuid, bigint, text, text, uuid, uuid
) OWNER TO poolai_runtime_owner;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_identity_update_user(
    uuid, bigint, text, text, uuid, uuid
) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.poolai_identity_update_user(
    uuid, bigint, text, text, uuid, uuid
) TO poolai_api;
RESET ROLE;

GRANT SELECT (last_failure_class) ON public.email_outbox TO poolai_worker;
GRANT UPDATE (last_failure_class) ON public.email_outbox TO poolai_worker;
GRANT SELECT, INSERT ON public.email_outbox_delivery_failures TO poolai_worker;
