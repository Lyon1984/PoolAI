-- PoolAI Release 1 M3-E4 Outbox query-scaling indexes.
--
-- Routed claims build logical lineage only from unresolved physical messages
-- and use a narrow published-lineage existence proof. Observability still
-- reports exact cumulative dead/replay counts, but reads only the relevant
-- partial indexes instead of retained published non-replay history. This is a
-- performance-only forward migration: it changes no fact, authority, state
-- transition, role grant, or public contract. Signed migrations remain
-- immutable.

CREATE INDEX ix_outbox_messages_unresolved_lineage
    ON public.outbox_messages (
        topic,
        aggregate_id,
        source_event_sequence,
        event_sequence
    )
    WHERE status <> 'published';

CREATE INDEX ix_outbox_messages_published_lineage
    ON public.outbox_messages (
        topic,
        aggregate_id,
        (coalesce(source_event_sequence, 0))
    )
    WHERE status = 'published';

CREATE INDEX ix_outbox_messages_backlog_metrics
    ON public.outbox_messages (occurred_at, event_sequence)
    WHERE status IN ('pending', 'processing');

CREATE INDEX ix_outbox_messages_dead_metrics
    ON public.outbox_messages (event_sequence)
    WHERE status = 'dead';

CREATE INDEX ix_outbox_messages_replay_metrics
    ON public.outbox_messages (event_sequence)
    WHERE replay_of IS NOT NULL;
