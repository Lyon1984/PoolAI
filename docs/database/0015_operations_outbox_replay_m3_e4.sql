-- PoolAI Release 1 M3-E4 safe Integration Event replay boundary.
--
-- The API role must not read dead-letter payloads or copy Outbox rows directly.
-- This SECURITY DEFINER entry point locks one source row, verifies that it is
-- terminal dead, and creates a new pending transport message with a new
-- identity/deduplication key. The original message is never modified.

-- The NOLOGIN function owner receives only the columns needed to lock and copy
-- an immutable envelope. Runtime callers receive no new direct table access.
GRANT SELECT (
    id,
    event_sequence,
    deduplication_key,
    topic,
    schema_version,
    aggregate_type,
    aggregate_id,
    aggregate_version,
    event_type,
    source_event_sequence,
    correlation_id,
    causation_id,
    payload,
    occurred_at,
    status,
    replay_of
) ON public.outbox_messages TO poolai_runtime_owner;
-- PostgreSQL row locking requires UPDATE privilege on at least one selected
-- column. The stable primary key is the only lock-only column granted here;
-- the function never updates the source row.
GRANT UPDATE (id) ON public.outbox_messages TO poolai_runtime_owner;
GRANT INSERT (
    id,
    deduplication_key,
    topic,
    schema_version,
    aggregate_type,
    aggregate_id,
    aggregate_version,
    event_type,
    source_event_sequence,
    correlation_id,
    causation_id,
    payload,
    occurred_at,
    replay_of
) ON public.outbox_messages TO poolai_runtime_owner;

GRANT CREATE ON SCHEMA public TO poolai_runtime_owner;

