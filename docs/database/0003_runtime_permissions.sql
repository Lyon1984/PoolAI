-- PoolAI Release 1 runtime privilege boundary for PostgreSQL 18.
--
-- Cluster provisioning creates these roles before PoolAI.Migrator runs. This
-- migration deliberately does not require CREATEROLE and never creates roles.

DO $permission$
DECLARE
    v_owner_can_login boolean;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_runtime_owner')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_api')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_worker') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_runtime_roles_not_provisioned',
            DETAIL = 'Pre-provision poolai_runtime_owner, poolai_api and poolai_worker before migration.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles r
        WHERE r.rolname IN ('poolai_runtime_owner', 'poolai_api', 'poolai_worker')
          AND (r.rolsuper OR r.rolcreaterole OR r.rolcreatedb
              OR r.rolreplication OR r.rolbypassrls)
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_runtime_role_attributes_forbidden',
            DETAIL = 'Runtime roles must be NOSUPERUSER, NOCREATEROLE, NOCREATEDB, NOREPLICATION and NOBYPASSRLS.';
    END IF;

    SELECT r.rolcanlogin INTO STRICT v_owner_can_login
    FROM pg_catalog.pg_roles r
    WHERE r.rolname = 'poolai_runtime_owner';

    IF v_owner_can_login THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_runtime_owner_must_be_nologin';
    END IF;

    IF pg_catalog.pg_has_role('poolai_api', 'poolai_runtime_owner', 'MEMBER')
        OR pg_catalog.pg_has_role('poolai_worker', 'poolai_runtime_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_runtime_role_membership_forbidden',
            DETAIL = 'API and Worker must not be direct or inherited members of poolai_runtime_owner.';
    END IF;
END;
$permission$;

REVOKE CREATE ON SCHEMA public
    FROM PUBLIC, poolai_runtime_owner, poolai_api, poolai_worker;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public
    FROM PUBLIC, poolai_runtime_owner, poolai_api, poolai_worker;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public
    FROM PUBLIC, poolai_runtime_owner, poolai_api, poolai_worker;
REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public
    FROM PUBLIC, poolai_api, poolai_worker;

GRANT USAGE ON SCHEMA public TO poolai_runtime_owner, poolai_api, poolai_worker;
-- ALTER FUNCTION ... OWNER requires the target owner to have CREATE on the
-- containing schema. Grant it only inside this migration transaction, while the
-- NOLOGIN owner is unreachable from API/Worker, and revoke it immediately after
-- every ownership transfer.
GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;

-- All quota entry points execute as this non-login owner. The explicit trusted
-- search path and PUBLIC revocation prevent object-shadowing and direct calls to
-- implementation helpers.
-- Freeze SECURITY DEFINER and search_path while the migration role still owns
-- the functions. Ownership is transferred to the NOLOGIN role immediately
-- afterwards; changing these attributes after the transfer would require an
-- explicit SET ROLE and would fail for the NOINHERIT migrator role.
ALTER FUNCTION public.poolai_quota_initialize(
    uuid, uuid, numeric, uuid, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_reserve(
    uuid, uuid, uuid, integer, uuid, uuid, uuid, uuid, uuid, uuid,
    numeric, boolean, text, uuid, uuid, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_mark_dispatched(
    uuid, uuid, text, text, text, numeric, numeric, uuid, uuid, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_renew(
    uuid, uuid, text, uuid, uuid, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_settle(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_release(
    uuid, uuid, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_expire(
    uuid, uuid, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;
ALTER FUNCTION public.poolai_quota_adjust_usage(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text, text
) SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp;

ALTER FUNCTION public.poolai_business_error(text, text) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_remaining(numeric, numeric, numeric) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_emit_quota_event(
    uuid, uuid, uuid, uuid, uuid, uuid, text,
    numeric, numeric, numeric, numeric, numeric, numeric,
    text, uuid, text, text, jsonb
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_end_pending(
    uuid, uuid, text, text, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_numeric78_max() OWNER TO poolai_runtime_owner;

ALTER FUNCTION public.poolai_quota_initialize(
    uuid, uuid, numeric, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_reserve(
    uuid, uuid, uuid, integer, uuid, uuid, uuid, uuid, uuid, uuid,
    numeric, boolean, text, uuid, uuid, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_mark_dispatched(
    uuid, uuid, text, text, text, numeric, numeric, uuid, uuid, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_renew(
    uuid, uuid, text, uuid, uuid, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_settle(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_release(
    uuid, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_expire(
    uuid, uuid, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;
ALTER FUNCTION public.poolai_quota_adjust_usage(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text, text
) OWNER TO poolai_runtime_owner;

REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

-- The function owner can perform only the writes needed by the quota state
-- machine; it receives no DELETE or DDL capability.
GRANT SELECT (id, status, locked_until, deleted_at) ON users TO poolai_runtime_owner;
GRANT SELECT (user_id) ON user_roles TO poolai_runtime_owner;
GRANT SELECT (id, provider, status, upstream_rate_limited_until,
    last_health_status, deleted_at) ON accounts TO poolai_runtime_owner;
GRANT SELECT (id, provider, status, deleted_at) ON channels TO poolai_runtime_owner;
GRANT SELECT (id, status, deleted_at) ON groups TO poolai_runtime_owner;
GRANT SELECT (group_id, channel_id, version)
    ON group_supply_configurations TO poolai_runtime_owner;
GRANT SELECT (group_id, account_id, is_enabled) ON group_accounts TO poolai_runtime_owner;
GRANT SELECT (id, user_id, group_id, status, starts_at, expires_at)
    ON subscriptions TO poolai_runtime_owner;
GRANT SELECT (id, user_id, group_id, status, expires_at)
    ON api_keys TO poolai_runtime_owner;
GRANT SELECT ON group_token_quotas, group_quota_periods,
    usage_requests, group_token_reservations, group_quota_events,
    usage_attempts, usage_attempt_adjustments
    TO poolai_runtime_owner;
GRANT SELECT (id, payload) ON outbox_messages TO poolai_runtime_owner;
GRANT INSERT, UPDATE ON group_token_quotas, group_quota_periods,
    group_token_reservations TO poolai_runtime_owner;
GRANT UPDATE ON usage_requests TO poolai_runtime_owner;
-- PostgreSQL requires UPDATE privilege on at least one column of every table
-- named in a FOR UPDATE/SHARE locking clause. These key-only grants permit row
-- locking by SECURITY DEFINER functions without exposing general table writes.
GRANT UPDATE (id) ON groups, users, api_keys, subscriptions, accounts, channels
    TO poolai_runtime_owner;
GRANT UPDATE (user_id) ON user_roles TO poolai_runtime_owner;
GRANT UPDATE (group_id) ON group_supply_configurations TO poolai_runtime_owner;
GRANT UPDATE (group_id) ON group_accounts TO poolai_runtime_owner;
GRANT INSERT ON group_quota_events, usage_attempts,
    usage_attempt_adjustments TO poolai_runtime_owner;
GRANT INSERT (
    id, deduplication_key, topic, schema_version,
    aggregate_type, aggregate_id, aggregate_version, event_type,
    source_event_sequence, correlation_id, causation_id,
    payload, occurred_at, next_attempt_at
) ON outbox_messages TO poolai_runtime_owner;
GRANT USAGE, SELECT ON SEQUENCE group_quota_events_event_sequence_seq,
    outbox_messages_event_sequence_seq
    TO poolai_runtime_owner;

-- The single Release 1 API role hosts Identity, Admin and Gateway, so its
-- explicit read set includes only those bounded contexts. Delivery/inbox
-- internals remain invisible; sensitive auth/account/idempotency columns are
-- present solely for their named Identity/Gateway/replay use cases.
GRANT SELECT ON poolai_schema_migrations,
    roles, user_roles, channels, groups, group_supply_configurations,
    group_accounts, subscription_templates,
    subscriptions, group_token_quotas, group_quota_periods,
    usage_requests, group_token_reservations, group_quota_events,
    usage_attempts, usage_attempt_adjustments,
    group_usage_hourly, account_usage_hourly, aggregation_watermarks,
    audit_logs
    TO poolai_api;
GRANT SELECT (
    id, email, normalized_email, display_name, password_hash, status,
    totp_secret_envelope, totp_last_accepted_step, security_stamp,
    token_version, failed_login_count, locked_until, last_login_at,
    version, created_at, updated_at, deleted_at
) ON users TO poolai_api;
GRANT SELECT (
    id, family_id, user_id, parent_session_id, replaced_by_session_id,
    token_hash, pepper_version, status, issued_at, expires_at,
    rotated_at, revoked_at, revoke_reason, ip_address, user_agent, metadata
) ON refresh_sessions TO poolai_api;
GRANT SELECT (
    id, user_id, purpose, token_hash, pepper_version, expires_at,
    used_at, revoked_at, revoke_reason, created_at
) ON one_time_tokens TO poolai_api;
GRANT SELECT (
    id, provider, name, auth_type, upstream_base_url,
    credential_envelope, credential_prefix, credential_hint, settings,
    status, priority, weight, max_concurrency,
    upstream_rate_limited_until, last_health_at, last_health_status,
    version, created_at, updated_at, deleted_at
) ON accounts TO poolai_api;
GRANT SELECT (
    id, user_id, group_id, name, key_prefix, secret_hash, pepper_version,
    status, expires_at, ip_acl, last_used_at, revoked_at, revoke_reason,
    version, created_at, updated_at
) ON api_keys TO poolai_api;
GRANT SELECT (
    scope, idempotency_key, id, actor_fingerprint, request_hash, status,
    response_status, response_body, response_body_envelope, response_headers,
    resource_type, resource_id, lock_owner, lock_generation, locked_until,
    expires_at, version, created_at, updated_at
) ON idempotency_records TO poolai_api;
GRANT INSERT, UPDATE ON users, refresh_sessions, one_time_tokens,
    accounts, channels, groups, subscription_templates,
    subscriptions, api_keys, idempotency_records
    TO poolai_api;
GRANT INSERT (group_id, channel_id)
    ON group_supply_configurations TO poolai_api;
GRANT UPDATE (channel_id, version, updated_at)
    ON group_supply_configurations TO poolai_api;
GRANT INSERT (
    group_id, account_id, priority_override, weight_override, is_enabled
) ON group_accounts TO poolai_api;
GRANT UPDATE (
    priority_override, weight_override, is_enabled, updated_at
) ON group_accounts TO poolai_api;
GRANT INSERT, DELETE ON user_roles TO poolai_api;
GRANT DELETE ON refresh_sessions, one_time_tokens TO poolai_api;
GRANT INSERT (
    id, idempotency_key, message_id, user_id, one_time_token_id,
    recipient_envelope, template_code, template_payload, delivery_secret_envelope
) ON email_outbox TO poolai_api;
GRANT INSERT (
    request_id, user_id, api_key_id, subscription_id,
    quota_group_id, routing_group_id, endpoint, client_request_id,
    requested_model, is_streaming, metadata
) ON usage_requests TO poolai_api;
GRANT INSERT ON audit_logs TO poolai_api;
GRANT INSERT (
    id, deduplication_key, topic, schema_version,
    aggregate_type, aggregate_id, aggregate_version, event_type,
    source_event_sequence, correlation_id, causation_id, payload, occurred_at
) ON outbox_messages TO poolai_api;
GRANT USAGE, SELECT ON SEQUENCE outbox_messages_event_sequence_seq TO poolai_api;

-- Function ACL changes must run as the transferred owner. The migrator has an
-- explicit SET membership in the NOLOGIN owner but does not inherit its rights.
SET LOCAL ROLE poolai_runtime_owner;
GRANT EXECUTE ON FUNCTION public.poolai_quota_initialize(
    uuid, uuid, numeric, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_reset(
    uuid, uuid, numeric, bigint, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_adjust_total(
    uuid, numeric, bigint, uuid, uuid, uuid, text, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_reserve(
    uuid, uuid, uuid, integer, uuid, uuid, uuid, uuid, uuid, uuid,
    numeric, boolean, text, uuid, uuid, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_mark_dispatched(
    uuid, uuid, text, text, text, numeric, numeric, uuid, uuid, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_renew(
    uuid, uuid, text, uuid, uuid, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_settle(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text
) TO poolai_api;
GRANT EXECUTE ON FUNCTION public.poolai_quota_release(
    uuid, uuid, uuid, uuid, text, text
) TO poolai_api;
RESET ROLE;

-- Worker reads are job-specific. Supply Health is the sole credential-bearing
-- Worker use case; User auth, token/API-key and idempotency secrets stay hidden.
GRANT SELECT ON poolai_schema_migrations,
    group_token_quotas, group_quota_periods, group_token_reservations,
    group_quota_events, outbox_messages, inbox_messages,
    group_usage_hourly, account_usage_hourly, aggregation_watermarks
    TO poolai_worker;
GRANT SELECT (
    id, message_id, recipient_envelope, template_code, template_payload,
    delivery_secret_envelope, status, attempts, next_attempt_at,
    lock_owner, lock_generation, locked_until, sent_at, dead_at, last_error,
    created_at, updated_at
) ON email_outbox TO poolai_worker;
GRANT SELECT (
    id, provider, name, auth_type, upstream_base_url, credential_envelope,
    settings, status, priority, weight,
    max_concurrency, upstream_rate_limited_until,
    last_health_at, last_health_status, version, created_at, updated_at, deleted_at
) ON accounts TO poolai_worker;
GRANT SELECT (
    request_id, quota_group_id, routing_group_id, requested_model,
    effective_model, is_streaming, status, attempt_count, final_attempt_id,
    error_code, received_at, completed_at
) ON usage_requests TO poolai_worker;
GRANT SELECT (
    attempt_id, request_id, attempt_index, reservation_id,
    quota_group_id, routing_group_id, account_id, channel_id,
    provider, model, status, upstream_http_status, error_code,
    input_tokens, output_tokens, total_tokens,
    cache_read_tokens, cache_creation_tokens, thinking_tokens,
    usage_source, is_estimated, dispatch_started_at,
    first_token_at, completed_at, created_at
) ON usage_attempts TO poolai_worker;
GRANT SELECT (
    attempt_id, quota_event_id, previous_total_tokens,
    corrected_input_tokens, corrected_output_tokens, corrected_total_tokens,
    corrected_cache_read_tokens, corrected_cache_creation_tokens,
    corrected_thinking_tokens, delta_tokens, usage_source, adjusted_at
) ON usage_attempt_adjustments TO poolai_worker;
GRANT UPDATE (
    status, attempts, next_attempt_at, lock_owner, lock_generation, locked_until,
    recipient_envelope, delivery_secret_envelope,
    sent_at, dead_at, last_error, updated_at
)
    ON email_outbox TO poolai_worker;
GRANT UPDATE (
    status, next_attempt_at, publish_attempts, locked_by, lock_generation, locked_until,
    published_at, dead_at, last_error
)
    ON outbox_messages TO poolai_worker;
GRANT UPDATE (upstream_rate_limited_until, last_health_at, last_health_status, version, updated_at)
    ON accounts TO poolai_worker;
GRANT INSERT, UPDATE, DELETE ON group_usage_hourly, account_usage_hourly
    TO poolai_worker;
GRANT INSERT, UPDATE, DELETE ON aggregation_watermarks TO poolai_worker;
GRANT INSERT ON inbox_messages TO poolai_worker;
GRANT INSERT ON audit_logs TO poolai_worker;
GRANT INSERT (
    id, deduplication_key, topic, schema_version,
    aggregate_type, aggregate_id, aggregate_version, event_type,
    source_event_sequence, correlation_id, causation_id, payload,
    occurred_at, replay_of
) ON outbox_messages TO poolai_worker;
GRANT USAGE, SELECT ON SEQUENCE outbox_messages_event_sequence_seq TO poolai_worker;

SET LOCAL ROLE poolai_runtime_owner;
GRANT EXECUTE ON FUNCTION public.poolai_quota_expire(
    uuid, uuid, uuid, uuid, text, text
) TO poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_quota_adjust_usage(
    uuid, uuid, uuid, uuid, text, text, text, integer, text,
    numeric, numeric, numeric, numeric, numeric, text, text, jsonb,
    timestamptz, timestamptz, timestamptz, text, uuid, uuid, text, text
) TO poolai_worker;
RESET ROLE;

DO $permission_audit$
DECLARE
    v_role text;
    v_forbidden record;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['poolai_runtime_owner', 'poolai_api', 'poolai_worker'] LOOP
        IF pg_catalog.has_schema_privilege(v_role, 'public', 'CREATE') THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'poolai_runtime_schema_create_forbidden',
                DETAIL = format('%s still inherits CREATE on schema public.', v_role);
        END IF;
    END LOOP;

    FOR v_forbidden IN
        SELECT *
        FROM (VALUES
            ('poolai_runtime_owner', 'public.users', 'password_hash'),
            ('poolai_runtime_owner', 'public.users', 'totp_secret_envelope'),
            ('poolai_runtime_owner', 'public.api_keys', 'secret_hash'),
            ('poolai_runtime_owner', 'public.accounts', 'credential_envelope'),
            ('poolai_runtime_owner', 'public.idempotency_records', 'response_body_envelope'),
            ('poolai_worker', 'public.users', 'password_hash'),
            ('poolai_worker', 'public.users', 'totp_secret_envelope'),
            ('poolai_worker', 'public.api_keys', 'secret_hash'),
            ('poolai_worker', 'public.idempotency_records', 'response_body_envelope')
        ) AS forbidden(role_name, table_name, column_name)
    LOOP
        IF pg_catalog.has_column_privilege(
            v_forbidden.role_name,
            v_forbidden.table_name,
            v_forbidden.column_name,
            'SELECT'
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'poolai_runtime_sensitive_column_read_forbidden',
                DETAIL = format(
                    '%s can read %s.%s.',
                    v_forbidden.role_name,
                    v_forbidden.table_name,
                    v_forbidden.column_name
                );
        END IF;
    END LOOP;

    IF pg_catalog.has_column_privilege(
            'poolai_api', 'public.email_outbox', 'delivery_secret_envelope', 'SELECT'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.outbox_messages', 'SELECT'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.inbox_messages', 'SELECT'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_api_delivery_internal_read_forbidden';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_api', 'public.group_supply_configurations', 'DELETE'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.group_accounts', 'DELETE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.group_supply_configurations', 'group_id', 'UPDATE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.group_supply_configurations', 'created_at', 'UPDATE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.group_supply_configurations', 'version', 'INSERT'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.group_accounts', 'group_id', 'UPDATE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.group_accounts', 'account_id', 'UPDATE'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_supply_configurations', 'INSERT'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_supply_configurations', 'UPDATE'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_supply_configurations', 'DELETE'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_accounts', 'INSERT'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_accounts', 'UPDATE'
        )
        OR pg_catalog.has_table_privilege(
            'poolai_worker', 'public.group_accounts', 'DELETE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.group_supply_configurations', 'channel_id', 'UPDATE'
        )
        OR pg_catalog.has_column_privilege(
            'poolai_runtime_owner', 'public.group_supply_configurations', 'version', 'UPDATE'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'poolai_supply_write_boundary_forbidden';
    END IF;
END;
$permission_audit$;
