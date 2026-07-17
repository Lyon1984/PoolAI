-- PoolAI Release 1 M1-E2 session and TOTP persistence increment.
--
-- This forward migration freezes login/setup TOTP challenge state, recovery-code
-- digests, and the refresh-family lookup used to invalidate access sessions.
-- Secret material remains envelope encrypted or one-way HMAC digested; the
-- Worker and the shared NOLOGIN function owner receive no read path.

ALTER TABLE public.one_time_tokens
    ADD COLUMN challenge_kind text,
    ADD COLUMN secret_envelope jsonb,
    ADD COLUMN security_stamp uuid,
    ADD COLUMN token_version bigint,
    ADD COLUMN response_body_envelope jsonb;

ALTER TABLE public.one_time_tokens
    ADD CONSTRAINT ck_one_time_tokens_challenge_kind CHECK (
        (purpose = 'totp_challenge'
            AND challenge_kind IS NOT NULL
            AND challenge_kind IN ('login', 'setup'))
        OR (purpose <> 'totp_challenge' AND challenge_kind IS NULL)
    ),
    ADD CONSTRAINT ck_one_time_tokens_secret_envelope CHECK (
        (purpose = 'totp_challenge'
            AND challenge_kind = 'setup'
            AND secret_envelope IS NOT NULL
            AND jsonb_typeof(secret_envelope) = 'object')
        OR ((purpose <> 'totp_challenge' OR challenge_kind <> 'setup')
            AND secret_envelope IS NULL)
    ),
    ADD CONSTRAINT ck_one_time_tokens_security_snapshot CHECK (
        (purpose = 'totp_challenge'
            AND security_stamp IS NOT NULL
            AND token_version IS NOT NULL
            AND token_version > 0)
        OR (purpose <> 'totp_challenge'
            AND security_stamp IS NULL
            AND token_version IS NULL)
    ),
    ADD CONSTRAINT ck_one_time_tokens_response_envelope CHECK (
        response_body_envelope IS NULL
        OR (purpose = 'totp_challenge'
            AND challenge_kind = 'setup'
            AND jsonb_typeof(response_body_envelope) = 'object')
    );

-- A challenge creator revokes the previous open row for the same user/kind
-- before inserting its replacement. Expiry is deliberately not part of this
-- predicate because PostgreSQL partial-index predicates cannot depend on the
-- moving database clock.
CREATE UNIQUE INDEX uq_one_time_tokens_user_totp_challenge_open
    ON public.one_time_tokens(user_id, challenge_kind)
    WHERE purpose = 'totp_challenge'
      AND used_at IS NULL
      AND revoked_at IS NULL;

CREATE TABLE public.totp_recovery_codes (
    id              uuid PRIMARY KEY,
    user_id         uuid NOT NULL,
    code_hash       bytea NOT NULL,
    pepper_version  smallint NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    used_at         timestamptz,
    revoked_at      timestamptz,
    revoke_reason   text,
    CONSTRAINT uq_totp_recovery_codes_hash UNIQUE (code_hash),
    CONSTRAINT fk_totp_recovery_codes_user
        FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE,
    CONSTRAINT ck_totp_recovery_codes_hash CHECK (octet_length(code_hash) = 32),
    CONSTRAINT ck_totp_recovery_codes_pepper_version CHECK (pepper_version > 0),
    CONSTRAINT ck_totp_recovery_codes_terminal CHECK (
        NOT (used_at IS NOT NULL AND revoked_at IS NOT NULL)
    ),
    CONSTRAINT ck_totp_recovery_codes_revoke_reason CHECK (
        (revoked_at IS NULL AND revoke_reason IS NULL)
        OR (revoked_at IS NOT NULL
            AND revoke_reason IS NOT NULL
            AND btrim(revoke_reason) = revoke_reason
            AND length(revoke_reason) BETWEEN 1 AND 128)
    ),
    CONSTRAINT ck_totp_recovery_codes_terminal_clock CHECK (
        (used_at IS NULL OR used_at >= created_at)
        AND (revoked_at IS NULL OR revoked_at >= created_at)
    )
);

CREATE INDEX ix_totp_recovery_codes_user_active
    ON public.totp_recovery_codes(user_id, created_at, id)
    WHERE used_at IS NULL AND revoked_at IS NULL;

-- Access JWTs identify a refresh family, and single-use rotation permits only
-- one active generation in that family. Rotation marks the old row rotated
-- before inserting its active child in the same transaction.
CREATE UNIQUE INDEX uq_refresh_sessions_family_active
    ON public.refresh_sessions(family_id)
    WHERE status = 'active';

-- Replace 0003's broad table write with the exact password-reset/TOTP command
-- columns. Identity, token hashes, challenge snapshots, and creation time are
-- immutable after insert; only terminal state and the setup replay envelope move.
REVOKE INSERT, UPDATE ON public.one_time_tokens FROM poolai_api;
REVOKE DELETE ON public.one_time_tokens, public.refresh_sessions FROM poolai_api;

GRANT SELECT (
    challenge_kind, secret_envelope, security_stamp, token_version,
    response_body_envelope
) ON public.one_time_tokens TO poolai_api;
GRANT INSERT (
    id, user_id, purpose, token_hash, pepper_version, expires_at,
    challenge_kind, secret_envelope, security_stamp, token_version
) ON public.one_time_tokens TO poolai_api;
GRANT UPDATE (
    used_at, revoked_at, revoke_reason, response_body_envelope
) ON public.one_time_tokens TO poolai_api;

GRANT SELECT (
    id, user_id, code_hash, pepper_version, created_at,
    used_at, revoked_at, revoke_reason
) ON public.totp_recovery_codes TO poolai_api;
GRANT INSERT (id, user_id, code_hash, pepper_version)
    ON public.totp_recovery_codes TO poolai_api;
GRANT UPDATE (used_at, revoked_at, revoke_reason)
    ON public.totp_recovery_codes TO poolai_api;

REVOKE ALL ON public.totp_recovery_codes
    FROM PUBLIC, poolai_runtime_owner, poolai_worker;
REVOKE SELECT (
    secret_envelope, response_body_envelope
) ON public.one_time_tokens FROM poolai_runtime_owner, poolai_worker;

DO $permission_audit$
DECLARE
    v_forbidden record;
BEGIN
    FOR v_forbidden IN
        SELECT *
        FROM (VALUES
            ('poolai_runtime_owner', 'public.one_time_tokens', 'secret_envelope'),
            ('poolai_runtime_owner', 'public.one_time_tokens', 'response_body_envelope'),
            ('poolai_runtime_owner', 'public.totp_recovery_codes', 'code_hash'),
            ('poolai_worker', 'public.one_time_tokens', 'secret_envelope'),
            ('poolai_worker', 'public.one_time_tokens', 'response_body_envelope'),
            ('poolai_worker', 'public.totp_recovery_codes', 'code_hash')
        ) AS forbidden(role_name, table_name, column_name)
    LOOP
        IF pg_catalog.has_column_privilege(
            v_forbidden.role_name,
            v_forbidden.table_name,
            v_forbidden.column_name,
            'SELECT'
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '42501',
                MESSAGE = 'poolai_identity_m1_e2_sensitive_column_read_forbidden',
                DETAIL = pg_catalog.format(
                    '%s can read %s.%s.',
                    v_forbidden.role_name,
                    v_forbidden.table_name,
                    v_forbidden.column_name
                );
        END IF;
    END LOOP;
END;
$permission_audit$;