CREATE FUNCTION public.poolai_operations_replay_dead_outbox(
    p_source_message_id uuid,
    p_new_message_id uuid,
    p_new_deduplication_key text
)
RETURNS TABLE (
    disposition text,
    new_message_id uuid,
    event_sequence bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, pg_temp
AS $function$
DECLARE
    v_existing record;
    v_existing_count bigint;
    v_new_event_sequence bigint;
    v_source record;
BEGIN
    IF p_source_message_id IS NULL
        OR p_new_message_id IS NULL
        OR p_source_message_id = p_new_message_id
        OR p_new_deduplication_key IS NULL
        OR pg_catalog.btrim(p_new_deduplication_key) = '' THEN
        RETURN QUERY SELECT
            'validation_failed'::text,
            NULL::uuid,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT source.id,
           source.deduplication_key,
           source.topic,
           source.schema_version,
           source.aggregate_type,
           source.aggregate_id,
           source.aggregate_version,
           source.event_type,
           source.source_event_sequence,
           source.correlation_id,
           source.causation_id,
           source.payload,
           source.occurred_at,
           source.status,
           source.replay_of
    INTO v_source
    FROM public.outbox_messages AS source
    WHERE source.id = p_source_message_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT
            'source_not_found'::text,
            NULL::uuid,
            NULL::bigint;
        RETURN;
    END IF;

    IF v_source.status <> 'dead' THEN
        RETURN QUERY SELECT
            'source_not_dead'::text,
            NULL::uuid,
            NULL::bigint;
        RETURN;
    END IF;

    SELECT pg_catalog.count(*)
    INTO v_existing_count
    FROM public.outbox_messages AS existing
    WHERE existing.id = p_new_message_id
       OR existing.deduplication_key = p_new_deduplication_key;

    IF v_existing_count > 0 THEN
        SELECT existing.id,
               existing.event_sequence,
               existing.deduplication_key,
               existing.topic,
               existing.schema_version,
               existing.aggregate_type,
               existing.aggregate_id,
               existing.aggregate_version,
               existing.event_type,
               existing.source_event_sequence,
               existing.correlation_id,
               existing.causation_id,
               existing.payload,
               existing.occurred_at,
               existing.replay_of
        INTO v_existing
        FROM public.outbox_messages AS existing
        WHERE existing.id = p_new_message_id
           OR existing.deduplication_key = p_new_deduplication_key
        ORDER BY
            CASE WHEN existing.id = p_new_message_id THEN 0 ELSE 1 END,
            existing.event_sequence
        LIMIT 1;

        IF v_existing_count = 1
            AND v_existing.id = p_new_message_id
            AND v_existing.deduplication_key = p_new_deduplication_key
            AND v_existing.topic = v_source.topic
            AND v_existing.schema_version = v_source.schema_version
            AND v_existing.aggregate_type = v_source.aggregate_type
            AND v_existing.aggregate_id = v_source.aggregate_id
            AND v_existing.aggregate_version IS NOT DISTINCT FROM
                v_source.aggregate_version
            AND v_existing.event_type = v_source.event_type
            AND v_existing.source_event_sequence IS NOT DISTINCT FROM
                v_source.source_event_sequence
            AND v_existing.correlation_id = v_source.correlation_id
            AND v_existing.causation_id IS NOT DISTINCT FROM
                v_source.causation_id
            AND v_existing.payload = v_source.payload
            AND v_existing.occurred_at = v_source.occurred_at
            AND v_existing.replay_of = v_source.id THEN
            RETURN QUERY SELECT
                'replayed'::text,
                v_existing.id,
                v_existing.event_sequence;
            RETURN;
        END IF;

        RETURN QUERY SELECT
            'replay_conflict'::text,
            NULL::uuid,
            NULL::bigint;
        RETURN;
    END IF;

    BEGIN
        INSERT INTO public.outbox_messages (
            id,
            deduplication_key,
            topic,
            schema_version,
            aggregate_type,
            aggregate_id,
            aggregate_version,
            event_type,
            source_event_sequence,
            correlation_id,
            causation_id,
            payload,
            occurred_at,
            replay_of
        ) VALUES (
            p_new_message_id,
            p_new_deduplication_key,
            v_source.topic,
            v_source.schema_version,
            v_source.aggregate_type,
            v_source.aggregate_id,
            v_source.aggregate_version,
            v_source.event_type,
            v_source.source_event_sequence,
            v_source.correlation_id,
            v_source.causation_id,
            v_source.payload,
            v_source.occurred_at,
            v_source.id
        )
        RETURNING outbox_messages.event_sequence
        INTO v_new_event_sequence;
    EXCEPTION
        WHEN unique_violation THEN
            RETURN QUERY SELECT
                'replay_conflict'::text,
                NULL::uuid,
                NULL::bigint;
            RETURN;
    END;

    RETURN QUERY SELECT
        'created'::text,
        p_new_message_id,
        v_new_event_sequence;
END;
$function$;

COMMENT ON FUNCTION public.poolai_operations_replay_dead_outbox(
    uuid, uuid, text
) IS
    'Copies one locked dead Outbox envelope to a new pending message without exposing its payload; exact retries return the original replay receipt.';

ALTER FUNCTION public.poolai_operations_replay_dead_outbox(
    uuid, uuid, text
) OWNER TO poolai_runtime_owner;

REVOKE CREATE ON SCHEMA public FROM poolai_runtime_owner;

SET LOCAL ROLE poolai_runtime_owner;
REVOKE ALL ON FUNCTION public.poolai_operations_replay_dead_outbox(
    uuid, uuid, text
) FROM PUBLIC, poolai_api, poolai_worker;
GRANT EXECUTE ON FUNCTION public.poolai_operations_replay_dead_outbox(
    uuid, uuid, text
) TO poolai_api;
RESET ROLE;

-- Fail closed if the function owner/search path/return surface changes, if a
-- non-API runtime principal can execute it, or if the API gains direct Outbox
-- read/update/delete/replay insertion rights.
DO $permission_audit$
DECLARE
    v_api_oid oid;
    v_function_oid oid;
    v_owner_oid oid;
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

    v_function_oid := pg_catalog.to_regprocedure(
        'public.poolai_operations_replay_dead_outbox(uuid,uuid,text)');

    IF v_api_oid IS NULL
        OR v_worker_oid IS NULL
        OR v_owner_oid IS NULL
        OR v_function_oid IS NULL THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_role_or_function_missing';
    END IF;

    IF (
        SELECT pg_catalog.count(*)
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = 'public'
          AND procedure.proname = 'poolai_operations_replay_dead_outbox'
    ) <> 1 THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_overload_forbidden';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc AS procedure
        JOIN pg_catalog.pg_roles AS owner
          ON owner.oid = procedure.proowner
        WHERE procedure.oid = v_function_oid
          AND procedure.prosecdef
          AND procedure.provolatile = 'v'
          AND procedure.proretset
          AND owner.rolname = 'poolai_runtime_owner'
          AND NOT owner.rolcanlogin
          AND procedure.proconfig @> ARRAY[
              'search_path=pg_catalog, public, pg_temp'
          ]::text[]
          AND procedure.proargnames = ARRAY[
              'p_source_message_id',
              'p_new_message_id',
              'p_new_deduplication_key',
              'disposition',
              'new_message_id',
              'event_sequence'
          ]::text[]
          AND procedure.proargmodes::text = '{i,i,i,t,t,t}'
          AND procedure.proallargtypes = ARRAY[
              'pg_catalog.uuid'::regtype::oid,
              'pg_catalog.uuid'::regtype::oid,
              'pg_catalog.text'::regtype::oid,
              'pg_catalog.text'::regtype::oid,
              'pg_catalog.uuid'::regtype::oid,
              'pg_catalog.int8'::regtype::oid
          ]::oid[]
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
                    OR acl.grantee NOT IN (v_owner_oid, v_api_oid)
                )
          )
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_function_boundary_missing';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_api', 'public.outbox_messages', 'SELECT')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.outbox_messages', 'SELECT')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.outbox_messages', 'UPDATE')
        OR pg_catalog.has_any_column_privilege(
            'poolai_api', 'public.outbox_messages', 'UPDATE')
        OR pg_catalog.has_table_privilege(
            'poolai_api', 'public.outbox_messages', 'DELETE')
        OR pg_catalog.has_column_privilege(
            'poolai_api', 'public.outbox_messages', 'replay_of', 'INSERT')
        OR pg_catalog.pg_has_role(
            'poolai_api', 'poolai_runtime_owner', 'MEMBER')
        OR pg_catalog.pg_has_role(
            'poolai_worker', 'poolai_runtime_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_direct_table_authority_forbidden';
    END IF;

    IF pg_catalog.has_table_privilege(
            'poolai_runtime_owner',
            'public.outbox_messages',
            'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        OR EXISTS (
            WITH expected(column_name, privilege_type) AS (
                VALUES
                    ('id', 'SELECT'),
                    ('event_sequence', 'SELECT'),
                    ('deduplication_key', 'SELECT'),
                    ('topic', 'SELECT'),
                    ('schema_version', 'SELECT'),
                    ('aggregate_type', 'SELECT'),
                    ('aggregate_id', 'SELECT'),
                    ('aggregate_version', 'SELECT'),
                    ('event_type', 'SELECT'),
                    ('source_event_sequence', 'SELECT'),
                    ('correlation_id', 'SELECT'),
                    ('causation_id', 'SELECT'),
                    ('payload', 'SELECT'),
                    ('occurred_at', 'SELECT'),
                    ('status', 'SELECT'),
                    ('replay_of', 'SELECT'),
                    ('id', 'INSERT'),
                    ('deduplication_key', 'INSERT'),
                    ('topic', 'INSERT'),
                    ('schema_version', 'INSERT'),
                    ('aggregate_type', 'INSERT'),
                    ('aggregate_id', 'INSERT'),
                    ('aggregate_version', 'INSERT'),
                    ('event_type', 'INSERT'),
                    ('source_event_sequence', 'INSERT'),
                    ('correlation_id', 'INSERT'),
                    ('causation_id', 'INSERT'),
                    ('payload', 'INSERT'),
                    ('occurred_at', 'INSERT'),
                    ('next_attempt_at', 'INSERT'),
                    ('replay_of', 'INSERT'),
                    ('id', 'UPDATE')
            ), actual AS (
                SELECT privilege.column_name, privilege.privilege_type
                FROM information_schema.column_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.table_name = 'outbox_messages'
                  AND privilege.grantee = 'poolai_runtime_owner'
                  AND privilege.privilege_type IN ('SELECT', 'INSERT', 'UPDATE')
            )
            SELECT 1
            FROM (
                (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            ) AS difference
        ) THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_owner_table_boundary_missing';
    END IF;

    IF pg_catalog.has_schema_privilege(
            'poolai_runtime_owner', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_api', 'public', 'CREATE')
        OR pg_catalog.has_schema_privilege(
            'poolai_worker', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = '42501',
            MESSAGE = 'poolai_m3_e4_replay_schema_create_forbidden';
    END IF;
END;
$permission_audit$;
