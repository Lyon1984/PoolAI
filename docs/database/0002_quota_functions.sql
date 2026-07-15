-- PoolAI Release 1 quota mutation API for PostgreSQL 18.
--
-- PoolAI.Migrator executes this file and its migration-ledger insert in one
-- transaction. Runtime callers use READ COMMITTED. Every mutation locks rows in
-- this relative order: quota -> request/identity/group rows (reserve only) ->
-- Supply configuration/binding/Account/Channel rows (reserve only) -> period ->
-- reservation. Never call these functions while holding those rows in another
-- order.

CREATE OR REPLACE FUNCTION poolai_business_error(p_code text, p_detail text DEFAULT NULL)
RETURNS void
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P0001',
        MESSAGE = p_code,
        DETAIL = coalesce(p_detail, '');
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_remaining(
    p_total numeric,
    p_consumed numeric,
    p_reserved numeric
)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
STRICT
AS $function$
    SELECT greatest(p_total - p_consumed - p_reserved, 0::numeric)
$function$;

CREATE OR REPLACE FUNCTION poolai_emit_quota_event(
    p_event_id uuid,
    p_outbox_id uuid,
    p_group_id uuid,
    p_period_id uuid,
    p_reservation_id uuid,
    p_attempt_id uuid,
    p_event_type text,
    p_delta_total numeric,
    p_delta_consumed numeric,
    p_delta_reserved numeric,
    p_total_after numeric,
    p_consumed_after numeric,
    p_reserved_after numeric,
    p_actor_type text,
    p_actor_user_id uuid,
    p_idempotency_key text,
    p_reason text,
    p_metadata jsonb
)
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    v_occurred_at timestamptz;
    v_source_event_sequence bigint;
    v_correlation_id uuid;
