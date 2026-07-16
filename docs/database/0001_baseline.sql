-- PoolAI Release 1 PostgreSQL 18 baseline.
--
-- Rules carried by this schema:
--   * the application supplies every UUID (UUIDv7); the database has no UUID default;
--   * PostgreSQL is the authority for subscriptions, quota and token facts;
--   * Group is the only cumulative token-quota subject;
--   * cumulative/fact tokens are exact numeric(78,0); public aggregates are decimal strings;
--   * business rows are retired/revoked, not physically deleted;
--   * ledger/fact rows are append-only.
--
-- PoolAI.Migrator executes this whole file and its migration-ledger insert in one
-- transaction. For manual execution use: psql --single-transaction --file ...

CREATE TABLE poolai_schema_migrations (
    version             bigint PRIMARY KEY,
    name                text NOT NULL,
    checksum_sha256     text NOT NULL,
    applied_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_by          text NOT NULL,
    CONSTRAINT uq_poolai_schema_migrations_name UNIQUE (name),
    CONSTRAINT ck_poolai_schema_migrations_version CHECK (version > 0),
    CONSTRAINT ck_poolai_schema_migrations_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_poolai_schema_migrations_checksum CHECK (checksum_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_poolai_schema_migrations_applied_by CHECK (btrim(applied_by) <> '')
);

CREATE TABLE users (
    id                  uuid PRIMARY KEY,
    email               text NOT NULL,
    normalized_email    text NOT NULL,
    display_name        text NOT NULL,
    password_hash       text NOT NULL,
    status              text NOT NULL DEFAULT 'active',
    totp_secret_envelope jsonb,
    totp_last_accepted_step bigint,
    security_stamp      uuid NOT NULL,
    token_version       bigint NOT NULL DEFAULT 1,
    failed_login_count  integer NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    last_login_at       timestamptz,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    deleted_at          timestamptz,
    CONSTRAINT uq_users_normalized_email UNIQUE (normalized_email),
    CONSTRAINT ck_users_email_nonempty CHECK (btrim(email) <> ''),
    CONSTRAINT ck_users_normalized_email CHECK (
        normalized_email = lower(btrim(normalized_email)) AND normalized_email <> ''
    ),
    CONSTRAINT ck_users_display_name CHECK (btrim(display_name) <> ''),
    CONSTRAINT ck_users_status CHECK (status IN ('active', 'disabled')),
    CONSTRAINT ck_users_failed_login_count CHECK (failed_login_count >= 0),
    CONSTRAINT ck_users_token_version CHECK (token_version > 0),
    CONSTRAINT ck_users_version CHECK (version > 0),
    CONSTRAINT ck_users_totp_envelope CHECK (
        totp_secret_envelope IS NULL OR jsonb_typeof(totp_secret_envelope) = 'object'
    ),
    CONSTRAINT ck_users_totp_last_step CHECK (
        totp_last_accepted_step IS NULL OR totp_last_accepted_step >= 0
    ),
    CONSTRAINT ck_users_deleted_status CHECK (deleted_at IS NULL OR status = 'disabled')
);

COMMENT ON CONSTRAINT uq_users_normalized_email ON users IS
    'Email identities are never reused, including after soft deletion.';

CREATE TABLE roles (
    id              uuid PRIMARY KEY,
    code            text NOT NULL,
    display_name    text NOT NULL,
    is_system       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_roles_code UNIQUE (code),
    CONSTRAINT ck_roles_code CHECK (code IN ('admin', 'operator', 'auditor', 'user')),
    CONSTRAINT ck_roles_display_name CHECK (btrim(display_name) <> '')
);

-- Stable system-role IDs are migration-owned UUIDv7-form identifiers. The first
-- administrator is deliberately not seeded; PoolAI.Bootstrap creates it once.
INSERT INTO roles (id, code, display_name, is_system) VALUES
    ('01900000-0000-7000-8000-000000000001', 'admin',    'Administrator', true),
    ('01900000-0000-7000-8000-000000000002', 'operator', 'Operator',      true),
    ('01900000-0000-7000-8000-000000000003', 'auditor',  'Auditor',       true),
    ('01900000-0000-7000-8000-000000000004', 'user',     'User',          true);

CREATE TABLE user_roles (
    user_id         uuid NOT NULL,
    role_id         uuid NOT NULL,
    assigned_by     uuid,
    assigned_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (user_id, role_id),
    CONSTRAINT uq_user_roles_one_role_per_user UNIQUE (user_id),
    CONSTRAINT fk_user_roles_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_user_roles_role FOREIGN KEY (role_id) REFERENCES roles(id) ON DELETE RESTRICT,
    CONSTRAINT fk_user_roles_assigner FOREIGN KEY (assigned_by) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX ix_user_roles_role_id ON user_roles(role_id, user_id);

CREATE TABLE refresh_sessions (
    id                  uuid PRIMARY KEY,
    family_id           uuid NOT NULL,
    user_id             uuid NOT NULL,
    parent_session_id   uuid,
    replaced_by_session_id uuid,
    token_hash          bytea NOT NULL,
    pepper_version      smallint NOT NULL,
    status              text NOT NULL DEFAULT 'active',
    issued_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at          timestamptz NOT NULL,
    rotated_at          timestamptz,
    revoked_at          timestamptz,
    revoke_reason       text,
    ip_address          inet,
    user_agent          text,
    metadata            jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_refresh_sessions_token_hash UNIQUE (token_hash),
    CONSTRAINT fk_refresh_sessions_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_refresh_sessions_parent FOREIGN KEY (parent_session_id)
        REFERENCES refresh_sessions(id) ON DELETE SET NULL,
    CONSTRAINT fk_refresh_sessions_replacement FOREIGN KEY (replaced_by_session_id)
        REFERENCES refresh_sessions(id) ON DELETE SET NULL,
    CONSTRAINT ck_refresh_sessions_status CHECK (status IN ('active', 'rotated', 'revoked', 'expired')),
    CONSTRAINT ck_refresh_sessions_hash CHECK (octet_length(token_hash) = 32),
    CONSTRAINT ck_refresh_sessions_pepper_version CHECK (pepper_version > 0),
    CONSTRAINT ck_refresh_sessions_expiry CHECK (expires_at > issued_at),
    CONSTRAINT ck_refresh_sessions_metadata CHECK (jsonb_typeof(metadata) = 'object'),
    CONSTRAINT ck_refresh_sessions_terminal_time CHECK (
        (status = 'active' AND rotated_at IS NULL
            AND revoked_at IS NULL AND revoke_reason IS NULL)
        OR (status = 'rotated' AND rotated_at IS NOT NULL
            AND revoked_at IS NULL AND revoke_reason IS NULL)
        OR (status = 'revoked' AND revoked_at IS NOT NULL
            AND revoke_reason IS NOT NULL AND btrim(revoke_reason) <> '')
        OR (status = 'expired' AND revoked_at IS NULL AND revoke_reason IS NULL)
    )
);

CREATE INDEX ix_refresh_sessions_user_active
    ON refresh_sessions(user_id, expires_at)
    WHERE status = 'active';
CREATE INDEX ix_refresh_sessions_family ON refresh_sessions(family_id, issued_at);
-- Runtime session retention uses DELETE; index both ON DELETE SET NULL self references.
CREATE INDEX ix_refresh_sessions_parent
    ON refresh_sessions(parent_session_id)
    WHERE parent_session_id IS NOT NULL;
CREATE INDEX ix_refresh_sessions_replacement
    ON refresh_sessions(replaced_by_session_id)
    WHERE replaced_by_session_id IS NOT NULL;

CREATE TABLE one_time_tokens (
    id                  uuid PRIMARY KEY,
    user_id             uuid NOT NULL,
    purpose             text NOT NULL,
    token_hash          bytea NOT NULL,
    pepper_version      smallint NOT NULL,
    expires_at          timestamptz NOT NULL,
    used_at             timestamptz,
    revoked_at          timestamptz,
    revoke_reason       text,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_one_time_tokens_hash UNIQUE (token_hash),
    CONSTRAINT uq_one_time_tokens_id_user UNIQUE (id, user_id),
    CONSTRAINT fk_one_time_tokens_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT ck_one_time_tokens_purpose CHECK (
        purpose IN ('activation', 'password_reset', 'totp_challenge')
    ),
    CONSTRAINT ck_one_time_tokens_hash CHECK (octet_length(token_hash) = 32),
    CONSTRAINT ck_one_time_tokens_pepper_version CHECK (pepper_version > 0),
    CONSTRAINT ck_one_time_tokens_expiry CHECK (expires_at > created_at),
    CONSTRAINT ck_one_time_tokens_terminal CHECK (NOT (used_at IS NOT NULL AND revoked_at IS NOT NULL)),
    CONSTRAINT ck_one_time_tokens_revoke_reason CHECK (
        (revoked_at IS NULL AND revoke_reason IS NULL)
        OR (revoked_at IS NOT NULL AND revoke_reason IS NOT NULL
            AND btrim(revoke_reason) <> '')
    )
);

CREATE INDEX ix_one_time_tokens_user_open
    ON one_time_tokens(user_id, purpose, expires_at, id)
    WHERE used_at IS NULL AND revoked_at IS NULL;

CREATE TABLE email_outbox (
    id                  uuid PRIMARY KEY,
    idempotency_key     text NOT NULL,
    message_id          text NOT NULL,
    user_id             uuid NOT NULL,
    one_time_token_id   uuid NOT NULL,
    recipient_envelope  jsonb,
    template_code       text NOT NULL,
    template_payload    jsonb NOT NULL DEFAULT '{}'::jsonb,
    delivery_secret_envelope jsonb,
    status              text NOT NULL DEFAULT 'pending',
    attempts            integer NOT NULL DEFAULT 0,
    next_attempt_at     timestamptz DEFAULT clock_timestamp(),
    lock_owner          uuid,
    lock_generation     bigint NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    sent_at             timestamptz,
    dead_at             timestamptz,
    last_error          text,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_email_outbox_idempotency UNIQUE (idempotency_key),
    CONSTRAINT uq_email_outbox_message_id UNIQUE (message_id),
    CONSTRAINT uq_email_outbox_one_time_token UNIQUE (one_time_token_id),
    CONSTRAINT fk_email_outbox_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT,
    CONSTRAINT fk_email_outbox_one_time_token FOREIGN KEY (one_time_token_id, user_id)
        REFERENCES one_time_tokens(id, user_id) ON DELETE RESTRICT,
    CONSTRAINT ck_email_outbox_idempotency CHECK (btrim(idempotency_key) <> ''),
    CONSTRAINT ck_email_outbox_message_id CHECK (
        length(message_id) BETWEEN 3 AND 320
        AND btrim(message_id) = message_id
        AND strpos(message_id, chr(13)) = 0
        AND strpos(message_id, chr(10)) = 0
    ),
    CONSTRAINT ck_email_outbox_recipient CHECK (
        recipient_envelope IS NULL OR jsonb_typeof(recipient_envelope) = 'object'
    ),
    CONSTRAINT ck_email_outbox_template CHECK (btrim(template_code) <> ''),
    CONSTRAINT ck_email_outbox_payload CHECK (
        jsonb_typeof(template_payload) = 'object'
        AND NOT (template_payload ?| ARRAY['token', 'password', 'secret', 'api_key', 'reset_url'])
    ),
    CONSTRAINT ck_email_outbox_secret_envelope CHECK (
        delivery_secret_envelope IS NULL
        OR jsonb_typeof(delivery_secret_envelope) = 'object'
    ),
    CONSTRAINT ck_email_outbox_status CHECK (status IN ('pending', 'processing', 'sent', 'dead')),
    CONSTRAINT ck_email_outbox_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_email_outbox_generation CHECK (lock_generation >= 0),
    CONSTRAINT ck_email_outbox_state CHECK (
        (status = 'pending'
            AND next_attempt_at IS NOT NULL
            AND lock_owner IS NULL AND locked_until IS NULL
            AND sent_at IS NULL AND dead_at IS NULL
            AND recipient_envelope IS NOT NULL AND delivery_secret_envelope IS NOT NULL)
        OR (status = 'processing'
            AND next_attempt_at IS NOT NULL
            AND lock_owner IS NOT NULL AND locked_until IS NOT NULL
            AND lock_generation > 0
            AND sent_at IS NULL AND dead_at IS NULL
            AND recipient_envelope IS NOT NULL AND delivery_secret_envelope IS NOT NULL)
        OR (status = 'sent'
            AND next_attempt_at IS NULL
            AND lock_owner IS NULL AND locked_until IS NULL
            AND lock_generation > 0
            AND sent_at IS NOT NULL AND dead_at IS NULL
            AND recipient_envelope IS NULL AND delivery_secret_envelope IS NULL)
        OR (status = 'dead'
            AND next_attempt_at IS NULL
            AND lock_owner IS NULL AND locked_until IS NULL
            AND lock_generation > 0
            AND sent_at IS NULL AND dead_at IS NOT NULL
            AND last_error IS NOT NULL AND btrim(last_error) <> ''
            AND recipient_envelope IS NULL AND delivery_secret_envelope IS NULL)
    )
);

COMMENT ON COLUMN email_outbox.template_payload IS
    'Non-secret rendering data only. The reset credential/full URL is envelope-encrypted in delivery_secret_envelope.';

CREATE INDEX ix_email_outbox_ready
    ON email_outbox(next_attempt_at, created_at, id)
    WHERE status = 'pending';
CREATE INDEX ix_email_outbox_processing_expiry
    ON email_outbox(locked_until, id)
    WHERE status = 'processing';

CREATE TABLE accounts (
    id                  uuid PRIMARY KEY,
    provider            text NOT NULL,
    name                text NOT NULL,
    auth_type           text NOT NULL,
    upstream_base_url   text NOT NULL,
    credential_envelope jsonb NOT NULL,
    credential_prefix   text NOT NULL,
    credential_hint     text,
    settings            jsonb NOT NULL DEFAULT '{}'::jsonb,
    status              text NOT NULL DEFAULT 'disabled',
    priority            integer NOT NULL DEFAULT 0,
    weight              integer NOT NULL DEFAULT 100,
    max_concurrency     integer NOT NULL DEFAULT 1,
    upstream_rate_limited_until timestamptz,
    last_health_at      timestamptz,
    last_health_status  text NOT NULL DEFAULT 'unknown',
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    deleted_at          timestamptz,
    CONSTRAINT ck_accounts_provider CHECK (provider IN ('openai', 'openai_compatible')),
    CONSTRAINT ck_accounts_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_accounts_auth_type CHECK (auth_type = 'api_key'),
    CONSTRAINT ck_accounts_base_url CHECK (
        upstream_base_url ~ '^https://[^[:space:]]+$'
        OR upstream_base_url ~ '^http://(localhost|127\.0\.0\.1|\[::1\])([:/][^[:space:]]*)?$'
    ),
    CONSTRAINT ck_accounts_credential_envelope CHECK (jsonb_typeof(credential_envelope) = 'object'),
    CONSTRAINT ck_accounts_credential_prefix CHECK (
        btrim(credential_prefix) <> '' AND length(credential_prefix) <= 32
    ),
    CONSTRAINT ck_accounts_credential_hint CHECK (
        credential_hint IS NULL OR (btrim(credential_hint) <> '' AND length(credential_hint) <= 128)
    ),
    CONSTRAINT ck_accounts_settings CHECK (jsonb_typeof(settings) = 'object'),
    CONSTRAINT ck_accounts_status CHECK (status IN ('active', 'disabled', 'retired')),
    CONSTRAINT ck_accounts_health_status CHECK (
        last_health_status IN ('unknown', 'healthy', 'degraded', 'cooling', 'unhealthy')
    ),
    CONSTRAINT ck_accounts_priority CHECK (priority BETWEEN -100000 AND 100000),
    CONSTRAINT ck_accounts_weight CHECK (weight BETWEEN 1 AND 100000),
    CONSTRAINT ck_accounts_max_concurrency CHECK (max_concurrency > 0),
    CONSTRAINT ck_accounts_version CHECK (version > 0),
    CONSTRAINT ck_accounts_deleted_status CHECK (deleted_at IS NULL OR status = 'retired')
);

CREATE INDEX ix_accounts_scheduler
    ON accounts(provider, priority DESC, id)
    WHERE status = 'active' AND deleted_at IS NULL;

CREATE TABLE channels (
    id                  uuid PRIMARY KEY,
    provider            text NOT NULL,
    name                text NOT NULL,
    model_rules         jsonb NOT NULL DEFAULT '{}'::jsonb,
    capabilities        jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings            jsonb NOT NULL DEFAULT '{}'::jsonb,
    status              text NOT NULL DEFAULT 'disabled',
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    deleted_at          timestamptz,
    CONSTRAINT ck_channels_provider CHECK (provider IN ('openai', 'openai_compatible')),
    CONSTRAINT ck_channels_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_channels_model_rules CHECK (jsonb_typeof(model_rules) = 'object'),
    CONSTRAINT ck_channels_capabilities CHECK (jsonb_typeof(capabilities) = 'object'),
    CONSTRAINT ck_channels_settings CHECK (jsonb_typeof(settings) = 'object'),
    CONSTRAINT ck_channels_status CHECK (status IN ('active', 'disabled', 'retired')),
    CONSTRAINT ck_channels_version CHECK (version > 0),
    CONSTRAINT ck_channels_deleted_status CHECK (deleted_at IS NULL OR status = 'retired')
);

CREATE INDEX ix_channels_provider_active
    ON channels(provider, id)
    WHERE status = 'active' AND deleted_at IS NULL;

CREATE TABLE groups (
    id                  uuid PRIMARY KEY,
    name                text NOT NULL,
    description         text,
    status              text NOT NULL DEFAULT 'disabled',
    model_policy        jsonb NOT NULL DEFAULT '{}'::jsonb,
    runtime_policy      jsonb NOT NULL DEFAULT '{}'::jsonb,
    activation_supply_readiness_token text,
    activation_supply_observed_at timestamptz,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    deleted_at          timestamptz,
    CONSTRAINT uq_groups_name UNIQUE (name),
    CONSTRAINT ck_groups_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_groups_status CHECK (status IN ('active', 'disabled', 'archived')),
    CONSTRAINT ck_groups_model_policy CHECK (jsonb_typeof(model_policy) = 'object'),
    CONSTRAINT ck_groups_runtime_policy CHECK (jsonb_typeof(runtime_policy) = 'object'),
    CONSTRAINT ck_groups_activation_supply_evidence_pair CHECK (
        (activation_supply_readiness_token IS NULL)
        = (activation_supply_observed_at IS NULL)
    ),
    CONSTRAINT ck_groups_activation_supply_token CHECK (
        activation_supply_readiness_token IS NULL
        OR (
            activation_supply_readiness_token = btrim(activation_supply_readiness_token)
            AND length(activation_supply_readiness_token) BETWEEN 4 AND 512
            AND activation_supply_readiness_token ~ '^[a-z][a-z0-9]*\.[A-Za-z0-9_-]+$'
        )
    ),
    CONSTRAINT ck_groups_active_supply_evidence CHECK (
        status <> 'active'
        OR (
            activation_supply_readiness_token IS NOT NULL
            AND activation_supply_observed_at IS NOT NULL
        )
    ),
    CONSTRAINT ck_groups_version CHECK (version > 0),
    CONSTRAINT ck_groups_deleted_status CHECK (deleted_at IS NULL OR status = 'archived')
);

CREATE TABLE group_supply_configurations (
    group_id            uuid PRIMARY KEY,
    channel_id          uuid,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT fk_group_supply_configurations_group
        FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_supply_configurations_channel
        FOREIGN KEY (channel_id) REFERENCES channels(id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_supply_configurations_version CHECK (version > 0)
);

CREATE INDEX ix_group_supply_configurations_channel
    ON group_supply_configurations(channel_id, group_id)
    WHERE channel_id IS NOT NULL;

CREATE TABLE group_accounts (
    group_id            uuid NOT NULL,
    account_id          uuid NOT NULL,
    priority_override   integer,
    weight_override     integer,
    is_enabled          boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (group_id, account_id),
    CONSTRAINT fk_group_accounts_configuration FOREIGN KEY (group_id)
        REFERENCES group_supply_configurations(group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_accounts_account FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_accounts_priority CHECK (
        priority_override IS NULL OR priority_override BETWEEN -100000 AND 100000
    ),
    CONSTRAINT ck_group_accounts_weight CHECK (
        weight_override IS NULL OR weight_override BETWEEN 1 AND 100000
    )
);

CREATE INDEX ix_group_accounts_account ON group_accounts(account_id, group_id) WHERE is_enabled;

CREATE TABLE subscription_templates (
    id                  uuid PRIMARY KEY,
    group_id            uuid NOT NULL,
    name                text NOT NULL,
    description         text,
    default_duration_days integer NOT NULL,
    status              text NOT NULL DEFAULT 'active',
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    deleted_at          timestamptz,
    CONSTRAINT uq_subscription_templates_group_name UNIQUE (group_id, name),
    CONSTRAINT uq_subscription_templates_id_group UNIQUE (id, group_id),
    CONSTRAINT fk_subscription_templates_group FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT ck_subscription_templates_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_subscription_templates_duration CHECK (
        default_duration_days BETWEEN 1 AND 3650
    ),
    CONSTRAINT ck_subscription_templates_status CHECK (status IN ('active', 'disabled', 'retired')),
    CONSTRAINT ck_subscription_templates_version CHECK (version > 0),
    CONSTRAINT ck_subscription_templates_deleted_status CHECK (deleted_at IS NULL OR status = 'retired')
);

COMMENT ON TABLE subscription_templates IS
    'Access-only templates; no commercial or per-user resource semantics.';

CREATE TABLE subscriptions (
    id                  uuid PRIMARY KEY,
    user_id             uuid NOT NULL,
    group_id            uuid NOT NULL,
    template_id         uuid NOT NULL,
    template_name_snapshot text NOT NULL,
    status              text NOT NULL DEFAULT 'active',
    starts_at           timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    source              text NOT NULL DEFAULT 'admin',
    assigned_by         uuid NOT NULL,
    change_reason       text NOT NULL,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_subscriptions_user_group UNIQUE (user_id, group_id),
    CONSTRAINT uq_subscriptions_id_user_group UNIQUE (id, user_id, group_id),
    CONSTRAINT fk_subscriptions_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT,
    CONSTRAINT fk_subscriptions_group FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT fk_subscriptions_template FOREIGN KEY (template_id, group_id)
        REFERENCES subscription_templates(id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_subscriptions_assigner FOREIGN KEY (assigned_by) REFERENCES users(id) ON DELETE RESTRICT,
    CONSTRAINT ck_subscriptions_status CHECK (status IN ('active', 'suspended', 'revoked')),
    CONSTRAINT ck_subscriptions_time CHECK (expires_at > starts_at),
    CONSTRAINT ck_subscriptions_source CHECK (source = 'admin'),
    CONSTRAINT ck_subscriptions_template_snapshot CHECK (btrim(template_name_snapshot) <> ''),
    CONSTRAINT ck_subscriptions_reason CHECK (btrim(change_reason) <> ''),
    CONSTRAINT ck_subscriptions_version CHECK (version > 0)
);

COMMENT ON CONSTRAINT uq_subscriptions_user_group ON subscriptions IS
    'One canonical mutable access grant per user and Group; renew/revoke/restore update this row and append audit.';

CREATE INDEX ix_subscriptions_access
    ON subscriptions(user_id, group_id, expires_at)
    WHERE status = 'active';
CREATE INDEX ix_subscriptions_expiry
    ON subscriptions(expires_at, id)
    WHERE status = 'active';

CREATE TABLE api_keys (
    id                  uuid PRIMARY KEY,
    user_id             uuid NOT NULL,
    group_id            uuid NOT NULL,
    name                text NOT NULL,
    key_prefix          text NOT NULL,
    secret_hash         bytea NOT NULL,
    pepper_version      smallint NOT NULL,
    status              text NOT NULL DEFAULT 'active',
    expires_at          timestamptz,
    ip_acl              jsonb NOT NULL DEFAULT '[]'::jsonb,
    last_used_at        timestamptz,
    revoked_at          timestamptz,
    revoke_reason       text,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_api_keys_secret_hash UNIQUE (secret_hash),
    CONSTRAINT uq_api_keys_id_user_group UNIQUE (id, user_id, group_id),
    CONSTRAINT fk_api_keys_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT,
    CONSTRAINT fk_api_keys_group FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT ck_api_keys_name CHECK (btrim(name) <> ''),
    CONSTRAINT ck_api_keys_prefix CHECK (key_prefix ~ '^sk-[A-Za-z0-9_-]{4,24}$'),
    CONSTRAINT ck_api_keys_hash CHECK (octet_length(secret_hash) = 32),
    CONSTRAINT ck_api_keys_pepper_version CHECK (pepper_version > 0),
    CONSTRAINT ck_api_keys_status CHECK (status IN ('active', 'disabled', 'revoked')),
    CONSTRAINT ck_api_keys_ip_acl CHECK (jsonb_typeof(ip_acl) = 'array'),
    CONSTRAINT ck_api_keys_version CHECK (version > 0),
    CONSTRAINT ck_api_keys_revocation CHECK (
        (status IN ('active', 'disabled') AND revoked_at IS NULL AND revoke_reason IS NULL)
        OR (status = 'revoked' AND revoked_at IS NOT NULL AND revoke_reason IS NOT NULL
            AND btrim(revoke_reason) <> '')
    )
);

COMMENT ON COLUMN api_keys.group_id IS
    'Required and immutable. To change Group, revoke this key and create another key.';

CREATE INDEX ix_api_keys_user_active
    ON api_keys(user_id, group_id, id)
    WHERE status = 'active';
CREATE INDEX ix_api_keys_prefix ON api_keys(key_prefix);

CREATE TABLE group_token_quotas (
    group_id            uuid PRIMARY KEY,
    current_period_id   uuid NOT NULL,
    enabled             boolean NOT NULL DEFAULT true,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT fk_group_token_quotas_group FOREIGN KEY (group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_token_quotas_version CHECK (version > 0)
);

CREATE TABLE group_quota_periods (
    id                  uuid PRIMARY KEY,
    group_id            uuid NOT NULL,
    period_number       bigint NOT NULL,
    total_tokens        numeric(78,0) NOT NULL,
    consumed_tokens     numeric(78,0) NOT NULL DEFAULT 0,
    reserved_tokens     numeric(78,0) NOT NULL DEFAULT 0,
    status              text NOT NULL DEFAULT 'current',
    opened_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    closed_at           timestamptz,
    reset_reason        text,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_group_quota_periods_group_number UNIQUE (group_id, period_number),
    CONSTRAINT uq_group_quota_periods_id_group UNIQUE (id, group_id),
    CONSTRAINT fk_group_quota_periods_quota FOREIGN KEY (group_id)
        REFERENCES group_token_quotas(group_id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_quota_periods_period_number CHECK (period_number > 0),
    CONSTRAINT ck_group_quota_periods_total CHECK (
        total_tokens BETWEEN 1 AND 9007199254740991 AND total_tokens = trunc(total_tokens)
    ),
    CONSTRAINT ck_group_quota_periods_consumed CHECK (
        consumed_tokens >= 0 AND consumed_tokens = trunc(consumed_tokens)
    ),
    CONSTRAINT ck_group_quota_periods_reserved CHECK (
        reserved_tokens >= 0 AND reserved_tokens = trunc(reserved_tokens)
    ),
    CONSTRAINT ck_group_quota_periods_status CHECK (status IN ('current', 'closed')),
    CONSTRAINT ck_group_quota_periods_close CHECK (
        (status = 'current' AND closed_at IS NULL)
        OR (status = 'closed' AND closed_at IS NOT NULL AND closed_at >= opened_at)
    ),
    CONSTRAINT ck_group_quota_periods_version CHECK (version > 0)
);

CREATE UNIQUE INDEX uq_group_quota_periods_one_current
    ON group_quota_periods(group_id)
    WHERE status = 'current';

ALTER TABLE group_token_quotas
    ADD CONSTRAINT fk_group_token_quotas_current_period
    FOREIGN KEY (current_period_id, group_id)
    REFERENCES group_quota_periods(id, group_id)
    ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;

COMMENT ON TABLE group_token_quotas IS
    'One stable quota configuration per Group. Provision quota and its first period in one transaction.';
COMMENT ON TABLE group_quota_periods IS
    'Authoritative exact Group counters use numeric(78,0)/BigInteger. Administrative total input is capped at 2^53-1; public cumulative values are decimal strings.';

CREATE TABLE usage_requests (
    request_id          uuid PRIMARY KEY,
    user_id             uuid NOT NULL,
    api_key_id          uuid NOT NULL,
    subscription_id     uuid NOT NULL,
    quota_group_id      uuid NOT NULL,
    routing_group_id    uuid NOT NULL,
    endpoint            text NOT NULL,
    client_request_id   text,
    requested_model     text NOT NULL,
    effective_model     text,
    is_streaming        boolean NOT NULL,
    status              text NOT NULL DEFAULT 'accepted',
    attempt_count       integer NOT NULL DEFAULT 0,
    final_attempt_id    uuid,
    error_code          text,
    received_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at        timestamptz,
    metadata            jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_usage_requests_id_group UNIQUE (request_id, quota_group_id),
    CONSTRAINT fk_usage_requests_api_key_identity
        FOREIGN KEY (api_key_id, user_id, quota_group_id)
        REFERENCES api_keys(id, user_id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_requests_subscription_identity
        FOREIGN KEY (subscription_id, user_id, quota_group_id)
        REFERENCES subscriptions(id, user_id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_requests_routing_group FOREIGN KEY (routing_group_id) REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT ck_usage_requests_same_group CHECK (routing_group_id = quota_group_id),
    CONSTRAINT ck_usage_requests_endpoint CHECK (endpoint LIKE '/v1/%'),
    CONSTRAINT ck_usage_requests_model CHECK (btrim(requested_model) <> ''),
    CONSTRAINT ck_usage_requests_status CHECK (
        status IN ('accepted', 'in_progress', 'succeeded', 'failed', 'cancelled')
    ),
    CONSTRAINT ck_usage_requests_attempt_count CHECK (attempt_count >= 0),
    CONSTRAINT ck_usage_requests_completion CHECK (
        (status IN ('accepted', 'in_progress') AND completed_at IS NULL)
        OR (status IN ('succeeded', 'failed', 'cancelled') AND completed_at IS NOT NULL)
    ),
    CONSTRAINT ck_usage_requests_metadata CHECK (jsonb_typeof(metadata) = 'object')
);

COMMENT ON CONSTRAINT ck_usage_requests_same_group ON usage_requests IS
    'Release 1 does not permit cross-Group fallback.';

CREATE INDEX ix_usage_requests_group_received
    ON usage_requests(quota_group_id, received_at DESC, request_id);
CREATE INDEX ix_usage_requests_user_received
    ON usage_requests(user_id, received_at DESC, request_id);
CREATE INDEX ix_usage_requests_subscription_received
    ON usage_requests(subscription_id, received_at DESC, request_id);
CREATE INDEX ix_usage_requests_key_received
    ON usage_requests(api_key_id, received_at DESC, request_id);
CREATE INDEX ix_usage_requests_in_progress
    ON usage_requests(received_at, request_id)
    WHERE status IN ('accepted', 'in_progress');

CREATE TABLE group_token_reservations (
    id                  uuid PRIMARY KEY,
    period_id           uuid NOT NULL,
    group_id            uuid NOT NULL,
    request_id          uuid NOT NULL,
    attempt_id          uuid NOT NULL,
    attempt_index       integer NOT NULL,
    account_id          uuid NOT NULL,
    channel_id          uuid NOT NULL,
    estimated_tokens    numeric(78,0) NOT NULL,
    actual_tokens       numeric(78,0),
    status              text NOT NULL DEFAULT 'pending',
    is_streaming        boolean NOT NULL,
    lease_owner         text NOT NULL,
    lease_expires_at    timestamptz NOT NULL,
    max_expires_at      timestamptz NOT NULL,
    dispatch_started_at timestamptz,
    dispatch_provider   text,
    dispatch_model      text,
    estimated_input_tokens numeric(78,0),
    estimated_output_tokens numeric(78,0),
    usage_source        text,
    settled_at          timestamptz,
    released_at         timestamptz,
    expired_at          timestamptz,
    adjusted_at         timestamptz,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_group_token_reservations_period_attempt UNIQUE (period_id, attempt_id),
    CONSTRAINT uq_group_token_reservations_attempt UNIQUE (attempt_id),
    CONSTRAINT uq_group_token_reservations_request_attempt_index UNIQUE (request_id, attempt_index),
    CONSTRAINT uq_group_token_reservations_fact_identity
        UNIQUE (id, attempt_id, request_id, attempt_index, group_id),
    CONSTRAINT uq_group_token_reservations_route_identity UNIQUE (id, account_id, channel_id),
    CONSTRAINT uq_group_token_reservations_event_identity
        UNIQUE (id, period_id, group_id, attempt_id),
    CONSTRAINT fk_group_token_reservations_period FOREIGN KEY (period_id, group_id)
        REFERENCES group_quota_periods(id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_token_reservations_request FOREIGN KEY (request_id, group_id)
        REFERENCES usage_requests(request_id, quota_group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_token_reservations_group_account FOREIGN KEY (group_id, account_id)
        REFERENCES group_accounts(group_id, account_id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_token_reservations_channel FOREIGN KEY (channel_id)
        REFERENCES channels(id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_token_reservations_attempt_index CHECK (attempt_index >= 0),
    CONSTRAINT ck_group_token_reservations_estimate CHECK (
        estimated_tokens BETWEEN 1 AND 9007199254740991
        AND estimated_tokens = trunc(estimated_tokens)
    ),
    CONSTRAINT ck_group_token_reservations_actual CHECK (
        actual_tokens IS NULL OR (actual_tokens >= 0 AND actual_tokens = trunc(actual_tokens))
    ),
    CONSTRAINT ck_group_token_reservations_status CHECK (
        status IN ('pending', 'settled', 'released', 'expired')
    ),
    CONSTRAINT ck_group_token_reservations_owner CHECK (btrim(lease_owner) <> ''),
    CONSTRAINT ck_group_token_reservations_lease CHECK (
        lease_expires_at > created_at AND max_expires_at >= lease_expires_at
    ),
    CONSTRAINT ck_group_token_reservations_dispatch CHECK (
        (dispatch_started_at IS NULL
            AND dispatch_provider IS NULL AND dispatch_model IS NULL
            AND estimated_input_tokens IS NULL AND estimated_output_tokens IS NULL)
        OR (dispatch_started_at IS NOT NULL
            AND dispatch_started_at >= created_at
            AND dispatch_provider IN ('openai', 'openai_compatible')
            AND dispatch_model IS NOT NULL AND btrim(dispatch_model) <> ''
            AND estimated_input_tokens IS NOT NULL AND estimated_input_tokens >= 0
            AND estimated_input_tokens = trunc(estimated_input_tokens)
            AND estimated_output_tokens IS NOT NULL AND estimated_output_tokens >= 0
            AND estimated_output_tokens = trunc(estimated_output_tokens)
            AND estimated_input_tokens + estimated_output_tokens = estimated_tokens)
    ),
    CONSTRAINT ck_group_token_reservations_source CHECK (
        usage_source IS NULL OR usage_source IN (
            'upstream', 'local_tokenizer', 'conservative_estimate',
            'confirmed_no_execution'
        )
    ),
    CONSTRAINT ck_group_token_reservations_confirmed_zero CHECK (
        usage_source <> 'confirmed_no_execution' OR actual_tokens = 0
    ),
    CONSTRAINT ck_group_token_reservations_terminal CHECK (
        (status = 'pending' AND actual_tokens IS NULL AND usage_source IS NULL
            AND settled_at IS NULL AND released_at IS NULL AND expired_at IS NULL)
        OR (status = 'settled' AND dispatch_started_at IS NOT NULL
            AND actual_tokens IS NOT NULL AND settled_at IS NOT NULL
            AND usage_source IS NOT NULL AND released_at IS NULL AND expired_at IS NULL)
        OR (status = 'released' AND dispatch_started_at IS NULL
            AND actual_tokens IS NULL AND usage_source IS NULL AND released_at IS NOT NULL
            AND settled_at IS NULL AND expired_at IS NULL)
        OR (status = 'expired' AND expired_at IS NOT NULL
            AND settled_at IS NULL AND released_at IS NULL
            AND (
                (dispatch_started_at IS NULL AND actual_tokens IS NULL AND usage_source IS NULL)
                OR (dispatch_started_at IS NOT NULL
                    AND actual_tokens = estimated_tokens
                    AND usage_source = 'conservative_estimate')
            ))
    )
);

CREATE INDEX ix_group_token_reservations_expiry
    ON group_token_reservations(lease_expires_at, id)
    WHERE status = 'pending';
CREATE INDEX ix_group_token_reservations_group_created
    ON group_token_reservations(group_id, created_at DESC, id);
CREATE INDEX ix_group_token_reservations_request
    ON group_token_reservations(request_id, attempt_index);

CREATE TABLE group_quota_events (
    id                  uuid PRIMARY KEY,
    event_sequence      bigint GENERATED ALWAYS AS IDENTITY,
    group_id            uuid NOT NULL,
    period_id           uuid NOT NULL,
    reservation_id      uuid,
    attempt_id          uuid,
    event_type          text NOT NULL,
    delta_total_tokens  numeric(78,0) NOT NULL DEFAULT 0,
    delta_consumed_tokens numeric(78,0) NOT NULL DEFAULT 0,
    delta_reserved_tokens numeric(78,0) NOT NULL DEFAULT 0,
    total_tokens_after  numeric(78,0) NOT NULL,
    consumed_tokens_after numeric(78,0) NOT NULL,
    reserved_tokens_after numeric(78,0) NOT NULL,
    actor_type          text NOT NULL,
    actor_user_id       uuid,
    idempotency_key     text NOT NULL,
    reason              text,
    metadata            jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_group_quota_events_sequence UNIQUE (event_sequence),
    CONSTRAINT uq_group_quota_events_idempotency UNIQUE (idempotency_key),
    CONSTRAINT fk_group_quota_events_period FOREIGN KEY (period_id, group_id)
        REFERENCES group_quota_periods(id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_group_quota_events_reservation_identity
        FOREIGN KEY (reservation_id, period_id, group_id, attempt_id)
        REFERENCES group_token_reservations(id, period_id, group_id, attempt_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_group_quota_events_actor FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE SET NULL,
    CONSTRAINT ck_group_quota_events_type CHECK (
        event_type IN (
            'initialized', 'reserved', 'dispatch_started', 'renewed',
            'settled', 'released', 'expired',
            'usage_adjusted', 'total_adjusted', 'period_reset'
        )
    ),
    CONSTRAINT ck_group_quota_events_delta_total CHECK (delta_total_tokens = trunc(delta_total_tokens)),
    CONSTRAINT ck_group_quota_events_delta_consumed CHECK (
        delta_consumed_tokens = trunc(delta_consumed_tokens)
    ),
    CONSTRAINT ck_group_quota_events_delta_reserved CHECK (
        delta_reserved_tokens = trunc(delta_reserved_tokens)
    ),
    CONSTRAINT ck_group_quota_events_total_after CHECK (
        total_tokens_after BETWEEN 1 AND 9007199254740991
        AND total_tokens_after = trunc(total_tokens_after)
    ),
    CONSTRAINT ck_group_quota_events_consumed_after CHECK (
        consumed_tokens_after >= 0 AND consumed_tokens_after = trunc(consumed_tokens_after)
    ),
    CONSTRAINT ck_group_quota_events_reserved_after CHECK (
        reserved_tokens_after >= 0 AND reserved_tokens_after = trunc(reserved_tokens_after)
    ),
    CONSTRAINT ck_group_quota_events_actor CHECK (actor_type IN ('gateway', 'worker', 'admin', 'system')),
    CONSTRAINT ck_group_quota_events_actor_user CHECK (
        (actor_type = 'admin' AND actor_user_id IS NOT NULL) OR actor_type <> 'admin'
    ),
    CONSTRAINT ck_group_quota_events_reservation_identity CHECK (
        (reservation_id IS NULL AND attempt_id IS NULL)
        OR (reservation_id IS NOT NULL AND attempt_id IS NOT NULL)
    ),
    CONSTRAINT ck_group_quota_events_idempotency CHECK (btrim(idempotency_key) <> ''),
    CONSTRAINT ck_group_quota_events_metadata CHECK (jsonb_typeof(metadata) = 'object')
);

CREATE INDEX ix_group_quota_events_group_sequence
    ON group_quota_events(group_id, event_sequence DESC);
CREATE INDEX ix_group_quota_events_period_sequence
    ON group_quota_events(period_id, event_sequence);
CREATE INDEX ix_group_quota_events_attempt
    ON group_quota_events(attempt_id)
    WHERE attempt_id IS NOT NULL;

CREATE TABLE usage_attempts (
    attempt_id          uuid PRIMARY KEY,
    request_id          uuid NOT NULL,
    attempt_index       integer NOT NULL,
    reservation_id      uuid NOT NULL,
    quota_group_id      uuid NOT NULL,
    routing_group_id    uuid NOT NULL,
    account_id          uuid NOT NULL,
    channel_id          uuid NOT NULL,
    provider            text NOT NULL,
    model               text NOT NULL,
    status              text NOT NULL,
    upstream_http_status integer,
    error_code          text,
    input_tokens        numeric(78,0) NOT NULL,
    output_tokens       numeric(78,0) NOT NULL,
    total_tokens        numeric(78,0) GENERATED ALWAYS AS (input_tokens + output_tokens) STORED,
    cache_read_tokens   numeric(78,0) NOT NULL DEFAULT 0,
    cache_creation_tokens numeric(78,0) NOT NULL DEFAULT 0,
    thinking_tokens     numeric(78,0) NOT NULL DEFAULT 0,
    usage_source        text NOT NULL,
    is_estimated        boolean NOT NULL,
    upstream_request_id text,
    raw_upstream_usage  jsonb,
    dispatch_started_at timestamptz NOT NULL,
    first_token_at      timestamptz,
    completed_at        timestamptz NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_usage_attempts_request_index UNIQUE (request_id, attempt_index),
    CONSTRAINT uq_usage_attempts_attempt_request UNIQUE (attempt_id, request_id),
    CONSTRAINT uq_usage_attempts_reservation UNIQUE (reservation_id),
    CONSTRAINT fk_usage_attempts_request FOREIGN KEY (request_id)
        REFERENCES usage_requests(request_id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_reservation FOREIGN KEY (reservation_id)
        REFERENCES group_token_reservations(id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_reservation_fact_identity
        FOREIGN KEY (reservation_id, attempt_id, request_id, attempt_index, quota_group_id)
        REFERENCES group_token_reservations(id, attempt_id, request_id, attempt_index, group_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_reservation_route_identity
        FOREIGN KEY (reservation_id, account_id, channel_id)
        REFERENCES group_token_reservations(id, account_id, channel_id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_quota_group FOREIGN KEY (quota_group_id)
        REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_routing_group FOREIGN KEY (routing_group_id)
        REFERENCES groups(id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_account FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempts_channel FOREIGN KEY (channel_id) REFERENCES channels(id) ON DELETE RESTRICT,
    CONSTRAINT ck_usage_attempts_index CHECK (attempt_index >= 0),
    CONSTRAINT ck_usage_attempts_same_group CHECK (routing_group_id = quota_group_id),
    CONSTRAINT ck_usage_attempts_provider CHECK (provider IN ('openai', 'openai_compatible')),
    CONSTRAINT ck_usage_attempts_model CHECK (btrim(model) <> ''),
    CONSTRAINT ck_usage_attempts_status CHECK (status IN ('succeeded', 'failed', 'cancelled')),
    CONSTRAINT ck_usage_attempts_http_status CHECK (
        upstream_http_status IS NULL OR upstream_http_status BETWEEN 100 AND 599
    ),
    CONSTRAINT ck_usage_attempts_input CHECK (input_tokens >= 0 AND input_tokens = trunc(input_tokens)),
    CONSTRAINT ck_usage_attempts_output CHECK (output_tokens >= 0 AND output_tokens = trunc(output_tokens)),
    CONSTRAINT ck_usage_attempts_cache_read CHECK (
        cache_read_tokens BETWEEN 0 AND input_tokens AND cache_read_tokens = trunc(cache_read_tokens)
    ),
    CONSTRAINT ck_usage_attempts_cache_creation CHECK (
        cache_creation_tokens BETWEEN 0 AND input_tokens
        AND cache_creation_tokens = trunc(cache_creation_tokens)
    ),
    CONSTRAINT ck_usage_attempts_cache_partition CHECK (
        cache_read_tokens + cache_creation_tokens <= input_tokens
    ),
    CONSTRAINT ck_usage_attempts_thinking CHECK (
        thinking_tokens BETWEEN 0 AND output_tokens AND thinking_tokens = trunc(thinking_tokens)
    ),
    CONSTRAINT ck_usage_attempts_source CHECK (
        usage_source IN (
            'upstream', 'local_tokenizer', 'conservative_estimate',
            'confirmed_no_execution'
        )
    ),
    CONSTRAINT ck_usage_attempts_estimated CHECK (
        (usage_source IN ('upstream', 'confirmed_no_execution') AND is_estimated = false)
        OR (usage_source IN ('local_tokenizer', 'conservative_estimate') AND is_estimated = true)
    ),
    CONSTRAINT ck_usage_attempts_confirmed_no_execution CHECK (
        usage_source <> 'confirmed_no_execution'
        OR (
            input_tokens = 0 AND output_tokens = 0
            AND cache_read_tokens = 0 AND cache_creation_tokens = 0
            AND thinking_tokens = 0
            AND status IN ('failed', 'cancelled')
            AND error_code IS NOT NULL AND btrim(error_code) <> ''
            AND first_token_at IS NULL
            AND (upstream_http_status IS NULL OR upstream_http_status IN (401, 403, 429))
        )
    ),
    CONSTRAINT ck_usage_attempts_raw_usage CHECK (
        raw_upstream_usage IS NULL OR jsonb_typeof(raw_upstream_usage) = 'object'
    ),
    CONSTRAINT ck_usage_attempts_timestamps CHECK (
        completed_at >= dispatch_started_at
        AND (first_token_at IS NULL OR first_token_at >= dispatch_started_at)
        AND (first_token_at IS NULL OR first_token_at <= completed_at)
    )
);

COMMENT ON CONSTRAINT ck_usage_attempts_same_group ON usage_attempts IS
    'Release 1 does not permit cross-Group fallback.';
COMMENT ON TABLE usage_attempts IS
    'GroupQuota-owned immutable settlement/integration fact written atomically with quota counters. Usage owns only projections derived from this table/event stream.';

ALTER TABLE usage_requests
    ADD CONSTRAINT fk_usage_requests_final_attempt
    FOREIGN KEY (final_attempt_id, request_id)
    REFERENCES usage_attempts(attempt_id, request_id) ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;

CREATE INDEX ix_usage_attempts_account_completed
    ON usage_attempts(account_id, completed_at DESC, attempt_id);
CREATE INDEX ix_usage_attempts_group_completed
    ON usage_attempts(quota_group_id, completed_at DESC, attempt_id);
CREATE INDEX ix_usage_attempts_model_completed
    ON usage_attempts(provider, model, completed_at DESC, attempt_id);

CREATE TABLE usage_attempt_adjustments (
    attempt_id              uuid PRIMARY KEY,
    quota_event_id          uuid NOT NULL,
    previous_total_tokens   numeric(78,0) NOT NULL,
    corrected_input_tokens  numeric(78,0) NOT NULL,
    corrected_output_tokens numeric(78,0) NOT NULL,
    corrected_total_tokens  numeric(78,0) GENERATED ALWAYS AS
        (corrected_input_tokens + corrected_output_tokens) STORED,
    corrected_cache_read_tokens numeric(78,0) NOT NULL DEFAULT 0,
    corrected_cache_creation_tokens numeric(78,0) NOT NULL DEFAULT 0,
    corrected_thinking_tokens numeric(78,0) NOT NULL DEFAULT 0,
    delta_tokens            numeric(78,0) GENERATED ALWAYS AS
        ((corrected_input_tokens + corrected_output_tokens) - previous_total_tokens) STORED,
    usage_source            text NOT NULL,
    reason                  text NOT NULL,
    raw_upstream_usage      jsonb,
    adjusted_at             timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_usage_attempt_adjustments_event UNIQUE (quota_event_id),
    CONSTRAINT fk_usage_attempt_adjustments_attempt FOREIGN KEY (attempt_id)
        REFERENCES usage_attempts(attempt_id) ON DELETE RESTRICT,
    CONSTRAINT fk_usage_attempt_adjustments_event FOREIGN KEY (quota_event_id)
        REFERENCES group_quota_events(id) ON DELETE RESTRICT,
    CONSTRAINT ck_usage_attempt_adjustments_previous CHECK (
        previous_total_tokens >= 0 AND previous_total_tokens = trunc(previous_total_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_input CHECK (
        corrected_input_tokens >= 0 AND corrected_input_tokens = trunc(corrected_input_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_output CHECK (
        corrected_output_tokens >= 0 AND corrected_output_tokens = trunc(corrected_output_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_cache_read CHECK (
        corrected_cache_read_tokens BETWEEN 0 AND corrected_input_tokens
        AND corrected_cache_read_tokens = trunc(corrected_cache_read_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_cache_creation CHECK (
        corrected_cache_creation_tokens BETWEEN 0 AND corrected_input_tokens
        AND corrected_cache_creation_tokens = trunc(corrected_cache_creation_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_cache_partition CHECK (
        corrected_cache_read_tokens + corrected_cache_creation_tokens <= corrected_input_tokens
    ),
    CONSTRAINT ck_usage_attempt_adjustments_thinking CHECK (
        corrected_thinking_tokens BETWEEN 0 AND corrected_output_tokens
        AND corrected_thinking_tokens = trunc(corrected_thinking_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_delta CHECK (
        ((corrected_input_tokens + corrected_output_tokens) - previous_total_tokens)
            = trunc((corrected_input_tokens + corrected_output_tokens) - previous_total_tokens)
    ),
    CONSTRAINT ck_usage_attempt_adjustments_source CHECK (
        usage_source IN (
            'upstream', 'local_tokenizer', 'conservative_estimate',
            'confirmed_no_execution'
        )
    ),
    CONSTRAINT ck_usage_attempt_adjustments_confirmed_zero CHECK (
        usage_source <> 'confirmed_no_execution'
        OR (
            corrected_input_tokens = 0 AND corrected_output_tokens = 0
            AND corrected_cache_read_tokens = 0 AND corrected_cache_creation_tokens = 0
            AND corrected_thinking_tokens = 0
        )
    ),
    CONSTRAINT ck_usage_attempt_adjustments_reason CHECK (btrim(reason) <> ''),
    CONSTRAINT ck_usage_attempt_adjustments_raw CHECK (
        raw_upstream_usage IS NULL OR jsonb_typeof(raw_upstream_usage) = 'object'
    )
);

COMMENT ON TABLE usage_attempt_adjustments IS
    'At most one late correction per attempt. Queries use corrected values when this row exists.';

CREATE TABLE group_usage_hourly (
    group_id                uuid NOT NULL,
    period_id               uuid NOT NULL,
    bucket_start            timestamptz NOT NULL,
    request_count           bigint NOT NULL DEFAULT 0,
    attempt_count           bigint NOT NULL DEFAULT 0,
    failure_count           bigint NOT NULL DEFAULT 0,
    failover_count          bigint NOT NULL DEFAULT 0,
    estimated_attempt_count bigint NOT NULL DEFAULT 0,
    input_tokens            numeric(78,0) NOT NULL DEFAULT 0,
    output_tokens           numeric(78,0) NOT NULL DEFAULT 0,
    cache_creation_tokens   numeric(78,0) NOT NULL DEFAULT 0,
    cache_read_tokens       numeric(78,0) NOT NULL DEFAULT 0,
    thinking_tokens         numeric(78,0) NOT NULL DEFAULT 0,
    total_tokens            numeric(78,0) NOT NULL DEFAULT 0,
    rebuilt_at              timestamptz NOT NULL DEFAULT clock_timestamp(),
    version                 bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (group_id, period_id, bucket_start),
    CONSTRAINT fk_group_usage_hourly_period FOREIGN KEY (period_id, group_id)
        REFERENCES group_quota_periods(id, group_id) ON DELETE RESTRICT,
    CONSTRAINT ck_group_usage_hourly_bucket CHECK (
        bucket_start = date_trunc('hour', bucket_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
    ),
    CONSTRAINT ck_group_usage_hourly_counts CHECK (
        request_count BETWEEN 0 AND 9007199254740991
        AND attempt_count BETWEEN 0 AND 9007199254740991
        AND failure_count BETWEEN 0 AND 9007199254740991
        AND failover_count BETWEEN 0 AND 9007199254740991
        AND estimated_attempt_count BETWEEN 0 AND 9007199254740991
    ),
    CONSTRAINT ck_group_usage_hourly_tokens CHECK (
        input_tokens >= 0 AND input_tokens = trunc(input_tokens)
        AND output_tokens >= 0 AND output_tokens = trunc(output_tokens)
        AND cache_creation_tokens >= 0 AND cache_creation_tokens = trunc(cache_creation_tokens)
        AND cache_read_tokens >= 0 AND cache_read_tokens = trunc(cache_read_tokens)
        AND thinking_tokens >= 0 AND thinking_tokens = trunc(thinking_tokens)
        AND total_tokens >= 0 AND total_tokens = trunc(total_tokens)
    ),
    CONSTRAINT ck_group_usage_hourly_total CHECK (total_tokens = input_tokens + output_tokens),
    CONSTRAINT ck_group_usage_hourly_count_relations CHECK (
        failure_count <= attempt_count
        AND estimated_attempt_count <= attempt_count
        AND failover_count <= attempt_count
    ),
    CONSTRAINT ck_group_usage_hourly_version CHECK (version > 0)
);

CREATE INDEX ix_group_usage_hourly_bucket
    ON group_usage_hourly(bucket_start DESC, group_id, period_id);

CREATE TABLE account_usage_hourly (
    group_id                uuid NOT NULL,
    account_id              uuid NOT NULL,
    period_id               uuid NOT NULL,
    bucket_start            timestamptz NOT NULL,
    request_count           bigint NOT NULL DEFAULT 0,
    attempt_count           bigint NOT NULL DEFAULT 0,
    failure_count           bigint NOT NULL DEFAULT 0,
    failover_count          bigint NOT NULL DEFAULT 0,
    estimated_attempt_count bigint NOT NULL DEFAULT 0,
    input_tokens            numeric(78,0) NOT NULL DEFAULT 0,
    output_tokens           numeric(78,0) NOT NULL DEFAULT 0,
    cache_creation_tokens   numeric(78,0) NOT NULL DEFAULT 0,
    cache_read_tokens       numeric(78,0) NOT NULL DEFAULT 0,
    thinking_tokens         numeric(78,0) NOT NULL DEFAULT 0,
    total_tokens            numeric(78,0) NOT NULL DEFAULT 0,
    rebuilt_at              timestamptz NOT NULL DEFAULT clock_timestamp(),
    version                 bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (group_id, account_id, period_id, bucket_start),
    CONSTRAINT fk_account_usage_hourly_period FOREIGN KEY (period_id, group_id)
        REFERENCES group_quota_periods(id, group_id) ON DELETE RESTRICT,
    CONSTRAINT fk_account_usage_hourly_account FOREIGN KEY (account_id)
        REFERENCES accounts(id) ON DELETE RESTRICT,
    CONSTRAINT ck_account_usage_hourly_bucket CHECK (
        bucket_start = date_trunc('hour', bucket_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
    ),
    CONSTRAINT ck_account_usage_hourly_counts CHECK (
        request_count BETWEEN 0 AND 9007199254740991
        AND attempt_count BETWEEN 0 AND 9007199254740991
        AND failure_count BETWEEN 0 AND 9007199254740991
        AND failover_count BETWEEN 0 AND 9007199254740991
        AND estimated_attempt_count BETWEEN 0 AND 9007199254740991
    ),
    CONSTRAINT ck_account_usage_hourly_tokens CHECK (
        input_tokens >= 0 AND input_tokens = trunc(input_tokens)
        AND output_tokens >= 0 AND output_tokens = trunc(output_tokens)
        AND cache_creation_tokens >= 0 AND cache_creation_tokens = trunc(cache_creation_tokens)
        AND cache_read_tokens >= 0 AND cache_read_tokens = trunc(cache_read_tokens)
        AND thinking_tokens >= 0 AND thinking_tokens = trunc(thinking_tokens)
        AND total_tokens >= 0 AND total_tokens = trunc(total_tokens)
    ),
    CONSTRAINT ck_account_usage_hourly_total CHECK (total_tokens = input_tokens + output_tokens),
    CONSTRAINT ck_account_usage_hourly_count_relations CHECK (
        failure_count <= attempt_count
        AND estimated_attempt_count <= attempt_count
        AND failover_count <= attempt_count
    ),
    CONSTRAINT ck_account_usage_hourly_version CHECK (version > 0)
);

CREATE INDEX ix_account_usage_hourly_account_bucket
    ON account_usage_hourly(account_id, bucket_start DESC, group_id, period_id);

CREATE TABLE aggregation_watermarks (
    projector_name          text NOT NULL,
    partition_key          text NOT NULL,
    last_event_sequence    bigint NOT NULL DEFAULT 0,
    completed_through      timestamptz,
    lease_owner            text,
    lease_until            timestamptz,
    version                bigint NOT NULL DEFAULT 1,
    updated_at             timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (projector_name, partition_key),
    CONSTRAINT ck_aggregation_watermarks_projector CHECK (btrim(projector_name) <> ''),
    CONSTRAINT ck_aggregation_watermarks_partition CHECK (btrim(partition_key) <> ''),
    CONSTRAINT ck_aggregation_watermarks_sequence CHECK (last_event_sequence >= 0),
    CONSTRAINT ck_aggregation_watermarks_lease CHECK (
        (lease_owner IS NULL AND lease_until IS NULL)
        OR (lease_owner IS NOT NULL AND btrim(lease_owner) <> '' AND lease_until IS NOT NULL)
    ),
    CONSTRAINT ck_aggregation_watermarks_version CHECK (version > 0)
);

COMMENT ON TABLE group_usage_hourly IS
    'Derived cache for /v1/usage; authoritative quota counters still come from group_quota_periods.';
COMMENT ON TABLE account_usage_hourly IS
    'Derived operator report. Late corrections rebuild the original completion-hour row.';
COMMENT ON TABLE aggregation_watermarks IS
    'Exactly one projector lease/checkpoint per logical partition; UPSERT uses the primary key.';

CREATE TABLE idempotency_records (
    scope               text NOT NULL,
    idempotency_key     text NOT NULL,
    id                  uuid NOT NULL,
    actor_fingerprint   text NOT NULL,
    request_hash        bytea NOT NULL,
    status              text NOT NULL DEFAULT 'in_progress',
    response_status     integer,
    response_body       jsonb,
    response_body_envelope jsonb,
    response_headers    jsonb NOT NULL DEFAULT '{}'::jsonb,
    resource_type       text,
    resource_id         uuid,
    lock_owner          uuid,
    lock_generation     bigint NOT NULL DEFAULT 1,
    locked_until        timestamptz,
    expires_at          timestamptz NOT NULL,
    version             bigint NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope, idempotency_key),
    CONSTRAINT uq_idempotency_records_id UNIQUE (id),
    CONSTRAINT ck_idempotency_records_scope CHECK (btrim(scope) <> ''),
    CONSTRAINT ck_idempotency_records_key CHECK (btrim(idempotency_key) <> ''),
    CONSTRAINT ck_idempotency_records_actor CHECK (btrim(actor_fingerprint) <> ''),
    CONSTRAINT ck_idempotency_records_request_hash CHECK (octet_length(request_hash) = 32),
    CONSTRAINT ck_idempotency_records_status CHECK (status IN ('in_progress', 'completed', 'failed')),
    CONSTRAINT ck_idempotency_records_generation CHECK (lock_generation > 0),
    CONSTRAINT ck_idempotency_records_version CHECK (
        version > 0 AND version >= lock_generation
    ),
    CONSTRAINT ck_idempotency_records_response_status CHECK (
        response_status IS NULL OR response_status BETWEEN 100 AND 599
    ),
    CONSTRAINT ck_idempotency_records_response CHECK (
        (status = 'in_progress' AND response_status IS NULL
            AND response_body IS NULL AND response_body_envelope IS NULL
            AND response_headers = '{}'::jsonb
            AND lock_owner IS NOT NULL AND locked_until IS NOT NULL)
        OR (status <> 'in_progress' AND response_status IS NOT NULL
            AND lock_owner IS NULL AND locked_until IS NULL)
    ),
    CONSTRAINT ck_idempotency_records_body CHECK (
        (response_body IS NULL OR jsonb_typeof(response_body) IN ('object', 'array'))
        AND (response_body_envelope IS NULL OR jsonb_typeof(response_body_envelope) = 'object')
        AND NOT (response_body IS NOT NULL AND response_body_envelope IS NOT NULL)
    ),
    CONSTRAINT ck_idempotency_records_headers CHECK (
        jsonb_typeof(response_headers) = 'object'
        AND response_headers - ARRAY['Location', 'ETag', 'Cache-Control'] = '{}'::jsonb
    ),
    CONSTRAINT ck_idempotency_records_expiry CHECK (
        expires_at > created_at
        AND (locked_until IS NULL
            OR (locked_until > created_at AND locked_until <= expires_at))
    )
);

CREATE INDEX ix_idempotency_records_expiry ON idempotency_records(expires_at);
CREATE INDEX ix_idempotency_records_reclaim
    ON idempotency_records(locked_until, scope, idempotency_key)
    WHERE status = 'in_progress';

CREATE TABLE outbox_messages (
    id                  uuid PRIMARY KEY,
    event_sequence      bigint GENERATED ALWAYS AS IDENTITY,
    deduplication_key   text NOT NULL,
    topic               text NOT NULL,
    schema_version      integer NOT NULL DEFAULT 1,
    aggregate_type      text NOT NULL,
    aggregate_id        uuid NOT NULL,
    aggregate_version   bigint,
    event_type          text NOT NULL,
    source_event_sequence bigint,
    correlation_id      uuid NOT NULL,
    causation_id        uuid,
    payload             jsonb NOT NULL,
    occurred_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    status              text NOT NULL DEFAULT 'pending',
    next_attempt_at     timestamptz DEFAULT clock_timestamp(),
    publish_attempts    integer NOT NULL DEFAULT 0,
    locked_by           uuid,
    lock_generation     bigint NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    published_at        timestamptz,
    dead_at             timestamptz,
    replay_of           uuid,
    last_error          text,
    CONSTRAINT uq_outbox_messages_sequence UNIQUE (event_sequence),
    CONSTRAINT uq_outbox_messages_deduplication UNIQUE (deduplication_key),
    CONSTRAINT fk_outbox_messages_replay FOREIGN KEY (replay_of)
        REFERENCES outbox_messages(id) ON DELETE RESTRICT,
    CONSTRAINT ck_outbox_messages_deduplication CHECK (btrim(deduplication_key) <> ''),
    CONSTRAINT ck_outbox_messages_topic CHECK (btrim(topic) <> ''),
    CONSTRAINT ck_outbox_messages_schema_version CHECK (schema_version > 0),
    CONSTRAINT ck_outbox_messages_source_sequence CHECK (
        source_event_sequence IS NULL OR source_event_sequence > 0
    ),
    CONSTRAINT ck_outbox_messages_aggregate_type CHECK (btrim(aggregate_type) <> ''),
    CONSTRAINT ck_outbox_messages_aggregate_version CHECK (
        aggregate_version IS NULL OR aggregate_version > 0
    ),
    CONSTRAINT ck_outbox_messages_event_type CHECK (btrim(event_type) <> ''),
    CONSTRAINT ck_outbox_messages_payload CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT ck_outbox_messages_attempts CHECK (publish_attempts >= 0),
    CONSTRAINT ck_outbox_messages_generation CHECK (lock_generation >= 0),
    CONSTRAINT ck_outbox_messages_replay CHECK (replay_of IS NULL OR replay_of <> id),
    CONSTRAINT ck_outbox_messages_status CHECK (
        status IN ('pending', 'processing', 'published', 'dead')
    ),
    CONSTRAINT ck_outbox_messages_state CHECK (
        (status = 'pending'
            AND next_attempt_at IS NOT NULL
            AND locked_by IS NULL AND locked_until IS NULL
            AND published_at IS NULL AND dead_at IS NULL)
        OR (status = 'processing'
            AND next_attempt_at IS NOT NULL
            AND locked_by IS NOT NULL AND locked_until IS NOT NULL
            AND lock_generation > 0
            AND published_at IS NULL AND dead_at IS NULL)
        OR (status = 'published'
            AND next_attempt_at IS NULL
            AND locked_by IS NULL AND locked_until IS NULL
            AND lock_generation > 0
            AND published_at IS NOT NULL AND dead_at IS NULL)
        OR (status = 'dead'
            AND next_attempt_at IS NULL
            AND locked_by IS NULL AND locked_until IS NULL
            AND lock_generation > 0
            AND published_at IS NULL AND dead_at IS NOT NULL
            AND last_error IS NOT NULL AND btrim(last_error) <> '')
    )
);

CREATE INDEX ix_outbox_messages_ready
    ON outbox_messages(next_attempt_at, event_sequence)
    WHERE status = 'pending';
CREATE INDEX ix_outbox_messages_lock_expiry
    ON outbox_messages(locked_until, event_sequence)
    WHERE status = 'processing';
CREATE UNIQUE INDEX uq_outbox_messages_topic_source_sequence
    ON outbox_messages(topic, source_event_sequence)
    WHERE source_event_sequence IS NOT NULL AND replay_of IS NULL;

CREATE TABLE inbox_messages (
    consumer_name       text NOT NULL,
    message_id          uuid NOT NULL,
    topic               text NOT NULL,
    event_sequence      bigint NOT NULL,
    schema_version      integer NOT NULL,
    payload_hash        bytea NOT NULL,
    processed_at        timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (consumer_name, message_id),
    CONSTRAINT uq_inbox_messages_consumer_sequence
        UNIQUE (consumer_name, topic, event_sequence),
    CONSTRAINT ck_inbox_messages_consumer CHECK (btrim(consumer_name) <> ''),
    CONSTRAINT ck_inbox_messages_topic CHECK (btrim(topic) <> ''),
    CONSTRAINT ck_inbox_messages_sequence CHECK (event_sequence > 0),
    CONSTRAINT ck_inbox_messages_schema_version CHECK (schema_version > 0),
    CONSTRAINT ck_inbox_messages_hash CHECK (octet_length(payload_hash) = 32)
);

COMMENT ON TABLE inbox_messages IS
    'Durable Idempotent Consumer receipt. Insert it in the same transaction as projection writes and checkpoint advancement.';

CREATE TABLE audit_logs (
    id                  uuid PRIMARY KEY,
    actor_type          text NOT NULL,
    actor_user_id       uuid,
    action              text NOT NULL,
    target_type         text NOT NULL,
    target_id           uuid,
    request_id          uuid,
    reason              text,
    ip_address          inet,
    user_agent          text,
    before_state        jsonb,
    after_state         jsonb,
    metadata            jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT fk_audit_logs_actor FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE SET NULL,
    CONSTRAINT ck_audit_logs_actor_type CHECK (actor_type IN ('user', 'admin', 'operator', 'system', 'service')),
    CONSTRAINT ck_audit_logs_actor_user CHECK (
        (actor_type IN ('user', 'admin', 'operator') AND actor_user_id IS NOT NULL)
        OR actor_type IN ('system', 'service')
    ),
    CONSTRAINT ck_audit_logs_action CHECK (btrim(action) <> ''),
    CONSTRAINT ck_audit_logs_target_type CHECK (btrim(target_type) <> ''),
    CONSTRAINT ck_audit_logs_reason CHECK (reason IS NULL OR btrim(reason) <> ''),
    CONSTRAINT ck_audit_logs_before CHECK (before_state IS NULL OR jsonb_typeof(before_state) = 'object'),
    CONSTRAINT ck_audit_logs_after CHECK (after_state IS NULL OR jsonb_typeof(after_state) = 'object'),
    CONSTRAINT ck_audit_logs_metadata CHECK (jsonb_typeof(metadata) = 'object')
);

CREATE INDEX ix_audit_logs_target ON audit_logs(target_type, target_id, occurred_at DESC, id);
CREATE INDEX ix_audit_logs_actor ON audit_logs(actor_user_id, occurred_at DESC, id)
    WHERE actor_user_id IS NOT NULL;
CREATE INDEX ix_audit_logs_request ON audit_logs(request_id) WHERE request_id IS NOT NULL;

-- Enforce the worker-side fencing state machine in addition to application
-- CAS predicates. A new claim changes owner and increments attempts/generation;
-- heartbeat/retry/terminal writes preserve the current generation.
CREATE OR REPLACE FUNCTION poolai_guard_delivery_fence()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_old_owner text;
    v_new_owner text;
    v_old_attempts bigint;
    v_new_attempts bigint;
    v_success_status text;
BEGIN
    IF TG_NARGS <> 3 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'delivery_fence_trigger_arguments_invalid';
    END IF;
    v_success_status := TG_ARGV[1];
    v_old_owner := to_jsonb(OLD) ->> TG_ARGV[0];
    v_new_owner := to_jsonb(NEW) ->> TG_ARGV[0];
    v_old_attempts := (to_jsonb(OLD) ->> TG_ARGV[2])::bigint;
    v_new_attempts := (to_jsonb(NEW) ->> TG_ARGV[2])::bigint;

    IF OLD.status IN (v_success_status, 'dead') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'delivery_terminal_immutable';
    END IF;

    IF OLD.status = 'pending' AND NEW.status NOT IN ('pending', 'processing') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'delivery_transition_requires_claim';
    END IF;
    IF OLD.status = 'processing'
        AND NEW.status NOT IN ('pending', 'processing', v_success_status, 'dead') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'invalid_delivery_transition';
    END IF;

    IF NEW.status = 'processing'
        AND (OLD.status <> 'processing' OR v_new_owner IS DISTINCT FROM v_old_owner) THEN
        IF NEW.lock_generation <> OLD.lock_generation + 1 THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'delivery_claim_generation_must_increment';
        END IF;
        IF v_new_attempts <> v_old_attempts + 1 THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'delivery_claim_attempts_must_increment';
        END IF;
    ELSIF NEW.lock_generation <> OLD.lock_generation
        OR v_new_attempts <> v_old_attempts THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'delivery_fence_change_without_claim';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_email_outbox_delivery_fence
BEFORE UPDATE ON email_outbox
FOR EACH ROW EXECUTE FUNCTION poolai_guard_delivery_fence('lock_owner', 'sent', 'attempts');

CREATE TRIGGER tr_outbox_messages_delivery_fence
BEFORE UPDATE ON outbox_messages
FOR EACH ROW EXECUTE FUNCTION poolai_guard_delivery_fence('locked_by', 'published', 'publish_attempts');

CREATE OR REPLACE FUNCTION poolai_reject_api_key_group_change()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF NEW.group_id IS DISTINCT FROM OLD.group_id THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'api_key_group_immutable',
            DETAIL = 'Revoke the existing API key and create a new key for the target Group.';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_api_keys_group_immutable
BEFORE UPDATE OF group_id ON api_keys
FOR EACH ROW EXECUTE FUNCTION poolai_reject_api_key_group_change();

CREATE OR REPLACE FUNCTION poolai_reject_supply_provider_change()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF NEW.provider IS DISTINCT FROM OLD.provider THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'supply_provider_immutable',
            DETAIL = 'Retire and recreate the Supply resource to change provider.';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_accounts_provider_immutable
BEFORE UPDATE OF provider ON accounts
FOR EACH ROW EXECUTE FUNCTION poolai_reject_supply_provider_change();

CREATE TRIGGER tr_channels_provider_immutable
BEFORE UPDATE OF provider ON channels
FOR EACH ROW EXECUTE FUNCTION poolai_reject_supply_provider_change();

CREATE OR REPLACE FUNCTION poolai_guard_supply_retirement()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF OLD.status IS DISTINCT FROM 'retired' AND NEW.status = 'retired' THEN
        IF TG_TABLE_NAME = 'accounts' AND EXISTS (
            SELECT 1
            FROM group_accounts ga
            WHERE ga.account_id = NEW.id AND ga.is_enabled = true
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'account_in_use';
        ELSIF TG_TABLE_NAME = 'channels' AND EXISTS (
            SELECT 1
            FROM group_supply_configurations sc
            WHERE sc.channel_id = NEW.id
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'channel_in_use';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_accounts_guard_retirement
BEFORE UPDATE OF status ON accounts
FOR EACH ROW EXECUTE FUNCTION poolai_guard_supply_retirement();

CREATE TRIGGER tr_channels_guard_retirement
BEFORE UPDATE OF status ON channels
FOR EACH ROW EXECUTE FUNCTION poolai_guard_supply_retirement();

CREATE OR REPLACE FUNCTION poolai_guard_terminal_status()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_terminal_status text;
BEGIN
    v_terminal_status := CASE TG_TABLE_NAME
        WHEN 'groups' THEN 'archived'
        WHEN 'accounts' THEN 'retired'
        WHEN 'channels' THEN 'retired'
        WHEN 'subscription_templates' THEN 'retired'
        WHEN 'api_keys' THEN 'revoked'
        ELSE NULL
    END;

    IF v_terminal_status IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'unsupported_terminal_guard';
    END IF;
    IF OLD.status = v_terminal_status AND NEW.status <> v_terminal_status THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'terminal_status_immutable',
            DETAIL = format('%I.%s cannot transition out of %s.', TG_TABLE_NAME, OLD.id, v_terminal_status);
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_groups_terminal_status
BEFORE UPDATE OF status ON groups
FOR EACH ROW EXECUTE FUNCTION poolai_guard_terminal_status();
CREATE TRIGGER tr_accounts_terminal_status
BEFORE UPDATE OF status ON accounts
FOR EACH ROW EXECUTE FUNCTION poolai_guard_terminal_status();
CREATE TRIGGER tr_channels_terminal_status
BEFORE UPDATE OF status ON channels
FOR EACH ROW EXECUTE FUNCTION poolai_guard_terminal_status();
CREATE TRIGGER tr_subscription_templates_terminal_status
BEFORE UPDATE OF status ON subscription_templates
FOR EACH ROW EXECUTE FUNCTION poolai_guard_terminal_status();
CREATE TRIGGER tr_api_keys_terminal_status
BEFORE UPDATE OF status ON api_keys
FOR EACH ROW EXECUTE FUNCTION poolai_guard_terminal_status();

CREATE OR REPLACE FUNCTION poolai_bump_user_security_versions()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_security_changed boolean;
BEGIN
    v_security_changed := NEW.status IS DISTINCT FROM OLD.status
        OR NEW.password_hash IS DISTINCT FROM OLD.password_hash
        OR NEW.totp_secret_envelope IS DISTINCT FROM OLD.totp_secret_envelope
        OR NEW.security_stamp IS DISTINCT FROM OLD.security_stamp;

    IF v_security_changed THEN
        NEW.token_version := OLD.token_version + 1;
    ELSIF NEW.token_version IS DISTINCT FROM OLD.token_version
        AND NEW.token_version <> OLD.token_version + 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'token_version_must_increment_once';
    END IF;

    IF v_security_changed OR NEW.token_version IS DISTINCT FROM OLD.token_version THEN
        NEW.version := greatest(NEW.version, OLD.version + 1);
        NEW.updated_at := clock_timestamp();
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_users_bump_security_versions
BEFORE UPDATE OF status, password_hash, totp_secret_envelope, security_stamp, token_version ON users
FOR EACH ROW EXECUTE FUNCTION poolai_bump_user_security_versions();

CREATE OR REPLACE FUNCTION poolai_bump_role_user_version()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF TG_OP IN ('DELETE', 'UPDATE') THEN
        UPDATE users
        SET token_version = token_version + 1,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE id = OLD.user_id;
    END IF;
    IF TG_OP = 'INSERT' OR (TG_OP = 'UPDATE' AND NEW.user_id IS DISTINCT FROM OLD.user_id) THEN
        UPDATE users
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

CREATE TRIGGER tr_user_roles_bump_user_version
AFTER INSERT OR DELETE OR UPDATE OF user_id, role_id ON user_roles
FOR EACH ROW EXECUTE FUNCTION poolai_bump_role_user_version();

CREATE OR REPLACE FUNCTION poolai_snapshot_subscription_template()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_template_name text;
    v_template_status text;
BEGIN
    IF TG_OP = 'INSERT'
        OR NEW.template_id IS DISTINCT FROM OLD.template_id
        OR NEW.group_id IS DISTINCT FROM OLD.group_id THEN
        SELECT t.name, t.status
        INTO v_template_name, v_template_status
        FROM subscription_templates t
        WHERE t.id = NEW.template_id AND t.group_id = NEW.group_id
        FOR SHARE;

        IF NOT FOUND THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'subscription_template_group_mismatch';
        END IF;
        IF v_template_status <> 'active' THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'subscription_template_not_active';
        END IF;
        NEW.template_name_snapshot := v_template_name;
    ELSE
        NEW.template_name_snapshot := OLD.template_name_snapshot;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_subscriptions_snapshot_template
BEFORE INSERT OR UPDATE OF template_id, group_id, template_name_snapshot ON subscriptions
FOR EACH ROW EXECUTE FUNCTION poolai_snapshot_subscription_template();

CREATE OR REPLACE FUNCTION poolai_guard_group_supply_configuration()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_channel_provider text;
    v_channel_status text;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.group_id IS DISTINCT FROM OLD.group_id
            OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'group_supply_configuration_identity_immutable';
        END IF;
        IF NEW.version <> OLD.version + 1 THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'group_supply_configuration_version_must_increment_once';
        END IF;
        NEW.updated_at := clock_timestamp();
    ELSIF NEW.version <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'group_supply_configuration_version_must_increment_once';
    END IF;

    IF NEW.channel_id IS NOT NULL THEN
        SELECT c.provider, c.status
        INTO v_channel_provider, v_channel_status
        FROM channels c
        WHERE c.id = NEW.channel_id
        FOR SHARE;

        IF NOT FOUND OR v_channel_status = 'retired' THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'group_account_binding_invalid';
        END IF;

        PERFORM 1
        FROM group_accounts ga
        JOIN accounts a ON a.id = ga.account_id
        WHERE ga.group_id = NEW.group_id
          AND ga.is_enabled = true
        FOR SHARE OF ga, a;

        IF EXISTS (
            SELECT 1
            FROM group_accounts ga
            JOIN accounts a ON a.id = ga.account_id
            WHERE ga.group_id = NEW.group_id
              AND ga.is_enabled = true
              AND (a.status = 'retired' OR a.provider <> v_channel_provider)
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'group_account_binding_invalid';
        END IF;
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_group_supply_configurations_guard
BEFORE INSERT OR UPDATE ON group_supply_configurations
FOR EACH ROW EXECUTE FUNCTION poolai_guard_group_supply_configuration();

CREATE OR REPLACE FUNCTION poolai_validate_group_account_binding()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_channel_id uuid;
    v_channel_provider text;
    v_channel_status text;
    v_account_provider text;
    v_account_status text;
BEGIN
    IF TG_OP = 'UPDATE' AND (
        NEW.group_id IS DISTINCT FROM OLD.group_id
        OR NEW.account_id IS DISTINCT FROM OLD.account_id
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'group_account_binding_invalid';
    END IF;

    -- Existing disabled rows are immutable historical bindings and may outlive
    -- later Account retirement or a configured-Channel provider switch. A new
    -- binding, or any re-enable, must satisfy current Supply constraints.
    IF TG_OP = 'UPDATE' AND NEW.is_enabled = false THEN
        RETURN NEW;
    END IF;

    SELECT sc.channel_id
    INTO v_channel_id
    FROM group_supply_configurations sc
    WHERE sc.group_id = NEW.group_id
    FOR SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_account_binding_invalid';
    END IF;

    SELECT a.provider, a.status
    INTO v_account_provider, v_account_status
    FROM accounts a
    WHERE a.id = NEW.account_id
    FOR SHARE;

    IF NOT FOUND OR v_account_status = 'retired' THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_account_binding_invalid';
    END IF;

    IF v_channel_id IS NOT NULL THEN
        SELECT c.provider, c.status
        INTO v_channel_provider, v_channel_status
        FROM channels c
        WHERE c.id = v_channel_id
        FOR SHARE;

        IF NOT FOUND
            OR v_channel_status = 'retired'
            OR v_channel_provider <> v_account_provider THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_account_binding_invalid';
        END IF;
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_group_accounts_validate_binding
BEFORE INSERT OR UPDATE OF group_id, account_id, is_enabled, priority_override, weight_override
ON group_accounts
FOR EACH ROW EXECUTE FUNCTION poolai_validate_group_account_binding();

CREATE OR REPLACE FUNCTION poolai_bump_group_supply_configuration_version()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF TG_OP IN ('DELETE', 'UPDATE') THEN
        UPDATE group_supply_configurations
        SET version = version + 1,
            updated_at = clock_timestamp()
        WHERE group_id = OLD.group_id;
    END IF;
    IF TG_OP = 'INSERT' OR (TG_OP = 'UPDATE' AND NEW.group_id IS DISTINCT FROM OLD.group_id) THEN
        UPDATE group_supply_configurations
        SET version = version + 1,
            updated_at = clock_timestamp()
        WHERE group_id = NEW.group_id;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_group_accounts_bump_supply_configuration_version
AFTER INSERT OR UPDATE OR DELETE ON group_accounts
FOR EACH ROW EXECUTE FUNCTION poolai_bump_group_supply_configuration_version();

CREATE OR REPLACE FUNCTION poolai_validate_group_activation()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    IF TG_OP = 'UPDATE'
        AND OLD.status = 'active'
        AND NEW.status = 'active'
        AND (
            NEW.activation_supply_readiness_token
                IS DISTINCT FROM OLD.activation_supply_readiness_token
            OR NEW.activation_supply_observed_at
                IS DISTINCT FROM OLD.activation_supply_observed_at
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'group_activation_evidence_immutable';
    END IF;

    IF NEW.status = 'active' AND (
        TG_OP = 'INSERT'
        OR OLD.status IS DISTINCT FROM 'active'
    ) THEN
        IF NEW.activation_supply_readiness_token IS NULL
            OR NEW.activation_supply_observed_at IS NULL THEN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'group_activation_evidence_required';
        END IF;
        IF NOT EXISTS (
            SELECT 1
            FROM group_supply_configurations sc
            WHERE sc.group_id = NEW.id
              AND sc.channel_id IS NOT NULL
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_channel_required';
        END IF;
        IF NOT EXISTS (
            SELECT 1
            FROM group_supply_configurations sc
            JOIN channels c ON c.id = sc.channel_id
            WHERE sc.group_id = NEW.id
              AND c.status = 'active'
              AND c.deleted_at IS NULL
              AND c.model_rules <> '{}'::jsonb
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_active_channel_mapping_required';
        END IF;
        IF NOT EXISTS (
            SELECT 1
            FROM group_token_quotas q
            JOIN group_quota_periods p
              ON p.id = q.current_period_id AND p.group_id = q.group_id
            WHERE q.group_id = NEW.id
              AND q.enabled = true
              AND p.status = 'current'
              AND p.total_tokens > 0
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_current_quota_required';
        END IF;
        IF NOT EXISTS (
            SELECT 1
            FROM group_supply_configurations sc
            JOIN group_accounts ga ON ga.group_id = sc.group_id
            JOIN accounts a ON a.id = ga.account_id
            JOIN channels c ON c.id = sc.channel_id
            WHERE sc.group_id = NEW.id
              AND ga.is_enabled = true
              AND a.status = 'active'
              AND a.deleted_at IS NULL
              AND a.last_health_status IN ('healthy', 'degraded')
              AND (a.upstream_rate_limited_until IS NULL
                  OR a.upstream_rate_limited_until <= clock_timestamp())
              AND a.provider = c.provider
        ) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'group_schedulable_account_required';
        END IF;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER tr_groups_validate_activation
BEFORE INSERT OR UPDATE OF status, activation_supply_readiness_token, activation_supply_observed_at ON groups
FOR EACH ROW EXECUTE FUNCTION poolai_validate_group_activation();

CREATE OR REPLACE FUNCTION poolai_reject_fact_mutation()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public, pg_temp
AS $function$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = 'P0001',
        MESSAGE = 'append_only_fact',
        DETAIL = format('%I is append-only; append a compensating fact instead.', TG_TABLE_NAME);
END;
$function$;

CREATE TRIGGER tr_group_quota_events_append_only
BEFORE UPDATE OR DELETE ON group_quota_events
FOR EACH ROW EXECUTE FUNCTION poolai_reject_fact_mutation();

CREATE TRIGGER tr_usage_attempts_append_only
BEFORE UPDATE OR DELETE ON usage_attempts
FOR EACH ROW EXECUTE FUNCTION poolai_reject_fact_mutation();

CREATE TRIGGER tr_usage_attempt_adjustments_append_only
BEFORE UPDATE OR DELETE ON usage_attempt_adjustments
FOR EACH ROW EXECUTE FUNCTION poolai_reject_fact_mutation();

CREATE TRIGGER tr_audit_logs_append_only
BEFORE UPDATE OR DELETE ON audit_logs
FOR EACH ROW EXECUTE FUNCTION poolai_reject_fact_mutation();
