-- PoolAI Release 1 M3 Exit dispatch timestamp monotonicity correction.
--
-- Migration 0002 remains byte-for-byte immutable. This forward replacement
-- keeps its exact dispatch ABI, locks, validation, lease clock and writes, and
-- only clamps the persisted timestamp to the reservation-local time frontier.

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;
SET LOCAL ROLE poolai_runtime_owner;

CREATE OR REPLACE FUNCTION public.poolai_quota_mark_dispatched(
    p_group_id uuid,
    p_attempt_id uuid,
    p_lease_owner text,
    p_provider text,
    p_model text,
    p_estimated_input_tokens numeric,
    p_estimated_output_tokens numeric,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_dispatch_started_at timestamptz,
    result_lease_expires_at timestamptz,
    result_max_expires_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_period_id uuid;
    v_now timestamptz;
    v_dispatch_at timestamptz;
BEGIN
    IF p_group_id IS NULL OR p_attempt_id IS NULL
        OR p_lease_owner IS NULL OR btrim(p_lease_owner) = ''
        OR p_provider IS NULL OR p_provider NOT IN ('openai', 'openai_compatible')
        OR p_model IS NULL OR btrim(p_model) = ''
        OR p_estimated_input_tokens IS NULL OR p_estimated_input_tokens < 0
        OR p_estimated_input_tokens <> trunc(p_estimated_input_tokens)
        OR p_estimated_output_tokens IS NULL OR p_estimated_output_tokens < 0
        OR p_estimated_output_tokens <> trunc(p_estimated_output_tokens)
        OR p_event_id IS NULL OR p_outbox_id IS NULL
        OR p_idempotency_key IS NULL OR btrim(p_idempotency_key) = '' THEN
        PERFORM poolai_business_error('invalid_reservation_dispatch');
    END IF;

    SELECT q.* INTO v_quota
    FROM group_token_quotas q
    WHERE q.group_id = p_group_id
    FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('group_quota_not_found');
    END IF;

    SELECT r.period_id INTO v_period_id
    FROM group_token_reservations r
    WHERE r.group_id = p_group_id AND r.attempt_id = p_attempt_id;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('reservation_not_found');
    END IF;

    SELECT p.* INTO STRICT v_period
    FROM group_quota_periods p
    WHERE p.id = v_period_id AND p.group_id = p_group_id
    FOR UPDATE;

    SELECT r.* INTO STRICT v_reservation
    FROM group_token_reservations r
    WHERE r.group_id = p_group_id AND r.attempt_id = p_attempt_id
    FOR UPDATE;

    IF p_estimated_input_tokens + p_estimated_output_tokens
        <> v_reservation.estimated_tokens THEN
        PERFORM poolai_business_error('dispatch_estimate_split_mismatch');
    END IF;

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;

    IF v_reservation.dispatch_started_at IS NOT NULL THEN
        IF NOT FOUND
            OR v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'dispatch_started'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_reservation.lease_owner <> p_lease_owner
            OR v_reservation.dispatch_provider <> p_provider
            OR v_reservation.dispatch_model <> p_model
            OR v_reservation.estimated_input_tokens <> p_estimated_input_tokens
            OR v_reservation.estimated_output_tokens <> p_estimated_output_tokens
            OR v_existing_event.metadata ->> 'lease_owner' IS DISTINCT FROM p_lease_owner
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('reservation_already_dispatched');
        END IF;
        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_reservation.dispatch_started_at,
            v_reservation.lease_expires_at, v_reservation.max_expires_at;
        RETURN;
    END IF;

    IF FOUND THEN
        PERFORM poolai_business_error('idempotency_key_reused');
    END IF;
    IF v_reservation.status <> 'pending' THEN
        PERFORM poolai_business_error('reservation_not_pending');
    END IF;
    IF v_reservation.lease_owner <> p_lease_owner THEN
        PERFORM poolai_business_error('reservation_owner_mismatch');
    END IF;

    PERFORM 1
    FROM accounts a
    JOIN channels c ON c.id = v_reservation.channel_id
    WHERE a.id = v_reservation.account_id
      AND a.provider = p_provider
      AND c.provider = p_provider
    FOR SHARE OF a, c;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('reservation_provider_mismatch');
    END IF;

    v_now := clock_timestamp();
    IF v_reservation.lease_expires_at <= v_now THEN
        PERFORM poolai_business_error('reservation_lease_expired');
    END IF;
    IF v_reservation.max_expires_at <= v_now THEN
        PERFORM poolai_business_error('reservation_max_lifetime_reached');
    END IF;

    v_dispatch_at := greatest(
        v_now,
        v_reservation.created_at,
        v_reservation.updated_at
    );

    UPDATE group_token_reservations r
    SET dispatch_started_at = v_dispatch_at,
        dispatch_provider = p_provider,
        dispatch_model = p_model,
        estimated_input_tokens = p_estimated_input_tokens,
        estimated_output_tokens = p_estimated_output_tokens,
        updated_at = v_dispatch_at
    WHERE r.id = v_reservation.id
      AND r.status = 'pending'
      AND r.dispatch_started_at IS NULL
    RETURNING r.* INTO STRICT v_reservation;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, 'dispatch_started',
        0, 0, 0,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'gateway', NULL, p_idempotency_key, NULL,
        jsonb_build_object(
            'request_id', v_reservation.request_id,
            'attempt_index', v_reservation.attempt_index,
            'account_id', v_reservation.account_id,
            'channel_id', v_reservation.channel_id,
            'lease_owner', p_lease_owner,
            'provider', p_provider,
            'model', p_model,
            'estimated_input_tokens', p_estimated_input_tokens::text,
            'estimated_output_tokens', p_estimated_output_tokens::text,
            'dispatch_started_at', v_dispatch_at
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_reservation.dispatch_started_at,
        v_reservation.lease_expires_at, v_reservation.max_expires_at;
END;
$function$;

RESET ROLE;
REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;