BEGIN
    IF p_event_id IS NULL OR p_outbox_id IS NULL OR p_group_id IS NULL
        OR p_period_id IS NULL OR p_event_type IS NULL
        OR p_delta_total IS NULL OR p_delta_consumed IS NULL OR p_delta_reserved IS NULL
        OR p_total_after IS NULL OR p_consumed_after IS NULL OR p_reserved_after IS NULL
        OR p_actor_type IS NULL OR p_idempotency_key IS NULL
        OR btrim(p_idempotency_key) = '' THEN
        PERFORM poolai_business_error('invalid_quota_event_identity');
    END IF;

    v_occurred_at := clock_timestamp();
    v_correlation_id := coalesce(
        nullif(coalesce(p_metadata, '{}'::jsonb) ->> 'request_id', '')::uuid,
        p_event_id
    );

    INSERT INTO group_quota_events (
        id, group_id, period_id, reservation_id, attempt_id, event_type,
        delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
        total_tokens_after, consumed_tokens_after, reserved_tokens_after,
        actor_type, actor_user_id, idempotency_key, reason, metadata, occurred_at
    ) VALUES (
        p_event_id, p_group_id, p_period_id, p_reservation_id, p_attempt_id, p_event_type,
        p_delta_total, p_delta_consumed, p_delta_reserved,
        p_total_after, p_consumed_after, p_reserved_after,
        p_actor_type, p_actor_user_id, p_idempotency_key,
        p_reason, coalesce(p_metadata, '{}'::jsonb), v_occurred_at
    )
    RETURNING event_sequence INTO v_source_event_sequence;

    INSERT INTO outbox_messages (
        id, deduplication_key, topic, schema_version,
        aggregate_type, aggregate_id, aggregate_version,
        event_type, source_event_sequence,
        correlation_id, causation_id, payload, occurred_at, next_attempt_at
    ) VALUES (
        p_outbox_id, 'quota:' || p_idempotency_key, 'poolai.quota.v1', 1,
        'group', p_group_id, NULL,
        p_event_type, v_source_event_sequence,
        v_correlation_id, p_attempt_id,
        jsonb_strip_nulls(jsonb_build_object(
            'schema_version', 1,
            'event_id', p_event_id,
            'source_event_sequence', v_source_event_sequence,
            'correlation_id', v_correlation_id,
            'causation_id', p_attempt_id,
            'group_id', p_group_id,
            'period_id', p_period_id,
            'reservation_id', p_reservation_id,
            'attempt_id', p_attempt_id,
            'event_type', p_event_type,
            'delta_total_tokens', p_delta_total::text,
            'delta_consumed_tokens', p_delta_consumed::text,
            'delta_reserved_tokens', p_delta_reserved::text,
            'total_tokens', p_total_after::text,
            'consumed_tokens', p_consumed_after::text,
            'reserved_tokens', p_reserved_after::text,
            'occurred_at', v_occurred_at,
            'metadata', coalesce(p_metadata, '{}'::jsonb)
        )),
        v_occurred_at, v_occurred_at
    );
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_reset(
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
    result_quota_version bigint
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_old_period group_quota_periods%ROWTYPE;
    v_new_period group_quota_periods%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_now timestamptz;
BEGIN
    IF p_group_id IS NULL OR p_new_period_id IS NULL OR p_new_total_tokens IS NULL
        OR p_actor_user_id IS NULL OR p_event_id IS NULL OR p_outbox_id IS NULL
        OR p_idempotency_key IS NULL OR btrim(p_idempotency_key) = ''
        OR p_expected_quota_version IS NULL
        OR p_new_total_tokens NOT BETWEEN 1 AND 9007199254740991
        OR p_new_total_tokens <> trunc(p_new_total_tokens)
        OR p_reason IS NULL OR btrim(p_reason) = '' THEN
        PERFORM poolai_business_error('invalid_quota_reset');
    END IF;

    SELECT q.* INTO v_quota
    FROM group_token_quotas q
    WHERE q.group_id = p_group_id
    FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('group_quota_not_found');
    END IF;

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'period_reset'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> p_new_period_id
            OR v_existing_event.total_tokens_after <> p_new_total_tokens
            OR v_existing_event.actor_user_id IS DISTINCT FROM p_actor_user_id
            OR v_existing_event.reason IS DISTINCT FROM p_reason
            OR v_existing_event.metadata ->> 'expected_quota_version'
                IS DISTINCT FROM p_expected_quota_version::text
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        SELECT p.* INTO STRICT v_new_period
        FROM group_quota_periods p
        WHERE p.id = v_existing_event.period_id AND p.group_id = p_group_id
        FOR UPDATE;
        RETURN QUERY SELECT
            v_new_period.id, v_new_period.period_number, v_new_period.total_tokens,
            v_new_period.consumed_tokens, v_new_period.reserved_tokens,
            poolai_quota_remaining(
                v_new_period.total_tokens, v_new_period.consumed_tokens, v_new_period.reserved_tokens
            ),
            v_quota.version;
        RETURN;
    END IF;

    IF v_quota.version <> p_expected_quota_version THEN
        PERFORM poolai_business_error('quota_version_conflict');
    END IF;

    SELECT p.* INTO STRICT v_old_period
    FROM group_quota_periods p
    WHERE p.id = v_quota.current_period_id AND p.group_id = p_group_id
    FOR UPDATE;

    IF v_old_period.status <> 'current' THEN
        PERFORM poolai_business_error('group_quota_period_not_current');
    END IF;
    v_now := clock_timestamp();
    UPDATE group_quota_periods p
    SET status = 'closed',
        closed_at = v_now,
        reset_reason = p_reason,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_old_period.id;

    INSERT INTO group_quota_periods (
        id, group_id, period_number, total_tokens, consumed_tokens,
        reserved_tokens, status, opened_at, version, created_at, updated_at
    ) VALUES (
        p_new_period_id, p_group_id, v_old_period.period_number + 1,
        p_new_total_tokens, 0, 0, 'current', v_now, 1, v_now, v_now
    )
    RETURNING * INTO v_new_period;

    UPDATE group_token_quotas q
    SET current_period_id = p_new_period_id,
        version = q.version + 1,
        updated_at = v_now
    WHERE q.group_id = p_group_id
    RETURNING q.* INTO v_quota;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_new_period.id,
        NULL, NULL, 'period_reset',
        p_new_total_tokens - v_old_period.total_tokens, 0, 0,
        v_new_period.total_tokens, 0, 0,
        'admin', p_actor_user_id, p_idempotency_key, p_reason,
        jsonb_build_object(
            'old_period_id', v_old_period.id,
            'old_period_number', v_old_period.period_number,
            'old_total_tokens', v_old_period.total_tokens::text,
            'old_consumed_tokens', v_old_period.consumed_tokens::text,
            'new_period_number', v_new_period.period_number,
            'expected_quota_version', p_expected_quota_version,
            'quota_version_after', v_quota.version
        )
    );

    RETURN QUERY SELECT
        v_new_period.id, v_new_period.period_number, v_new_period.total_tokens,
        v_new_period.consumed_tokens, v_new_period.reserved_tokens,
        v_new_period.total_tokens, v_quota.version;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_adjust_total(
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
    result_quota_version bigint
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_old_total numeric;
    v_now timestamptz;
BEGIN
    IF p_group_id IS NULL OR p_new_total_tokens IS NULL OR p_actor_user_id IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL OR p_idempotency_key IS NULL
        OR btrim(p_idempotency_key) = '' OR p_expected_quota_version IS NULL
        OR p_new_total_tokens NOT BETWEEN 1 AND 9007199254740991
        OR p_new_total_tokens <> trunc(p_new_total_tokens)
        OR p_reason IS NULL OR btrim(p_reason) = '' THEN
        PERFORM poolai_business_error('invalid_quota_total_adjustment');
    END IF;

    SELECT q.* INTO v_quota
    FROM group_token_quotas q
    WHERE q.group_id = p_group_id
    FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('group_quota_not_found');
    END IF;

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'total_adjusted'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.total_tokens_after <> p_new_total_tokens
            OR v_existing_event.actor_user_id IS DISTINCT FROM p_actor_user_id
            OR v_existing_event.reason IS DISTINCT FROM p_reason
            OR v_existing_event.metadata ->> 'expected_quota_version'
                IS DISTINCT FROM p_expected_quota_version::text
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        RETURN QUERY SELECT
            v_existing_event.period_id, v_existing_event.total_tokens_after,
            v_existing_event.consumed_tokens_after,
            v_existing_event.reserved_tokens_after,
            poolai_quota_remaining(
                v_existing_event.total_tokens_after,
                v_existing_event.consumed_tokens_after,
                v_existing_event.reserved_tokens_after
            ),
            v_quota.version;
        RETURN;
    END IF;

    SELECT p.* INTO STRICT v_period
    FROM group_quota_periods p
    WHERE p.id = v_quota.current_period_id AND p.group_id = p_group_id
    FOR UPDATE;

    IF v_quota.version <> p_expected_quota_version THEN
        PERFORM poolai_business_error('quota_version_conflict');
    END IF;
    IF v_period.status <> 'current' THEN
        PERFORM poolai_business_error('group_quota_period_not_current');
    END IF;
    v_now := clock_timestamp();
    v_old_total := v_period.total_tokens;
    UPDATE group_quota_periods p
    SET total_tokens = p_new_total_tokens,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_period.id
    RETURNING p.* INTO v_period;

    UPDATE group_token_quotas q
    SET version = q.version + 1,
        updated_at = v_now
    WHERE q.group_id = p_group_id
    RETURNING q.* INTO v_quota;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        NULL, NULL, 'total_adjusted',
        p_new_total_tokens - v_old_total, 0, 0,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'admin', p_actor_user_id, p_idempotency_key, p_reason,
        jsonb_build_object(
            'old_total_tokens', v_old_total::text,
            'new_total_tokens', p_new_total_tokens::text,
            'expected_quota_version', p_expected_quota_version,
            'quota_version_after', v_quota.version
        )
    );

    RETURN QUERY SELECT
        v_period.id, v_period.total_tokens, v_period.consumed_tokens,
        v_period.reserved_tokens,
        poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens),
        v_quota.version;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_end_pending(
    p_group_id uuid,
    p_attempt_id uuid,
    p_target_status text,
    p_actor_type text,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_period_id uuid;
    v_now timestamptz;
    v_conservative_expiry boolean;
    v_delta_consumed numeric;
BEGIN
    IF p_group_id IS NULL OR p_attempt_id IS NULL
        OR p_target_status IS NULL OR p_actor_type IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL OR p_idempotency_key IS NULL
        OR btrim(p_idempotency_key) = ''
        OR p_target_status NOT IN ('released', 'expired')
        OR (p_target_status = 'released' AND p_actor_type NOT IN ('gateway', 'system'))
        OR (p_target_status = 'expired' AND p_actor_type <> 'worker')
        OR p_reason IS NULL OR btrim(p_reason) = '' THEN
        PERFORM poolai_business_error('invalid_reservation_terminal_transition');
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

    v_now := clock_timestamp();

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> p_target_status
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_existing_event.actor_type IS DISTINCT FROM p_actor_type
            OR v_existing_event.reason IS DISTINCT FROM p_reason
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
            poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
        RETURN;
    END IF;

    IF v_reservation.status = p_target_status THEN
        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
            poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
        RETURN;
    END IF;
    IF v_reservation.status <> 'pending' THEN
        PERFORM poolai_business_error('reservation_not_pending');
    END IF;
    IF p_target_status = 'expired' AND v_reservation.lease_expires_at > v_now THEN
        PERFORM poolai_business_error('reservation_lease_not_expired');
    END IF;
    IF p_target_status = 'released' AND v_reservation.dispatch_started_at IS NOT NULL THEN
        PERFORM poolai_business_error(
            'dispatched_reservation_cannot_release',
            'A dispatched attempt must settle actual/estimated usage or expire conservatively.'
        );
    END IF;
    IF v_period.reserved_tokens < v_reservation.estimated_tokens THEN
        PERFORM poolai_business_error('quota_counter_invariant_broken');
    END IF;

    v_conservative_expiry := p_target_status = 'expired'
        AND v_reservation.dispatch_started_at IS NOT NULL;
    v_delta_consumed := CASE
        WHEN v_conservative_expiry THEN v_reservation.estimated_tokens
        ELSE 0
    END;
    IF v_period.consumed_tokens + v_delta_consumed > repeat('9', 78)::numeric THEN
        PERFORM poolai_business_error(
            'token_numeric_overflow',
            'The conservative expiry would exceed the 78-digit period counter.'
        );
    END IF;

    UPDATE group_quota_periods p
    SET consumed_tokens = p.consumed_tokens + v_delta_consumed,
        reserved_tokens = p.reserved_tokens - v_reservation.estimated_tokens,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_period.id
    RETURNING p.* INTO v_period;

    IF p_target_status = 'released' THEN
        UPDATE group_token_reservations r
        SET status = 'released', released_at = v_now, updated_at = v_now
        WHERE r.id = v_reservation.id AND r.status = 'pending'
        RETURNING r.* INTO STRICT v_reservation;
    ELSE
        UPDATE group_token_reservations r
        SET status = 'expired',
            actual_tokens = CASE
                WHEN v_conservative_expiry THEN r.estimated_tokens
                ELSE NULL
            END,
            usage_source = CASE
                WHEN v_conservative_expiry THEN 'conservative_estimate'
                ELSE NULL
            END,
            expired_at = v_now,
            updated_at = v_now
        WHERE r.id = v_reservation.id AND r.status = 'pending'
        RETURNING r.* INTO STRICT v_reservation;
    END IF;

    IF v_conservative_expiry THEN
        INSERT INTO usage_attempts (
            attempt_id, request_id, attempt_index, reservation_id,
            quota_group_id, routing_group_id, account_id, channel_id,
            provider, model, status, upstream_http_status, error_code,
            input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
            thinking_tokens, usage_source, is_estimated, upstream_request_id,
            raw_upstream_usage, dispatch_started_at, first_token_at, completed_at, created_at
        ) VALUES (
            p_attempt_id, v_reservation.request_id, v_reservation.attempt_index,
            v_reservation.id, p_group_id, p_group_id,
            v_reservation.account_id, v_reservation.channel_id,
            v_reservation.dispatch_provider, v_reservation.dispatch_model,
            'failed', NULL, 'reservation_lease_expired_after_dispatch',
            v_reservation.estimated_input_tokens, v_reservation.estimated_output_tokens,
            0, 0, 0, 'conservative_estimate', true, NULL, NULL,
            v_reservation.dispatch_started_at, NULL, v_now, v_now
        );

        UPDATE usage_requests r
        SET status = CASE
                WHEN r.status IN ('accepted', 'in_progress') THEN 'failed'
                ELSE r.status
            END,
            attempt_count = r.attempt_count + 1,
            final_attempt_id = CASE
                WHEN r.status IN ('accepted', 'in_progress') THEN p_attempt_id
                ELSE r.final_attempt_id
            END,
            effective_model = coalesce(r.effective_model, v_reservation.dispatch_model),
            error_code = CASE
                WHEN r.status IN ('accepted', 'in_progress')
                    THEN 'reservation_lease_expired_after_dispatch'
                ELSE r.error_code
            END,
            completed_at = CASE
                WHEN r.status IN ('accepted', 'in_progress') THEN v_now
                ELSE r.completed_at
            END
        WHERE r.request_id = v_reservation.request_id;
    END IF;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, p_target_status,
        0, v_delta_consumed, -v_reservation.estimated_tokens,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        p_actor_type, NULL, p_idempotency_key, p_reason,
        jsonb_build_object(
            'request_id', v_reservation.request_id,
            'attempt_index', v_reservation.attempt_index,
            'estimated_tokens', v_reservation.estimated_tokens::text,
            'dispatch_started_at', v_reservation.dispatch_started_at,
            'conservative_expiry', v_conservative_expiry,
            'conservative_consumed_tokens', v_delta_consumed::text,
            'lease_owner', v_reservation.lease_owner,
            'lease_expires_at', v_reservation.lease_expires_at
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_release(
    p_group_id uuid,
    p_attempt_id uuid,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric
)
LANGUAGE sql
AS $function$
    SELECT * FROM poolai_quota_end_pending(
        p_group_id, p_attempt_id, 'released', 'gateway',
        p_event_id, p_outbox_id, p_idempotency_key, p_reason
    )
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_expire(
    p_group_id uuid,
    p_attempt_id uuid,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric
)
LANGUAGE sql
AS $function$
    SELECT * FROM poolai_quota_end_pending(
        p_group_id, p_attempt_id, 'expired', 'worker',
        p_event_id, p_outbox_id, p_idempotency_key, p_reason
    )
$function$;

CREATE OR REPLACE FUNCTION poolai_numeric78_max()
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $function$
    SELECT repeat('9', 78)::numeric
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_settle(
    p_group_id uuid,
    p_attempt_id uuid,
    p_account_id uuid,
    p_channel_id uuid,
    p_provider text,
    p_model text,
    p_attempt_status text,
    p_upstream_http_status integer,
    p_error_code text,
    p_input_tokens numeric,
    p_output_tokens numeric,
    p_cache_read_tokens numeric,
    p_cache_creation_tokens numeric,
    p_thinking_tokens numeric,
    p_usage_source text,
    p_upstream_request_id text,
    p_raw_upstream_usage jsonb,
    p_dispatch_started_at timestamptz,
    p_first_token_at timestamptz,
    p_completed_at timestamptz,
    p_request_terminal_status text,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_attempt usage_attempts%ROWTYPE;
    v_period_id uuid;
    v_actual_tokens numeric;
    v_now timestamptz;
BEGIN
    v_actual_tokens := coalesce(p_input_tokens, -1) + coalesce(p_output_tokens, -1);

    IF p_group_id IS NULL OR p_attempt_id IS NULL OR p_account_id IS NULL
        OR p_channel_id IS NULL OR p_provider IS NULL
        OR p_provider NOT IN ('openai', 'openai_compatible')
        OR p_model IS NULL OR btrim(p_model) = ''
        OR p_attempt_status IS NULL OR p_attempt_status NOT IN ('succeeded', 'failed', 'cancelled')
        OR p_input_tokens IS NULL OR p_input_tokens < 0 OR p_input_tokens <> trunc(p_input_tokens)
        OR p_output_tokens IS NULL OR p_output_tokens < 0 OR p_output_tokens <> trunc(p_output_tokens)
        OR p_cache_read_tokens IS NULL OR p_cache_read_tokens NOT BETWEEN 0 AND p_input_tokens
        OR p_cache_read_tokens <> trunc(p_cache_read_tokens)
        OR p_cache_creation_tokens IS NULL OR p_cache_creation_tokens NOT BETWEEN 0 AND p_input_tokens
        OR p_cache_creation_tokens <> trunc(p_cache_creation_tokens)
        OR p_thinking_tokens IS NULL OR p_thinking_tokens NOT BETWEEN 0 AND p_output_tokens
        OR p_thinking_tokens <> trunc(p_thinking_tokens)
        OR p_cache_read_tokens + p_cache_creation_tokens > p_input_tokens
        OR p_usage_source IS NULL
        OR p_usage_source NOT IN (
            'upstream', 'local_tokenizer', 'conservative_estimate',
            'confirmed_no_execution'
        )
        OR p_dispatch_started_at IS NULL OR p_completed_at IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL OR p_idempotency_key IS NULL
        OR btrim(p_idempotency_key) = ''
        OR p_completed_at < p_dispatch_started_at
        OR (p_first_token_at IS NOT NULL
            AND p_first_token_at NOT BETWEEN p_dispatch_started_at AND p_completed_at)
        OR (p_usage_source = 'confirmed_no_execution' AND (
            v_actual_tokens <> 0
            OR p_cache_read_tokens <> 0 OR p_cache_creation_tokens <> 0
            OR p_thinking_tokens <> 0
            OR p_attempt_status NOT IN ('failed', 'cancelled')
            OR p_error_code IS NULL OR btrim(p_error_code) = ''
            OR p_first_token_at IS NOT NULL
            OR (p_upstream_http_status IS NOT NULL
                AND p_upstream_http_status NOT IN (401, 403, 429))
        ))
        OR (p_request_terminal_status IS NOT NULL
            AND p_request_terminal_status NOT IN ('succeeded', 'failed', 'cancelled')) THEN
        PERFORM poolai_business_error('invalid_usage_attempt');
    END IF;

    IF p_input_tokens > poolai_numeric78_max()
        OR p_output_tokens > poolai_numeric78_max()
        OR v_actual_tokens > poolai_numeric78_max() THEN
        PERFORM poolai_business_error(
            'token_numeric_overflow',
            'The exact upstream fact exceeds the 78-digit contract; transaction was not truncated.'
        );
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

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'settled'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_existing_event.metadata ->> 'request_terminal_status'
                IS DISTINCT FROM p_request_terminal_status
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        SELECT a.* INTO STRICT v_attempt
        FROM usage_attempts a
        WHERE a.attempt_id = p_attempt_id;
        IF v_attempt.account_id <> p_account_id
            OR v_attempt.channel_id <> p_channel_id
            OR v_attempt.provider <> p_provider
            OR v_attempt.model <> p_model
            OR v_attempt.status <> p_attempt_status
            OR v_attempt.upstream_http_status IS DISTINCT FROM p_upstream_http_status
            OR v_attempt.error_code IS DISTINCT FROM p_error_code
            OR v_attempt.input_tokens <> p_input_tokens
            OR v_attempt.output_tokens <> p_output_tokens
            OR v_attempt.cache_read_tokens <> p_cache_read_tokens
            OR v_attempt.cache_creation_tokens <> p_cache_creation_tokens
            OR v_attempt.thinking_tokens <> p_thinking_tokens
            OR v_attempt.usage_source <> p_usage_source
            OR v_attempt.upstream_request_id IS DISTINCT FROM p_upstream_request_id
            OR v_attempt.raw_upstream_usage IS DISTINCT FROM p_raw_upstream_usage
            OR v_attempt.dispatch_started_at <> p_dispatch_started_at
            OR v_attempt.first_token_at IS DISTINCT FROM p_first_token_at
            OR v_attempt.completed_at <> p_completed_at THEN
            PERFORM poolai_business_error('attempt_already_settled_with_different_usage');
        END IF;
        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
            poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
        RETURN;
    END IF;

    IF v_reservation.status = 'settled' THEN
        SELECT a.* INTO v_attempt FROM usage_attempts a WHERE a.attempt_id = p_attempt_id;
        IF FOUND
            AND v_attempt.account_id = p_account_id
            AND v_attempt.channel_id = p_channel_id
            AND v_attempt.provider = p_provider
            AND v_attempt.model = p_model
            AND v_attempt.status = p_attempt_status
            AND v_attempt.upstream_http_status IS NOT DISTINCT FROM p_upstream_http_status
            AND v_attempt.error_code IS NOT DISTINCT FROM p_error_code
            AND v_attempt.input_tokens = p_input_tokens
            AND v_attempt.output_tokens = p_output_tokens
            AND v_attempt.cache_read_tokens = p_cache_read_tokens
            AND v_attempt.cache_creation_tokens = p_cache_creation_tokens
            AND v_attempt.thinking_tokens = p_thinking_tokens
            AND v_attempt.usage_source = p_usage_source
            AND v_attempt.upstream_request_id IS NOT DISTINCT FROM p_upstream_request_id
            AND v_attempt.raw_upstream_usage IS NOT DISTINCT FROM p_raw_upstream_usage
            AND v_attempt.dispatch_started_at = p_dispatch_started_at
            AND v_attempt.first_token_at IS NOT DISTINCT FROM p_first_token_at
            AND v_attempt.completed_at = p_completed_at THEN
            RETURN QUERY SELECT
                v_reservation.id, v_period.id, v_reservation.status,
                v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
                poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
            RETURN;
        END IF;
        PERFORM poolai_business_error('attempt_already_settled_with_different_usage');
    END IF;

    IF v_reservation.status = 'expired'
        AND v_reservation.dispatch_started_at IS NOT NULL THEN
        PERFORM poolai_business_error(
            'reservation_terminal_use_adjust_usage',
            'Correct the conservative expiry fact with poolai_quota_adjust_usage.'
        );
    END IF;
    IF v_reservation.status IN ('released', 'expired') THEN
        PERFORM poolai_business_error(
            'reservation_terminal_without_dispatch',
            'A pre-dispatch terminal reservation has no upstream usage to settle.'
        );
    END IF;
    IF v_reservation.status <> 'pending' THEN
        PERFORM poolai_business_error('reservation_not_pending');
    END IF;
    IF v_reservation.dispatch_started_at IS NULL THEN
        PERFORM poolai_business_error(
            'reservation_not_dispatched',
            'Mark the persistent dispatch boundary before calling the upstream.'
        );
    END IF;
    IF p_dispatch_started_at <> v_reservation.dispatch_started_at THEN
        PERFORM poolai_business_error('reservation_dispatch_timestamp_mismatch');
    END IF;
    IF v_reservation.account_id <> p_account_id OR v_reservation.channel_id <> p_channel_id THEN
        PERFORM poolai_business_error('reservation_route_mismatch');
    END IF;
    IF v_reservation.dispatch_provider <> p_provider
        OR v_reservation.dispatch_model <> p_model THEN
        PERFORM poolai_business_error('reservation_dispatch_identity_mismatch');
    END IF;
    PERFORM 1
    FROM accounts a
    JOIN channels c ON c.id = p_channel_id
    WHERE a.id = p_account_id
      AND a.provider = p_provider
      AND c.provider = p_provider
    FOR SHARE OF a, c;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('reservation_provider_mismatch');
    END IF;

    -- Sample wall-clock time only after the final route/provider row locks.
    v_now := clock_timestamp();
    IF v_period.reserved_tokens < v_reservation.estimated_tokens THEN
        PERFORM poolai_business_error('quota_counter_invariant_broken');
    END IF;
    IF v_period.consumed_tokens + v_actual_tokens > poolai_numeric78_max() THEN
        PERFORM poolai_business_error(
            'token_numeric_overflow',
            'The period counter would exceed 78 digits; transaction was not truncated.'
        );
    END IF;

    INSERT INTO usage_attempts (
        attempt_id, request_id, attempt_index, reservation_id,
        quota_group_id, routing_group_id, account_id, channel_id,
        provider, model, status, upstream_http_status, error_code,
        input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
        thinking_tokens, usage_source, is_estimated, upstream_request_id,
        raw_upstream_usage, dispatch_started_at, first_token_at, completed_at, created_at
    ) VALUES (
        p_attempt_id, v_reservation.request_id, v_reservation.attempt_index, v_reservation.id,
        p_group_id, p_group_id, p_account_id, p_channel_id,
        p_provider, p_model, p_attempt_status, p_upstream_http_status, p_error_code,
        p_input_tokens, p_output_tokens, p_cache_read_tokens, p_cache_creation_tokens,
        p_thinking_tokens, p_usage_source,
        p_usage_source NOT IN ('upstream', 'confirmed_no_execution'), p_upstream_request_id,
        p_raw_upstream_usage, p_dispatch_started_at, p_first_token_at, p_completed_at, v_now
    )
    RETURNING * INTO v_attempt;

    UPDATE group_quota_periods p
    SET consumed_tokens = p.consumed_tokens + v_actual_tokens,
        reserved_tokens = p.reserved_tokens - v_reservation.estimated_tokens,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_period.id
    RETURNING p.* INTO v_period;

    UPDATE group_token_reservations r
    SET status = 'settled',
        actual_tokens = v_actual_tokens,
        usage_source = p_usage_source,
        settled_at = v_now,
        updated_at = v_now
    WHERE r.id = v_reservation.id AND r.status = 'pending'
    RETURNING r.* INTO STRICT v_reservation;

    IF p_request_terminal_status IS NULL THEN
        UPDATE usage_requests r
        SET status = CASE WHEN r.status = 'accepted' THEN 'in_progress' ELSE r.status END,
            attempt_count = r.attempt_count + 1,
            effective_model = coalesce(r.effective_model, p_model)
        WHERE r.request_id = v_reservation.request_id
          AND r.status IN ('accepted', 'in_progress');
    ELSE
        UPDATE usage_requests r
        SET status = p_request_terminal_status,
            attempt_count = r.attempt_count + 1,
            final_attempt_id = p_attempt_id,
            effective_model = coalesce(r.effective_model, p_model),
            error_code = CASE WHEN p_request_terminal_status = 'succeeded' THEN NULL ELSE p_error_code END,
            completed_at = p_completed_at
        WHERE r.request_id = v_reservation.request_id
          AND r.status IN ('accepted', 'in_progress');
    END IF;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('usage_request_already_terminal');
    END IF;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, 'settled',
        0, v_actual_tokens, -v_reservation.estimated_tokens,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'gateway', NULL, p_idempotency_key, NULL,
        jsonb_build_object(
            'request_id', v_reservation.request_id,
            'attempt_index', v_reservation.attempt_index,
            'account_id', p_account_id,
            'channel_id', p_channel_id,
            'attempt_status', p_attempt_status,
            'request_terminal_status', p_request_terminal_status,
            'usage_source', p_usage_source,
            'input_tokens', p_input_tokens::text,
            'output_tokens', p_output_tokens::text,
            'actual_tokens', v_actual_tokens::text
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens);
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_initialize(
    p_group_id uuid,
    p_period_id uuid,
    p_total_tokens numeric,
    p_event_id uuid,
    p_outbox_id uuid,
    p_actor_user_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_period_id uuid,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric,
    result_quota_version bigint
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_quota_found boolean;
    v_now timestamptz;
BEGIN
    IF p_group_id IS NULL OR p_period_id IS NULL OR p_total_tokens IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL OR p_actor_user_id IS NULL
        OR p_idempotency_key IS NULL OR btrim(p_idempotency_key) = ''
        OR p_total_tokens NOT BETWEEN 1 AND 9007199254740991
        OR p_total_tokens <> trunc(p_total_tokens)
        OR p_reason IS NULL OR btrim(p_reason) = '' THEN
        PERFORM poolai_business_error('invalid_quota_initialization');
    END IF;

    -- Initialization has no quota row yet, so the Group row is its serialization point.
    PERFORM 1 FROM groups g WHERE g.id = p_group_id AND g.status <> 'archived' FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('group_not_found_or_archived');
    END IF;

    SELECT q.* INTO v_quota
    FROM group_token_quotas q
    WHERE q.group_id = p_group_id
    FOR UPDATE;
    v_quota_found := FOUND;

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;

    IF v_quota_found THEN
        IF v_existing_event.id = p_event_id
            AND v_existing_event.event_type = 'initialized'
            AND v_existing_event.group_id = p_group_id
            AND v_existing_event.period_id = p_period_id
            AND v_existing_event.total_tokens_after = p_total_tokens
            AND v_existing_event.actor_user_id = p_actor_user_id
            AND v_existing_event.reason = p_reason
            AND EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            SELECT p.* INTO STRICT v_period
            FROM group_quota_periods p
            WHERE p.id = v_existing_event.period_id AND p.group_id = p_group_id
            FOR UPDATE;

            RETURN QUERY SELECT
                v_period.id, v_period.total_tokens, v_period.consumed_tokens,
                v_period.reserved_tokens,
                poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens),
                v_quota.version;
            RETURN;
        END IF;

        IF v_existing_event.idempotency_key = p_idempotency_key THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;

        PERFORM poolai_business_error('group_quota_already_initialized');
    END IF;

    -- A key may already belong to another aggregate even though this Group has
    -- not been initialized yet.
    IF v_existing_event.idempotency_key = p_idempotency_key THEN
        PERFORM poolai_business_error('idempotency_key_reused');
    END IF;

    v_now := clock_timestamp();

    INSERT INTO group_token_quotas (
        group_id, current_period_id, enabled, version, created_at, updated_at
    ) VALUES (
        p_group_id, p_period_id, true, 1, v_now, v_now
    );

    INSERT INTO group_quota_periods (
        id, group_id, period_number, total_tokens, consumed_tokens, reserved_tokens,
        status, opened_at, version, created_at, updated_at
    ) VALUES (
        p_period_id, p_group_id, 1, p_total_tokens, 0, 0,
        'current', v_now, 1, v_now, v_now
    )
    RETURNING * INTO v_period;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, p_period_id, NULL, NULL,
        'initialized', p_total_tokens, 0, 0,
        p_total_tokens, 0, 0,
        'admin', p_actor_user_id, p_idempotency_key, p_reason,
        jsonb_build_object('period_number', 1, 'period_id', p_period_id)
    );

    RETURN QUERY SELECT
        v_period.id, v_period.total_tokens, v_period.consumed_tokens,
        v_period.reserved_tokens, v_period.total_tokens,
        1::bigint;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_reserve(
    p_reservation_id uuid,
    p_attempt_id uuid,
    p_request_id uuid,
    p_attempt_index integer,
    p_user_id uuid,
    p_api_key_id uuid,
    p_subscription_id uuid,
    p_group_id uuid,
    p_account_id uuid,
    p_channel_id uuid,
    p_estimated_tokens numeric,
    p_is_streaming boolean,
    p_lease_owner text,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_total_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric,
    result_remaining_tokens numeric,
    result_lease_expires_at timestamptz,
    result_max_expires_at timestamptz
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_existing_period_id uuid;
    v_configured_channel_id uuid;
    v_now timestamptz;
    v_lease_expires_at timestamptz;
    v_max_expires_at timestamptz;
BEGIN
    IF p_reservation_id IS NULL OR p_attempt_id IS NULL OR p_request_id IS NULL
        OR p_user_id IS NULL OR p_api_key_id IS NULL OR p_subscription_id IS NULL
        OR p_group_id IS NULL OR p_account_id IS NULL OR p_channel_id IS NULL
        OR p_attempt_index IS NULL OR p_estimated_tokens IS NULL OR p_is_streaming IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL OR p_idempotency_key IS NULL
        OR btrim(p_idempotency_key) = ''
        OR p_attempt_index < 0 OR p_estimated_tokens NOT BETWEEN 1 AND 9007199254740991
        OR p_estimated_tokens <> trunc(p_estimated_tokens)
        OR p_lease_owner IS NULL OR btrim(p_lease_owner) = '' THEN
        PERFORM poolai_business_error('invalid_quota_reservation');
    END IF;

    -- Serialization and fixed lock-order root.
    SELECT q.* INTO v_quota
    FROM group_token_quotas q
    WHERE q.group_id = p_group_id
    FOR UPDATE;

    IF NOT FOUND OR v_quota.enabled = false THEN
        PERFORM poolai_business_error('group_quota_disabled');
    END IF;

    -- Lock canonical identities without status/time filters. A fresh database time
    -- is sampled only after these rows and the selected period are linearized.
    PERFORM 1
    FROM usage_requests r
    JOIN users u ON u.id = r.user_id
    JOIN user_roles ur ON ur.user_id = u.id
    JOIN api_keys k
      ON (k.id, k.user_id, k.group_id) = (r.api_key_id, r.user_id, r.quota_group_id)
    JOIN subscriptions s
      ON (s.id, s.user_id, s.group_id) = (r.subscription_id, r.user_id, r.quota_group_id)
    JOIN groups g ON g.id = r.quota_group_id
    WHERE r.request_id = p_request_id
      AND r.user_id = p_user_id
      AND r.api_key_id = p_api_key_id
      AND r.subscription_id = p_subscription_id
      AND r.quota_group_id = p_group_id
      AND r.routing_group_id = p_group_id
    FOR SHARE OF r, u, ur, k, s, g;

    IF NOT FOUND THEN
        PERFORM poolai_business_error('admission_identity_mismatch');
    END IF;

    -- attempt_id is the retry identity. Replays return the original reservation,
    -- even if access was revoked or Supply changed after the original reservation
    -- committed. Replays therefore return before locking current Supply rows.
    SELECT r.period_id INTO v_existing_period_id
    FROM group_token_reservations r
    WHERE r.group_id = p_group_id AND r.attempt_id = p_attempt_id;

    IF FOUND THEN
        SELECT p.* INTO STRICT v_period
        FROM group_quota_periods p
        WHERE p.id = v_existing_period_id AND p.group_id = p_group_id
        FOR UPDATE;

        SELECT r.* INTO STRICT v_reservation
        FROM group_token_reservations r
        WHERE r.group_id = p_group_id AND r.attempt_id = p_attempt_id
        FOR UPDATE;

        IF v_reservation.id <> p_reservation_id
            OR v_reservation.request_id <> p_request_id
            OR v_reservation.attempt_index <> p_attempt_index
            OR v_reservation.account_id <> p_account_id
            OR v_reservation.channel_id <> p_channel_id
            OR v_reservation.estimated_tokens <> p_estimated_tokens
            OR v_reservation.is_streaming <> p_is_streaming
            OR v_reservation.lease_owner <> p_lease_owner THEN
            PERFORM poolai_business_error('attempt_id_reused_with_different_reservation');
        END IF;

        SELECT e.* INTO v_existing_event
        FROM group_quota_events e
        WHERE e.idempotency_key = p_idempotency_key;
        IF NOT FOUND
            OR v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'reserved'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_existing_event.metadata ->> 'request_id' IS DISTINCT FROM p_request_id::text
            OR v_existing_event.metadata ->> 'user_id' IS DISTINCT FROM p_user_id::text
            OR v_existing_event.metadata ->> 'api_key_id' IS DISTINCT FROM p_api_key_id::text
            OR v_existing_event.metadata ->> 'subscription_id' IS DISTINCT FROM p_subscription_id::text
            OR v_existing_event.metadata ->> 'lease_owner' IS DISTINCT FROM p_lease_owner
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;

        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
            poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens),
            v_reservation.lease_expires_at, v_reservation.max_expires_at;
        RETURN;
    END IF;

    PERFORM 1
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        PERFORM poolai_business_error('idempotency_key_reused');
    END IF;

    -- A new attempt locks the current Supply aggregate in its ownership order:
    -- configuration -> binding/Account -> configured Channel. The configuration
    -- row lock freezes channel_id before equality is checked and before period is
    -- locked; historical replays above deliberately skip this current-state gate.
    SELECT sc.channel_id
    INTO v_configured_channel_id
    FROM group_supply_configurations sc
    WHERE sc.group_id = p_group_id
    FOR SHARE;

    IF NOT FOUND OR v_configured_channel_id IS NULL THEN
        PERFORM poolai_business_error('no_available_account');
    END IF;

    PERFORM 1
    FROM group_accounts ga
    JOIN accounts a ON a.id = ga.account_id
    WHERE ga.group_id = p_group_id
      AND ga.account_id = p_account_id
    FOR SHARE OF ga, a;

    IF NOT FOUND THEN
        PERFORM poolai_business_error('no_available_account');
    END IF;

    PERFORM 1
    FROM channels c
    WHERE c.id = v_configured_channel_id
    FOR SHARE;

    IF NOT FOUND OR v_configured_channel_id IS DISTINCT FROM p_channel_id THEN
        PERFORM poolai_business_error('no_available_account');
    END IF;

    SELECT p.* INTO v_period
    FROM group_quota_periods p
    WHERE p.id = v_quota.current_period_id AND p.group_id = p_group_id
    FOR UPDATE;

    IF NOT FOUND OR v_period.status <> 'current' THEN
        PERFORM poolai_business_error('group_quota_period_not_current');
    END IF;

    v_now := clock_timestamp();

    -- Re-read the locked rows with status/time predicates at the linearization
    -- instant. Each failure maps to a stable public admission category; no
    -- positive Redis cache can substitute for these checks.
    PERFORM 1
    FROM usage_requests r
    WHERE r.request_id = p_request_id
      AND r.user_id = p_user_id
      AND r.api_key_id = p_api_key_id
      AND r.subscription_id = p_subscription_id
      AND r.quota_group_id = p_group_id
      AND r.routing_group_id = p_group_id
      AND r.status IN ('accepted', 'in_progress');

    IF NOT FOUND THEN
        PERFORM poolai_business_error('admission_request_not_active');
    END IF;

    PERFORM 1
    FROM users u
    JOIN user_roles ur ON ur.user_id = u.id
    JOIN api_keys k
      ON (k.user_id, k.group_id) = (u.id, p_group_id)
    WHERE u.id = p_user_id
      AND k.id = p_api_key_id
      AND u.status = 'active' AND u.deleted_at IS NULL
      AND (u.locked_until IS NULL OR u.locked_until <= v_now)
      AND k.status = 'active'
      AND (k.expires_at IS NULL OR k.expires_at > v_now);

    IF NOT FOUND THEN
        PERFORM poolai_business_error('invalid_api_key');
    END IF;

    PERFORM 1
    FROM subscriptions s
    WHERE s.id = p_subscription_id
      AND s.user_id = p_user_id
      AND s.group_id = p_group_id
      AND s.status = 'active'
      AND s.starts_at <= v_now
      AND s.expires_at > v_now;

    IF NOT FOUND THEN
        PERFORM poolai_business_error('subscription_inactive');
    END IF;

    PERFORM 1
    FROM groups g
    WHERE g.id = p_group_id
      AND g.status = 'active'
      AND g.deleted_at IS NULL;

    IF NOT FOUND THEN
        PERFORM poolai_business_error('group_disabled');
    END IF;

    PERFORM 1
    FROM group_supply_configurations sc
    JOIN group_accounts ga
      ON ga.group_id = sc.group_id AND ga.account_id = p_account_id
    JOIN accounts a ON a.id = ga.account_id
    JOIN channels c ON c.id = sc.channel_id
    WHERE sc.group_id = p_group_id
      AND sc.channel_id = p_channel_id
      AND ga.is_enabled = true
      AND a.status = 'active' AND a.deleted_at IS NULL
      AND a.last_health_status IN ('healthy', 'degraded')
      AND (a.upstream_rate_limited_until IS NULL OR a.upstream_rate_limited_until <= v_now)
      AND c.status = 'active' AND c.deleted_at IS NULL
      AND a.provider = c.provider;

    IF NOT FOUND THEN
        PERFORM poolai_business_error('no_available_account');
    END IF;

    IF v_period.consumed_tokens >= v_period.total_tokens THEN
        PERFORM poolai_business_error('group_quota_exhausted');
    END IF;

    IF v_period.consumed_tokens + p_estimated_tokens > v_period.total_tokens THEN
        PERFORM poolai_business_error('group_quota_insufficient');
    END IF;

    IF v_period.consumed_tokens + v_period.reserved_tokens + p_estimated_tokens
        > v_period.total_tokens THEN
        PERFORM poolai_business_error(
            'group_quota_reserved',
            'The unconsumed capacity is temporarily held by in-flight attempts.'
        );
    END IF;

    IF p_is_streaming THEN
        v_lease_expires_at := v_now + interval '120 seconds';
        v_max_expires_at := v_now + interval '2 hours';
    ELSE
        v_lease_expires_at := v_now + interval '5 minutes';
        v_max_expires_at := v_now + interval '10 minutes';
    END IF;

    UPDATE group_quota_periods p
    SET reserved_tokens = p.reserved_tokens + p_estimated_tokens,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_period.id
    RETURNING p.* INTO v_period;

    INSERT INTO group_token_reservations (
        id, period_id, group_id, request_id, attempt_id, attempt_index,
        account_id, channel_id, estimated_tokens, status, is_streaming,
        lease_owner, lease_expires_at, max_expires_at, created_at, updated_at
    ) VALUES (
        p_reservation_id, v_period.id, p_group_id, p_request_id, p_attempt_id, p_attempt_index,
        p_account_id, p_channel_id, p_estimated_tokens, 'pending', p_is_streaming,
        p_lease_owner, v_lease_expires_at, v_max_expires_at, v_now, v_now
    )
    RETURNING * INTO v_reservation;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, 'reserved',
        0, 0, p_estimated_tokens,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'gateway', NULL, p_idempotency_key, NULL,
        jsonb_build_object(
            'request_id', p_request_id,
            'user_id', p_user_id,
            'api_key_id', p_api_key_id,
            'subscription_id', p_subscription_id,
            'attempt_index', p_attempt_index,
            'account_id', p_account_id,
            'channel_id', p_channel_id,
            'estimated_tokens', p_estimated_tokens,
            'is_streaming', p_is_streaming,
            'lease_owner', p_lease_owner,
            'lease_expires_at', v_lease_expires_at,
            'max_expires_at', v_max_expires_at
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        poolai_quota_remaining(v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens),
        v_reservation.lease_expires_at, v_reservation.max_expires_at;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_mark_dispatched(
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
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_period_id uuid;
    v_now timestamptz;
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

    UPDATE group_token_reservations r
    SET dispatch_started_at = v_now,
        dispatch_provider = p_provider,
        dispatch_model = p_model,
        estimated_input_tokens = p_estimated_input_tokens,
        estimated_output_tokens = p_estimated_output_tokens,
        updated_at = v_now
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
            'dispatch_started_at', v_now
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_reservation.dispatch_started_at,
        v_reservation.lease_expires_at, v_reservation.max_expires_at;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_renew(
    p_group_id uuid,
    p_attempt_id uuid,
    p_lease_owner text,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_status text,
    result_lease_expires_at timestamptz,
    result_max_expires_at timestamptz
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_period_id uuid;
    v_now timestamptz;
    v_new_expiry timestamptz;
BEGIN
    IF p_group_id IS NULL OR p_attempt_id IS NULL OR p_lease_owner IS NULL
        OR btrim(p_lease_owner) = ''
        OR p_event_id IS NULL OR p_outbox_id IS NULL
        OR p_idempotency_key IS NULL OR btrim(p_idempotency_key) = '' THEN
        PERFORM poolai_business_error('invalid_reservation_renewal');
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

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'renewed'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_existing_event.metadata ->> 'lease_owner' IS DISTINCT FROM p_lease_owner
            OR v_existing_event.metadata ->> 'lease_expires_at' IS NULL
            OR v_existing_event.metadata ->> 'max_expires_at' IS NULL
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        RETURN QUERY SELECT
            v_reservation.id, v_existing_event.period_id, 'pending'::text,
            (v_existing_event.metadata ->> 'lease_expires_at')::timestamptz,
            (v_existing_event.metadata ->> 'max_expires_at')::timestamptz;
        RETURN;
    END IF;

    -- Lease validity is evaluated at a fresh time sampled after every
    -- linearization lock, never at transaction or statement start.
    v_now := clock_timestamp();

    IF v_reservation.status <> 'pending' THEN
        PERFORM poolai_business_error('reservation_not_pending');
    END IF;
    IF v_reservation.lease_owner <> p_lease_owner THEN
        PERFORM poolai_business_error('reservation_owner_mismatch');
    END IF;
    IF v_reservation.lease_expires_at <= v_now THEN
        PERFORM poolai_business_error('reservation_lease_expired');
    END IF;
    IF v_reservation.max_expires_at <= v_now THEN
        PERFORM poolai_business_error('reservation_max_lifetime_reached');
    END IF;

    IF v_reservation.is_streaming THEN
        v_new_expiry := least(v_now + interval '120 seconds', v_reservation.max_expires_at);
    ELSE
        v_new_expiry := least(v_now + interval '5 minutes', v_reservation.max_expires_at);
    END IF;

    UPDATE group_token_reservations r
    SET lease_expires_at = v_new_expiry,
        updated_at = v_now
    WHERE r.id = v_reservation.id AND r.status = 'pending'
    RETURNING r.* INTO STRICT v_reservation;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, 'renewed',
        0, 0, 0,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'gateway', NULL, p_idempotency_key, NULL,
        jsonb_build_object(
            'lease_owner', p_lease_owner,
            'lease_expires_at', v_new_expiry,
            'max_expires_at', v_reservation.max_expires_at
        )
    );

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_reservation.lease_expires_at, v_reservation.max_expires_at;
END;
$function$;

CREATE OR REPLACE FUNCTION poolai_quota_adjust_usage(
    p_group_id uuid,
    p_attempt_id uuid,
    p_account_id uuid,
    p_channel_id uuid,
    p_provider text,
    p_model text,
    p_attempt_status text,
    p_upstream_http_status integer,
    p_error_code text,
    p_corrected_input_tokens numeric,
    p_corrected_output_tokens numeric,
    p_corrected_cache_read_tokens numeric,
    p_corrected_cache_creation_tokens numeric,
    p_corrected_thinking_tokens numeric,
    p_usage_source text,
    p_upstream_request_id text,
    p_raw_upstream_usage jsonb,
    p_dispatch_started_at timestamptz,
    p_first_token_at timestamptz,
    p_completed_at timestamptz,
    p_request_terminal_status text,
    p_event_id uuid,
    p_outbox_id uuid,
    p_idempotency_key text,
    p_reason text
)
RETURNS TABLE (
    result_reservation_id uuid,
    result_period_id uuid,
    result_reservation_status text,
    result_previous_tokens numeric,
    result_corrected_tokens numeric,
    result_delta_tokens numeric,
    result_consumed_tokens numeric,
    result_reserved_tokens numeric
)
LANGUAGE plpgsql
AS $function$
DECLARE
    v_quota group_token_quotas%ROWTYPE;
    v_period group_quota_periods%ROWTYPE;
    v_reservation group_token_reservations%ROWTYPE;
    v_attempt usage_attempts%ROWTYPE;
    v_adjustment usage_attempt_adjustments%ROWTYPE;
    v_existing_event group_quota_events%ROWTYPE;
    v_period_id uuid;
    v_previous numeric;
    v_corrected numeric;
    v_delta numeric;
    v_now timestamptz;
BEGIN
    v_corrected := coalesce(p_corrected_input_tokens, -1)
        + coalesce(p_corrected_output_tokens, -1);

    IF p_group_id IS NULL OR p_attempt_id IS NULL OR p_account_id IS NULL
        OR p_channel_id IS NULL OR p_provider IS NULL
        OR p_provider NOT IN ('openai', 'openai_compatible')
        OR p_model IS NULL OR btrim(p_model) = ''
        OR p_attempt_status IS NULL
        OR p_attempt_status NOT IN ('succeeded', 'failed', 'cancelled')
        OR p_corrected_input_tokens IS NULL OR p_corrected_input_tokens < 0
        OR p_corrected_input_tokens <> trunc(p_corrected_input_tokens)
        OR p_corrected_output_tokens IS NULL OR p_corrected_output_tokens < 0
        OR p_corrected_output_tokens <> trunc(p_corrected_output_tokens)
        OR p_corrected_cache_read_tokens IS NULL
        OR p_corrected_cache_read_tokens NOT BETWEEN 0 AND p_corrected_input_tokens
        OR p_corrected_cache_read_tokens <> trunc(p_corrected_cache_read_tokens)
        OR p_corrected_cache_creation_tokens IS NULL
        OR p_corrected_cache_creation_tokens NOT BETWEEN 0 AND p_corrected_input_tokens
        OR p_corrected_cache_creation_tokens <> trunc(p_corrected_cache_creation_tokens)
        OR p_corrected_thinking_tokens IS NULL
        OR p_corrected_thinking_tokens NOT BETWEEN 0 AND p_corrected_output_tokens
        OR p_corrected_thinking_tokens <> trunc(p_corrected_thinking_tokens)
        OR p_corrected_cache_read_tokens + p_corrected_cache_creation_tokens
            > p_corrected_input_tokens
        OR p_usage_source IS NULL
        OR p_usage_source NOT IN (
            'upstream', 'local_tokenizer', 'conservative_estimate',
            'confirmed_no_execution'
        )
        OR p_dispatch_started_at IS NULL OR p_completed_at IS NULL
        OR p_event_id IS NULL OR p_outbox_id IS NULL
        OR p_idempotency_key IS NULL OR btrim(p_idempotency_key) = ''
        OR p_completed_at < p_dispatch_started_at
        OR (p_first_token_at IS NOT NULL
            AND p_first_token_at NOT BETWEEN p_dispatch_started_at AND p_completed_at)
        OR (p_usage_source = 'confirmed_no_execution' AND (
            v_corrected <> 0
            OR p_corrected_cache_read_tokens <> 0
            OR p_corrected_cache_creation_tokens <> 0
            OR p_corrected_thinking_tokens <> 0
            OR p_attempt_status NOT IN ('failed', 'cancelled')
            OR p_error_code IS NULL OR btrim(p_error_code) = ''
            OR p_first_token_at IS NOT NULL
            OR (p_upstream_http_status IS NOT NULL
                AND p_upstream_http_status NOT IN (401, 403, 429))
        ))
        OR (p_request_terminal_status IS NOT NULL
            AND p_request_terminal_status NOT IN ('succeeded', 'failed', 'cancelled'))
        OR p_reason IS NULL OR btrim(p_reason) = '' THEN
        PERFORM poolai_business_error('invalid_usage_adjustment');
    END IF;
    IF v_corrected > poolai_numeric78_max() THEN
        PERFORM poolai_business_error(
            'token_numeric_overflow',
            'The exact corrected fact exceeds the 78-digit contract; transaction was not truncated.'
        );
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

    SELECT a.* INTO v_adjustment
    FROM usage_attempt_adjustments a
    WHERE a.attempt_id = p_attempt_id;
    IF FOUND THEN
        SELECT e.* INTO STRICT v_existing_event
        FROM group_quota_events e
        WHERE e.id = v_adjustment.quota_event_id;

        SELECT a.* INTO STRICT v_attempt
        FROM usage_attempts a
        WHERE a.attempt_id = p_attempt_id;
        IF v_adjustment.corrected_input_tokens <> p_corrected_input_tokens
            OR v_adjustment.corrected_output_tokens <> p_corrected_output_tokens
            OR v_adjustment.corrected_cache_read_tokens <> p_corrected_cache_read_tokens
            OR v_adjustment.corrected_cache_creation_tokens <> p_corrected_cache_creation_tokens
            OR v_adjustment.corrected_thinking_tokens <> p_corrected_thinking_tokens
            OR v_adjustment.usage_source <> p_usage_source
            OR v_attempt.account_id <> p_account_id
            OR v_attempt.channel_id <> p_channel_id
            OR v_attempt.provider <> p_provider
            OR v_attempt.model <> p_model
            OR v_attempt.status <> p_attempt_status
            OR v_attempt.upstream_http_status IS DISTINCT FROM p_upstream_http_status
            OR v_attempt.error_code IS DISTINCT FROM p_error_code
            OR v_attempt.upstream_request_id IS DISTINCT FROM p_upstream_request_id
            OR v_attempt.dispatch_started_at <> p_dispatch_started_at
            OR v_attempt.first_token_at IS DISTINCT FROM p_first_token_at
            OR v_attempt.completed_at <> p_completed_at
            OR v_adjustment.reason <> p_reason
            OR v_adjustment.raw_upstream_usage IS DISTINCT FROM p_raw_upstream_usage
            OR v_existing_event.id <> p_event_id
            OR v_existing_event.idempotency_key <> p_idempotency_key
            OR v_existing_event.event_type <> 'usage_adjusted'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id
            OR v_existing_event.metadata ->> 'request_terminal_status'
                IS DISTINCT FROM p_request_terminal_status
            OR NOT EXISTS (
                SELECT 1
                FROM outbox_messages o
                WHERE o.id = p_outbox_id
                  AND o.payload ->> 'event_id' = p_event_id::text
            ) THEN
            PERFORM poolai_business_error('attempt_adjustment_already_exists_with_different_usage');
        END IF;
        RETURN QUERY SELECT
            v_reservation.id, v_period.id, v_reservation.status,
            v_adjustment.previous_total_tokens, v_adjustment.corrected_total_tokens,
            v_adjustment.delta_tokens, v_period.consumed_tokens, v_period.reserved_tokens;
        RETURN;
    END IF;

    SELECT e.* INTO v_existing_event
    FROM group_quota_events e
    WHERE e.idempotency_key = p_idempotency_key;
    IF FOUND THEN
        IF v_existing_event.id <> p_event_id
            OR v_existing_event.event_type <> 'usage_adjusted'
            OR v_existing_event.group_id <> p_group_id
            OR v_existing_event.period_id <> v_period.id
            OR v_existing_event.reservation_id <> v_reservation.id
            OR v_existing_event.attempt_id <> p_attempt_id THEN
            PERFORM poolai_business_error('idempotency_key_reused');
        END IF;
        PERFORM poolai_business_error('adjustment_event_without_adjustment_fact');
    END IF;

    IF v_reservation.status = 'pending' THEN
        PERFORM poolai_business_error('pending_reservation_must_be_settled');
    END IF;
    IF v_reservation.dispatch_started_at IS NULL THEN
        PERFORM poolai_business_error(
            'usage_adjustment_requires_dispatch',
            'A pre-dispatch release/expiry has no upstream execution to correct.'
        );
    END IF;
    IF p_dispatch_started_at <> v_reservation.dispatch_started_at THEN
        PERFORM poolai_business_error('reservation_dispatch_timestamp_mismatch');
    END IF;
    IF v_reservation.adjusted_at IS NOT NULL THEN
        PERFORM poolai_business_error('attempt_adjustment_already_exists');
    END IF;
    IF v_reservation.account_id <> p_account_id OR v_reservation.channel_id <> p_channel_id THEN
        PERFORM poolai_business_error('reservation_route_mismatch');
    END IF;
    PERFORM 1
    FROM accounts a
    JOIN channels c ON c.id = p_channel_id
    WHERE a.id = p_account_id
      AND a.provider = p_provider
      AND c.provider = p_provider
    FOR SHARE OF a, c;
    IF NOT FOUND THEN
        PERFORM poolai_business_error('reservation_provider_mismatch');
    END IF;

    -- The adjustment fact and all writes use a sample taken after the final
    -- route/provider row locks.
    v_now := clock_timestamp();

    SELECT a.* INTO v_attempt FROM usage_attempts a WHERE a.attempt_id = p_attempt_id;
    IF FOUND THEN
        IF v_attempt.account_id <> p_account_id
            OR v_attempt.channel_id <> p_channel_id
            OR v_attempt.provider <> p_provider
            OR v_attempt.model <> p_model
            OR v_attempt.status <> p_attempt_status
            OR v_attempt.upstream_http_status IS DISTINCT FROM p_upstream_http_status
            OR v_attempt.error_code IS DISTINCT FROM p_error_code
            OR v_attempt.upstream_request_id IS DISTINCT FROM p_upstream_request_id
            OR v_attempt.dispatch_started_at <> p_dispatch_started_at
            OR v_attempt.first_token_at IS DISTINCT FROM p_first_token_at
            OR v_attempt.completed_at <> p_completed_at THEN
            PERFORM poolai_business_error('attempt_identity_mismatch');
        END IF;
        IF v_reservation.status <> 'settled'
            AND NOT (
                v_reservation.status = 'expired'
                AND v_reservation.dispatch_started_at IS NOT NULL
                AND v_attempt.usage_source = 'conservative_estimate'
            ) THEN
            PERFORM poolai_business_error('terminal_reservation_has_unexpected_attempt_fact');
        END IF;
        v_previous := v_attempt.total_tokens;
    ELSE
        PERFORM poolai_business_error('terminal_reservation_missing_attempt_fact');
    END IF;

    v_delta := v_corrected - v_previous;
    IF v_period.consumed_tokens + v_delta < 0 THEN
        PERFORM poolai_business_error('quota_counter_would_be_negative');
    END IF;
    IF v_period.consumed_tokens + v_delta > poolai_numeric78_max() THEN
        PERFORM poolai_business_error(
            'token_numeric_overflow',
            'The corrected period counter would exceed 78 digits; transaction was not truncated.'
        );
    END IF;

    UPDATE group_quota_periods p
    SET consumed_tokens = p.consumed_tokens + v_delta,
        version = p.version + 1,
        updated_at = v_now
    WHERE p.id = v_period.id
    RETURNING p.* INTO v_period;

    PERFORM poolai_emit_quota_event(
        p_event_id, p_outbox_id, p_group_id, v_period.id,
        v_reservation.id, p_attempt_id, 'usage_adjusted',
        0, v_delta, 0,
        v_period.total_tokens, v_period.consumed_tokens, v_period.reserved_tokens,
        'worker', NULL, p_idempotency_key, p_reason,
        jsonb_build_object(
            'request_id', v_reservation.request_id,
            'attempt_index', v_reservation.attempt_index,
            'account_id', p_account_id,
            'channel_id', p_channel_id,
            'provider', p_provider,
            'model', p_model,
            'attempt_status', p_attempt_status,
            'request_terminal_status', p_request_terminal_status,
            'previous_tokens', v_previous::text,
            'corrected_tokens', v_corrected::text,
            'delta_tokens', v_delta::text,
            'terminal_status_preserved', v_reservation.status,
            'usage_source', p_usage_source
        )
    );

    INSERT INTO usage_attempt_adjustments (
        attempt_id, quota_event_id, previous_total_tokens,
        corrected_input_tokens, corrected_output_tokens,
        corrected_cache_read_tokens, corrected_cache_creation_tokens,
        corrected_thinking_tokens, usage_source, reason,
        raw_upstream_usage, adjusted_at
    ) VALUES (
        p_attempt_id, p_event_id, v_previous,
        p_corrected_input_tokens, p_corrected_output_tokens,
        p_corrected_cache_read_tokens, p_corrected_cache_creation_tokens,
        p_corrected_thinking_tokens, p_usage_source, p_reason,
        p_raw_upstream_usage, v_now
    )
    RETURNING * INTO v_adjustment;

    UPDATE group_token_reservations r
    SET adjusted_at = v_now, updated_at = v_now
    WHERE r.id = v_reservation.id AND r.adjusted_at IS NULL
    RETURNING r.* INTO STRICT v_reservation;

    RETURN QUERY SELECT
        v_reservation.id, v_period.id, v_reservation.status,
        v_previous, v_corrected, v_delta,
        v_period.consumed_tokens, v_period.reserved_tokens;
END;
$function$;
