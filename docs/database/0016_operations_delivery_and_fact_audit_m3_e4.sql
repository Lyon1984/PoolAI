-- PoolAI Release 1 M3-E4 delivery and attempt-fact audit authority closure.
--
-- 0003 deliberately grants the Worker a narrow Outbox INSERT column set for
-- ordinary operational events. 0015 introduced replay_of, which must remain
-- writable only through the API-only SECURITY DEFINER replay entry point.
-- M3-E4 also makes settlement/adjustment/expiry audits durable. Worker retains
-- the exact append columns used by its existing health and credential jobs, but
-- cannot read, update, delete, or set the server-owned timestamp. A NOLOGIN
-- owner verifies exact attempt-fact retries through one narrow API/Worker entry
-- point. Signed migrations remain immutable.

REVOKE INSERT (replay_of)
    ON public.outbox_messages
    FROM poolai_worker;

-- Replace 0003's table-wide Worker Audit INSERT with the exact columns used by
-- PostgresAuditAppender. Existing Worker health/credential audits keep working;
-- occurred_at remains server-owned and no Audit read/mutation authority is added.
REVOKE INSERT
    ON public.audit_logs
    FROM poolai_worker;
REVOKE INSERT (
    id, actor_type, actor_user_id, action, target_type, target_id,
    request_id, reason, ip_address, user_agent, before_state,
    after_state, metadata, occurred_at
)
    ON public.audit_logs
    FROM poolai_worker;

GRANT INSERT (
    id, actor_type, actor_user_id, action, target_type, target_id,
    request_id, reason, ip_address, user_agent, before_state,
    after_state, metadata
)
    ON public.audit_logs
    TO poolai_worker;

GRANT SELECT (
    id, actor_type, actor_user_id, action, target_type, target_id,
    request_id, reason, ip_address, user_agent, before_state,
    after_state, metadata
), INSERT (
    id, actor_type, actor_user_id, action, target_type, target_id,
    request_id, reason, ip_address, user_agent, before_state,
    after_state, metadata
)
    ON public.audit_logs
    TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;

CREATE FUNCTION public.poolai_operations_append_attempt_fact_audit_once(
    p_id uuid,
    p_actor_type text,
    p_actor_user_id uuid,
    p_action text,
    p_target_type text,
    p_target_id uuid,
    p_request_id uuid,
    p_reason text,
    p_ip_address inet,
    p_user_agent text,
    p_before_state jsonb,
    p_after_state jsonb,
    p_metadata jsonb
)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_existing record;
    v_inserted integer;
    v_unsigned_token_pattern CONSTANT text := '^(0|[1-9][0-9]{0,77})$';
    v_signed_token_pattern CONSTANT text := '^(0|-?[1-9][0-9]{0,77})$';
    v_uuid_pattern CONSTANT text := '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
BEGIN
    IF p_id IS NULL
        OR p_actor_type IS DISTINCT FROM 'service'
        OR p_actor_user_id IS NOT NULL
        OR p_action IS NULL
        OR p_action NOT IN (
            'group_quota.attempt_fact_settled',
            'group_quota.attempt_fact_usage_adjusted',
            'group_quota.attempt_fact_conservative_expired'
        )
        OR p_target_type IS DISTINCT FROM 'usage_attempt'
        OR p_target_id IS NULL
        OR p_reason IS NOT NULL
        OR p_ip_address IS NOT NULL
        OR p_user_agent IS NOT NULL
        OR p_before_state IS NOT NULL
        OR p_after_state IS NULL
        OR pg_catalog.jsonb_typeof(p_after_state) <> 'object'
        OR pg_catalog.octet_length(p_after_state::text) > 2048
        OR p_metadata IS NULL
        OR pg_catalog.jsonb_typeof(p_metadata) <> 'object'
        OR pg_catalog.octet_length(p_metadata::text) > 4096
        OR NOT (p_metadata ?& ARRAY[
            'quota_event_id',
            'group_id',
            'period_id',
            'reservation_id',
            'attempt_id',
            'outcome',
            'usage_source'
        ])
        OR pg_catalog.jsonb_typeof(p_metadata -> 'quota_event_id')
            IS DISTINCT FROM 'string'
        OR pg_catalog.jsonb_typeof(p_metadata -> 'group_id')
            IS DISTINCT FROM 'string'
        OR pg_catalog.jsonb_typeof(p_metadata -> 'period_id')
            IS DISTINCT FROM 'string'
        OR pg_catalog.jsonb_typeof(p_metadata -> 'reservation_id')
            IS DISTINCT FROM 'string'
        OR pg_catalog.jsonb_typeof(p_metadata -> 'attempt_id')
            IS DISTINCT FROM 'string'
        OR (p_metadata ->> 'quota_event_id') !~ v_uuid_pattern
        OR (p_metadata ->> 'group_id') !~ v_uuid_pattern
        OR (p_metadata ->> 'period_id') !~ v_uuid_pattern
        OR (p_metadata ->> 'reservation_id') !~ v_uuid_pattern
        OR (p_metadata ->> 'attempt_id') !~ v_uuid_pattern
        OR (p_metadata ->> 'attempt_id') IS DISTINCT FROM p_target_id::text
        OR pg_catalog.jsonb_typeof(p_metadata -> 'outcome')
            IS DISTINCT FROM 'string'
        OR (p_metadata ->> 'outcome') NOT IN (
            'succeeded', 'failed', 'cancelled')
        OR pg_catalog.jsonb_typeof(p_metadata -> 'usage_source')
            IS DISTINCT FROM 'string'
        OR (p_metadata ->> 'usage_source') NOT IN (
            'upstream',
            'local_tokenizer',
            'conservative_estimate',
            'confirmed_no_execution'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'poolai_attempt_fact_audit_invalid';
    END IF;

    IF p_action = 'group_quota.attempt_fact_usage_adjusted' THEN
        IF p_request_id IS NOT NULL
            OR p_metadata - ARRAY[
                'quota_event_id',
                'group_id',
                'period_id',
                'reservation_id',
                'attempt_id',
                'outcome',
                'usage_source',
                'token_counts'
            ] <> '{}'::jsonb
            OR pg_catalog.jsonb_typeof(p_metadata -> 'token_counts')
                IS DISTINCT FROM 'object'
            OR NOT ((p_metadata -> 'token_counts') ?& ARRAY[
                'input_tokens',
                'output_tokens',
                'cache_read_tokens',
                'cache_creation_tokens',
                'thinking_tokens',
                'total_tokens'
            ])
            OR (p_metadata -> 'token_counts') - ARRAY[
                'input_tokens',
                'output_tokens',
                'cache_read_tokens',
                'cache_creation_tokens',
                'thinking_tokens',
                'total_tokens'
            ] <> '{}'::jsonb
            OR NOT (p_after_state ?& ARRAY[
                'previous_total_tokens',
                'corrected_total_tokens',
                'delta_tokens'
            ])
            OR p_after_state - ARRAY[
                'previous_total_tokens',
                'corrected_total_tokens',
                'delta_tokens'
            ] <> '{}'::jsonb
            OR pg_catalog.jsonb_typeof(
                p_after_state -> 'previous_total_tokens') IS DISTINCT FROM 'string'
            OR (p_after_state ->> 'previous_total_tokens')
                !~ v_unsigned_token_pattern
            OR pg_catalog.jsonb_typeof(
                p_after_state -> 'corrected_total_tokens') IS DISTINCT FROM 'string'
            OR (p_after_state ->> 'corrected_total_tokens')
                !~ v_unsigned_token_pattern
            OR pg_catalog.jsonb_typeof(p_after_state -> 'delta_tokens')
                IS DISTINCT FROM 'string'
            OR (p_after_state ->> 'delta_tokens') !~ v_signed_token_pattern
            OR EXISTS (
                SELECT 1
                FROM pg_catalog.jsonb_each(
                    p_metadata -> 'token_counts') AS token
                WHERE pg_catalog.jsonb_typeof(token.value) <> 'string'
                   OR token.value #>> '{}' !~ v_unsigned_token_pattern
            ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '22023',
                MESSAGE = 'poolai_attempt_fact_audit_invalid';
        END IF;
    ELSE
        IF p_request_id IS NULL
            OR p_metadata - ARRAY[
                'quota_event_id',
                'group_id',
                'period_id',
                'reservation_id',
                'attempt_id',
                'outcome',
                'usage_source'
            ] <> '{}'::jsonb
            OR NOT (p_after_state ?& ARRAY[
                'input_tokens',
                'output_tokens',
                'cache_read_tokens',
                'cache_creation_tokens',
                'thinking_tokens',
                'total_tokens'
            ])
            OR p_after_state - ARRAY[
                'input_tokens',
                'output_tokens',
                'cache_read_tokens',
                'cache_creation_tokens',
                'thinking_tokens',
                'total_tokens'
            ] <> '{}'::jsonb
            OR EXISTS (
                SELECT 1
                FROM pg_catalog.jsonb_each(p_after_state) AS token
                WHERE pg_catalog.jsonb_typeof(token.value) <> 'string'
                   OR token.value #>> '{}' !~ v_unsigned_token_pattern
            )
            OR (
                p_action = 'group_quota.attempt_fact_conservative_expired'
                AND (p_metadata ->> 'usage_source')
                    IS DISTINCT FROM 'conservative_estimate'
            ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '22023',
                MESSAGE = 'poolai_attempt_fact_audit_invalid';
        END IF;
    END IF;

    INSERT INTO public.audit_logs (
        id, actor_type, actor_user_id, action, target_type, target_id,
        request_id, reason, ip_address, user_agent, before_state,
        after_state, metadata
    ) VALUES (
        p_id, p_actor_type, p_actor_user_id, p_action, p_target_type, p_target_id,
        p_request_id, p_reason, p_ip_address, p_user_agent, p_before_state,
        p_after_state, p_metadata
    )
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    IF v_inserted = 1 THEN
        RETURN 'created';
    END IF;

    SELECT audit.actor_type,
           audit.actor_user_id,
           audit.action,
           audit.target_type,
           audit.target_id,
           audit.request_id,
           audit.reason,
           audit.ip_address,
           audit.user_agent,
           audit.before_state,
           audit.after_state,
           audit.metadata
    INTO v_existing
    FROM public.audit_logs AS audit
    WHERE audit.id = p_id;

    IF FOUND
        AND v_existing.actor_type IS NOT DISTINCT FROM p_actor_type
        AND v_existing.actor_user_id IS NOT DISTINCT FROM p_actor_user_id
        AND v_existing.action IS NOT DISTINCT FROM p_action
        AND v_existing.target_type IS NOT DISTINCT FROM p_target_type
        AND v_existing.target_id IS NOT DISTINCT FROM p_target_id
        AND v_existing.request_id IS NOT DISTINCT FROM p_request_id
        AND v_existing.reason IS NOT DISTINCT FROM p_reason
        AND v_existing.ip_address IS NOT DISTINCT FROM p_ip_address
        AND v_existing.user_agent IS NOT DISTINCT FROM p_user_agent
        AND v_existing.before_state IS NOT DISTINCT FROM p_before_state
        AND v_existing.after_state IS NOT DISTINCT FROM p_after_state
        AND v_existing.metadata IS NOT DISTINCT FROM p_metadata THEN
        RETURN 'replayed';
    END IF;

    RAISE EXCEPTION USING
        ERRCODE = '23505',
        MESSAGE = 'poolai_attempt_fact_audit_conflict';
END;
$function$;

COMMENT ON FUNCTION public.poolai_operations_append_attempt_fact_audit_once(
    uuid, text, uuid, text, text, uuid, uuid, text, inet, text, jsonb, jsonb, jsonb
) IS
    'Appends one allowlisted GroupQuota attempt-fact audit; exact deterministic-ID retries converge and contradictory collisions fail closed.';

ALTER FUNCTION public.poolai_operations_append_attempt_fact_audit_once(
    uuid, text, uuid, text, text, uuid, uuid, text, inet, text, jsonb, jsonb, jsonb
) OWNER TO poolai_runtime_owner;

REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_operations_append_attempt_fact_audit_once(
    uuid, text, uuid, text, text, uuid, uuid, text, inet, text, jsonb, jsonb, jsonb
) FROM PUBLIC, poolai_api, poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_operations_append_attempt_fact_audit_once(
    uuid, text, uuid, text, text, uuid, uuid, text, inet, text, jsonb, jsonb, jsonb
) TO poolai_api, poolai_worker;
RESET ROLE;

DO $permission_audit$
DECLARE
    v_api_oid oid;
    v_audit_function_oid oid;
    v_owner_oid oid;
    v_replay_function_oid oid;
    v_worker_oid oid;
BEGIN
    SELECT role.oid INTO v_api_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_api';

    SELECT role.oid INTO v_worker_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_worker';

    SELECT role.oid INTO v_owner_oid
    FROM pg_catalog.pg_roles AS role
    WHERE role.rolname = 'poolai_runtime_owner';

    v_replay_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_operations_replay_dead_outbox(uuid,uuid,text)');
    v_audit_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_operations_append_attempt_fact_audit_once(uuid,text,uuid,text,text,uuid,uuid,text,inet,text,jsonb,jsonb,jsonb)');

    IF v_api_oid IS NULL
        OR v_worker_oid IS NULL
        OR v_owner_oid IS NULL
        OR v_replay_function_oid IS NULL
        OR v_audit_function_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_delivery_audit_dependency_missing';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_worker', 'public.outbox_messages', 'INSERT')
        OR NOT pg_catalog.has_any_column_privilege(
            'poolai_worker', 'public.outbox_messages', 'INSERT')
        OR pg_catalog.has_column_privilege(
            'poolai_worker',
            'public.outbox_messages',
            'replay_of',
            'INSERT')
        OR EXISTS (
            WITH expected(column_name) AS (
                VALUES
                    ('id'),
                    ('deduplication_key'),
                    ('topic'),
                    ('schema_version'),
                    ('aggregate_type'),
                    ('aggregate_id'),
                    ('aggregate_version'),
                    ('event_type'),
                    ('source_event_sequence'),
                    ('correlation_id'),
                    ('causation_id'),
                    ('payload'),
                    ('occurred_at')
            ), actual AS (
                SELECT privilege.column_name
                FROM information_schema.column_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.table_name = 'outbox_messages'
                  AND privilege.grantee = 'poolai_worker'
                  AND privilege.privilege_type = 'INSERT'
            )
            SELECT 1
            FROM (
                (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            ) AS difference
        )
        OR EXISTS (
            SELECT 1
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'outbox_messages'
              AND privilege.grantee = 'poolai_worker'
              AND privilege.privilege_type = 'INSERT'
              AND privilege.is_grantable <> 'NO'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_worker_outbox_insert_boundary_missing';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_worker', 'public.audit_logs', 'INSERT')
        OR pg_catalog.has_table_privilege(
            'poolai_worker',
            'public.audit_logs',
            'SELECT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        OR NOT pg_catalog.has_any_column_privilege(
            'poolai_worker', 'public.audit_logs', 'INSERT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_worker',
            'public.audit_logs',
            'SELECT, UPDATE, REFERENCES')
        OR EXISTS (
            WITH expected(column_name) AS (
                VALUES
                    ('id'),
                    ('actor_type'),
                    ('actor_user_id'),
                    ('action'),
                    ('target_type'),
                    ('target_id'),
                    ('request_id'),
                    ('reason'),
                    ('ip_address'),
                    ('user_agent'),
                    ('before_state'),
                    ('after_state'),
                    ('metadata')
            ), actual AS (
                SELECT privilege.column_name
                FROM information_schema.column_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.table_name = 'audit_logs'
                  AND privilege.grantee = 'poolai_worker'
                  AND privilege.privilege_type = 'INSERT'
            )
            SELECT 1
            FROM (
                (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            ) AS difference
        )
        OR EXISTS (
            SELECT 1
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'audit_logs'
              AND privilege.grantee = 'poolai_worker'
              AND privilege.privilege_type = 'INSERT'
              AND privilege.is_grantable <> 'NO'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_worker_audit_insert_boundary_missing';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_runtime_owner',
            'public.audit_logs',
            'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        OR EXISTS (
            WITH expected(column_name, privilege_type) AS (
                VALUES
                    ('id', 'SELECT'),
                    ('actor_type', 'SELECT'),
                    ('actor_user_id', 'SELECT'),
                    ('action', 'SELECT'),
                    ('target_type', 'SELECT'),
                    ('target_id', 'SELECT'),
                    ('request_id', 'SELECT'),
                    ('reason', 'SELECT'),
                    ('ip_address', 'SELECT'),
                    ('user_agent', 'SELECT'),
                    ('before_state', 'SELECT'),
                    ('after_state', 'SELECT'),
                    ('metadata', 'SELECT'),
                    ('id', 'INSERT'),
                    ('actor_type', 'INSERT'),
                    ('actor_user_id', 'INSERT'),
                    ('action', 'INSERT'),
                    ('target_type', 'INSERT'),
                    ('target_id', 'INSERT'),
                    ('request_id', 'INSERT'),
                    ('reason', 'INSERT'),
                    ('ip_address', 'INSERT'),
                    ('user_agent', 'INSERT'),
                    ('before_state', 'INSERT'),
                    ('after_state', 'INSERT'),
                    ('metadata', 'INSERT')
            ), actual AS (
                SELECT privilege.column_name, privilege.privilege_type
                FROM information_schema.column_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.table_name = 'audit_logs'
                  AND privilege.grantee = 'poolai_runtime_owner'
                  AND privilege.privilege_type IN (
                      'SELECT', 'INSERT', 'UPDATE', 'REFERENCES')
            )
            SELECT 1
            FROM (
                (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            ) AS difference
        )
        OR EXISTS (
            SELECT 1
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'audit_logs'
              AND privilege.grantee = 'poolai_runtime_owner'
              AND privilege.is_grantable <> 'NO'
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_audit_owner_table_boundary_missing';
    END IF;

    IF (
        SELECT pg_catalog.count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname =
              'poolai_operations_append_attempt_fact_audit_once'
    ) <> 1
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = procedure.proowner
            WHERE procedure.oid = v_audit_function_oid
              AND procedure.prosecdef
              AND procedure.provolatile = 'v'
              AND NOT procedure.proretset
              AND procedure.prorettype = 'pg_catalog.text'::regtype::oid
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND procedure.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND procedure.proargnames = ARRAY[
                  'p_id',
                  'p_actor_type',
                  'p_actor_user_id',
                  'p_action',
                  'p_target_type',
                  'p_target_id',
                  'p_request_id',
                  'p_reason',
                  'p_ip_address',
                  'p_user_agent',
                  'p_before_state',
                  'p_after_state',
                  'p_metadata'
              ]::text[]
              AND pg_catalog.has_function_privilege(
                  'poolai_api', procedure.oid, 'EXECUTE')
              AND pg_catalog.has_function_privilege(
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
                            v_owner_oid, v_api_oid, v_worker_oid)
                    )
              )
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_attempt_fact_audit_function_boundary_missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS function
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = function.proowner
        WHERE function.oid = v_replay_function_oid
          AND function.prosecdef
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND function.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
          AND pg_catalog.has_function_privilege(
              'poolai_api', function.oid, 'EXECUTE')
          AND NOT pg_catalog.has_function_privilege(
              'poolai_worker', function.oid, 'EXECUTE')
          AND pg_catalog.has_column_privilege(
              'poolai_runtime_owner',
              'public.outbox_messages',
              'replay_of',
              'INSERT')
          AND NOT pg_catalog.has_column_privilege(
              'poolai_api',
              'public.outbox_messages',
              'replay_of',
              'INSERT')
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_api_replay_boundary_missing';
    END IF;

    IF NOT pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_quota_expire(uuid,uuid,uuid,uuid,text,text)',
            'EXECUTE')
        OR NOT pg_catalog.has_function_privilege(
            'poolai_worker',
            'public.poolai_quota_adjust_usage(uuid,uuid,uuid,uuid,text,text,text,integer,text,numeric,numeric,numeric,numeric,numeric,text,text,jsonb,timestamptz,timestamptz,timestamptz,text,uuid,uuid,text,text)',
            'EXECUTE')
        OR pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_worker_entry_point_or_schema_boundary_missing';
    END IF;
END;
$permission_audit$;
