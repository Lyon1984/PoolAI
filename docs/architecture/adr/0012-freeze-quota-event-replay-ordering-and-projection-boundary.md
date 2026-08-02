# ADR 0012: Freeze quota-event replay ordering and the Usage projection boundary

- Status: **Proposed**
- Date: 2026-08-01
- Decider: PoolAI architecture, GroupQuota, Usage, Operations, and security owner (`@Lyon1984`) — pending explicit approval
- Relates to: DEC-038, DEC-040, AC-040, AC-041, AC-045, [M3-E4 Issue #22](https://github.com/Lyon1984/PoolAI/issues/22), ADR 0002, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Architecture approval evidence: pending

## Context

M3-E4 adds the first durable consumer of `poolai.quota.v1`, a generic Outbox
publisher, poison handling, and Admin replay. The signed migration 0015 correctly
creates a replay as a new physical Outbox message: it receives a new
`message_id`, deduplication key, and global Outbox `event_sequence`, while the
original dead row remains immutable. It also correctly preserves the producing
ledger's `source_event_sequence`.

Two frozen descriptions are incompatible if read literally. The database
specification says that Usage uses the physical Outbox `event_sequence` as its
cursor. The architecture specification says that delivery has no global ordering
and an ordered consumer must use a declared topic/aggregate sequence. Advancing a
Group watermark to a replay's later physical sequence can skip an earlier
physical message that was held behind the poison message. Blocking on the
immutable dead physical row forever would make its replay unclaimable instead.

The conflict cannot be hidden in implementation code. The logical quota order is
already available without changing a signed migration: every original and replay
copy carries the same positive `group_quota_events.event_sequence` as
`source_event_sequence`. The physical Outbox sequence remains necessary for
transport identity, Inbox collision detection, and operational diagnostics, but
it is not the business ordering key.

M3-E4 also needs one exact boundary for validation and projection. If an Outbox
row is marked `published` before the durable Usage consumer validates and commits,
an unknown major or contradictory fact cannot legally move that terminal row to
`dead`. Conversely, running consumer work inside the claim transaction would hold
a database transaction across an unbounded delivery attempt. The publication
attempt therefore needs three short transactions separated by in-process work:
claim and commit; durable consumer transaction; terminal delivery CAS.

## Proposed decision

This proposal becomes effective only after `@Lyon1984` approves one exact
candidate through a permanent Issue #44 comment and this ADR is changed to
`Accepted` with that evidence.

### Published Language and the two sequences

`docs/contracts/group-quota-events-v1.json` is the machine-verifiable Published
Language for `topic=poolai.quota.v1`, `schema_version=1`. It codifies the ten
already frozen event types and the Envelope/payload relationships described by
the database specification. Implementations consume that file and its fixtures;
they do not maintain a second handwritten event vocabulary.

- `event_sequence` is the positive, globally unique physical Outbox delivery
  sequence. A replay always receives a new value. Inbox receipts store this value.
- `source_event_sequence` is the positive immutable
  `group_quota_events.event_sequence`. Original and replay messages preserve it.
  It is the logical ordering and checkpoint value for quota consumers.
- A quota partition is exactly
  `poolai.quota.v1:group:{aggregate_id:D}`, using the lower-case canonical Group
  UUID. The Envelope must have `aggregate_type=group` and
  `aggregate_id=payload.group_id`.
- For this quota route, partition and lineage identity is exactly
  `(topic, 'group', aggregate_id, source_event_sequence)`. The raw Envelope
  `aggregate_type` remains a required strict-codec field and must equal `group`,
  but it is not allowed to create a second partition for the same Group. A
  malformed value therefore poisons and blocks that Group position rather than
  escaping into a sibling partition.
- `aggregation_watermarks.last_event_sequence` for projector
  `usage-hourly-v1` stores the last successfully consumed
  `source_event_sequence` for that Group. Gaps caused by other Groups are valid.
- `completed_through` is monotonic and becomes the maximum of its prior value and
  the successfully validated event `occurred_at`; it is not the affected fact's
  possibly older completion time.

The strict codec rejects duplicate JSON property names, malformed UUIDs or
timestamps, non-canonical integer strings, missing required mapping fields,
unknown event types inside schema v1, unknown major schema versions, and any
Envelope/payload disagreement. New optional payload fields remain forward
compatible only when the machine contract permits them; required fields and
existing meanings cannot change within v1.

### Partition order, poison, and replay lineage

A quota logical lineage is all Outbox messages with the same
`topic/'group'/aggregate_id/source_event_sequence`. The original member has
`replay_of = null`; every replay preserves the complete original Envelope,
including its raw `aggregate_type`, even when it points to another dead replay.
That preserved field is still validated by the codec but cannot split the quota
Group's ordering boundary.

The publisher may claim only topics for which an explicit consumer route is
registered. It must never mark an Identity, Supply, Subscription, or other
unrouted event as published merely because a generic publisher exists.

Within one quota Group partition, only the smallest unresolved
`source_event_sequence` is eligible:

1. a lineage is complete when one of its members is `published`;
2. a due `pending` member, or an expired `processing` member, of the earliest
   incomplete lineage may be claimed with the existing owner/generation fence;
3. a dead lineage with no due replay blocks only its own Group partition;
4. a pending replay of that dead lineage is eligible despite its later physical
   Outbox sequence; and
5. another Group remains independently claimable.

A publisher may treat a lineage as globally complete only when an exact member
is already `published`; the proof compares the complete immutable Envelope, not
only the four ordering fields. This global published proof is a claim/convergence
optimization for a completed lineage. It does not stand in for any individual
consumer's Inbox receipt when a prior physical message ended `dead`.

For the normative ordering example, source 7 / physical 20 is dead, source 8 /
physical 21 is waiting, and its Admin replay is physical 42 / source 7. The
projector consumes physical 42 as logical 7, advances the Group watermark to 7,
then consumes physical 21 as logical 8. It must not advance directly to physical
42 or lose physical 21.

The Worker retains the existing PostgreSQL session advisory lock and row-level
owner/generation/lease CAS. Claim commits before dispatch. No consumer, delay, or
external I/O runs in that transaction. Heartbeat and terminal updates use their
own short UoWs; a zero-row CAS means ownership loss and no compensating write.

### Publication guard and durable consumer transaction

For every claimed message, the dispatcher constructs the complete immutable
Envelope and invokes every explicitly registered consumer for the exact
`(topic,schema_version)` route. A duplicate registration is a startup error. All
registered durable consumers must return processed or exact duplicate before the
publisher may CAS the row to `published`.

The Usage consumer performs one short UoW in this order:

1. validate the Envelope and `GroupQuotaEventV1` with the shared strict codec;
2. insert an Inbox receipt containing the physical `message_id`, physical
   `event_sequence`, topic, schema version, and canonical payload hash;
3. for `settled`, conservative `expired`, or `usage_adjusted`, read the immutable
   GroupQuota fact through `GroupQuota.Abstractions`, validate the reference, and
   recompute the affected UTC completion-hour Group and Account projections;
4. UPSERT the complete recomputed buckets; and
5. advance the Group checkpoint to the logical `source_event_sequence` with the
   existing owner/version CAS, then commit once.

Inbox recovery is owned independently by every durable consumer. If consumer A
committed the original physical message before consumer B poisoned it, an Admin
replay can be an exact duplicate for A while it remains new work for B. For a
replay whose logical source is already at or behind A's checkpoint, A may accept
it only when `replay_of` names the direct predecessor and A has that predecessor's
Inbox receipt with the same consumer name, topic, schema version, and canonical
payload hash. A writes the replay's new physical Inbox receipt in that same UoW
and returns exact duplicate without reapplying its projection. A missing
predecessor receipt lets a consumer whose checkpoint has not reached that source
continue normal processing; if its checkpoint is already at or beyond the source,
missing or contradictory proof fails closed. Each later consumer makes the same
decision from its own receipts, so no global consumer-progress row or new table
is needed.

The fact reader returns immutable snapshots for one exact
Group/Period/completion-hour and never exposes a GroupQuota repository, DbContext,
or queryable. Usage does not query `usage_attempts`, adjustments, quota events, or
Outbox tables directly. Bucket values are replaced from facts, never incremented
from delivery count. The aggregation rules are:

- `request_count` is the number of distinct request IDs;
- `attempt_count` is the number of immutable dispatched attempt facts;
- `failure_count` counts `failed` and `cancelled` outcomes;
- `failover_count` counts facts whose `attempt_index > 0`;
- `estimated_attempt_count` counts facts with `is_estimated=true`;
- Group and Account identity come only from each authoritative fact; and
- an adjustment replaces the base Token fields for that attempt without changing
  its request, attempt, failure, failover, or Account ownership counts.

The other seven quota event types still validate and advance the logical
checkpoint but do not add usage. Pre-dispatch release/expiry never creates an
attempt count. Conservative post-dispatch expiry has an immutable fact and does.

If codec, Envelope, Inbox hash, source order, fact reference, or projection
validation fails, the consumer UoW rolls back. The dispatcher returns a bounded
poison reason; the publisher CASes the still-processing physical row to `dead`
and emits a P0 operational event. It does not mark the row published and does not
advance the checkpoint. A transient dependency failure rolls back and returns the
row to pending with bounded retry. A crash after the consumer commit but before
the published CAS redelivers the same physical message; the Inbox makes the
second attempt an exact duplicate and the new owner may complete publication.

Here `published` means that every registered durable consumer accepted the
publication attempt. The consumer transaction necessarily commits before that
terminal confirmation; this is the normal at-least-once confirmation window, not
permission to consume an uncommitted producer fact.

### Settlement audit boundary

The existing Admin quota total-adjust/reset audit remains unchanged. M3-E4 adds a
service audit only when GroupQuota creates or corrects an immutable attempt usage
fact: `settled`, conservative `expired`, and `usage_adjusted`. The audit ID is
deterministically domain-separated from the corresponding immutable quota event
ID, and append-once behavior occurs in the same GroupQuota UoW as the fact, ledger
event, and Outbox row. Exact command replay therefore produces no second audit.

These service audits contain only bounded action, Group/Period/reservation/attempt
identifiers, outcome/source, and canonical Token-count strings. They exclude raw
upstream usage, prompts, credentials, arbitrary exception text, and transport
payloads. Reserve, dispatch, renew, pre-dispatch release, and pre-dispatch expiry
remain observable through their immutable quota event/outbox facts and do not
create high-volume duplicate audit rows.

## Alternatives considered

### Keep the physical Outbox sequence as the Usage cursor

Rejected. A replay's later physical sequence would move the watermark beyond
messages held behind the original poison message.

### Rewrite the replay to reuse the original physical sequence

Rejected. It would violate signed migration 0015, global Outbox uniqueness, and
the requirement that replay is a new auditable delivery attempt.

### Mark published first and add a consumer dead-letter table

Rejected for M3-E4. It requires a new migration and a second replay state machine
while the existing processing/dead/replay lifecycle already closes the failure
loop when validation is part of the publication attempt.

### Block the entire quota topic on one poison message

Rejected. Group is the event aggregate and the declared ordering boundary; one
damaged Group must not stop unrelated Groups.

### Increment projections from event deltas

Rejected. At-least-once delivery, late usage, and adjustment make additive
delivery accounting fragile. Immutable-fact recomputation is deterministic.

## Consequences

- Replay remains a new physical event while recovering from the original logical
  position without skipping later facts.
- Inbox collision detection and Usage ordering intentionally use different
  sequences, and tests must prevent them from being conflated.
- A poison message stops one Group until an Admin creates a valid replay; other
  Groups and other explicitly routed topics can continue.
- Operations owns delivery state and dispatch, GroupQuota owns the event language
  and immutable facts, and Usage owns only projections and watermarks.
- The publisher cannot silently drain unregistered topics.
- Projection and settlement audit are idempotent at database commit boundaries,
  including crash-after-commit redelivery.
- While this ADR remains Proposed, its ordering clarification cannot be used as
  M3-E4 completion or release evidence.

## Migration and rollback impact

- Forward migration 0016 closes two runtime authorities without rewriting
  signed migration 0015: Worker loses `outbox_messages.replay_of` INSERT, and
  attempt-fact audit retries use one fixed-search-path, NOLOGIN-owner
  append-once function. Worker keeps only the pre-existing audit append columns
  needed by health and credential jobs; it receives no Audit read/update/delete
  authority and cannot set `occurred_at`.
- Existing `source_event_sequence`, `replay_of`, Inbox columns, and bigint
  watermarks carry the ordering decision; migration 0016 adds no table or
  mutable fact column. Migrations 0001-0015 and Redis contracts are unchanged.
- OpenAPI tightens the Admin replay `reason` text contract to the runtime's
  Unicode-scalar, non-whitespace, control-free 1..500 validation. Its exact
  candidate requires the normal incremental OpenAPI approval. This ADR does not
  authorize executing any migration remotely.
- A future performance-only index or consumer dead-letter store requires a new
  forward migration and independent database approval; it cannot rewrite 0015.
- Before acceptance, the candidate can be withdrawn. After acceptance, changing
  the partition, sequence roles, publication guard, or projection ownership needs
  a superseding ADR and atomic contract/test updates rather than reinterpretation.

## Security impact

- Unknown major versions, contradictory envelopes/facts, and same-identity hash
  conflicts fail closed, enter dead, and emit P0 without exposing payload data.
- `last_error`, logs, traces, metrics, and operational-event attributes use only
  bounded low-cardinality classifications. IDs, deduplication keys, Admin replay
  reason, raw JSON, prompts, credentials, and exception text are not metric labels.
- Admin replay remains the separately signed API-only 0015 boundary with HTTP
  idempotency and append-only audit in one UoW. This ADR grants no direct Outbox
  table access and no Worker access to the Admin replay function.
- Worker audit access is narrowed from table-wide INSERT to the 13 columns used
  by `PostgresAuditAppender`; exact attempt-fact replay additionally crosses the
  allowlisted append-once function. Neither path grants Audit reads or mutation
  of existing rows.

## Acceptance evidence gate

Before changing this ADR to `Accepted`, the exact candidate must pass:

- machine contract/schema and all positive/negative event fixtures;
- the normative physical-20/21/replay-42 ordering case and cross-Group progress;
- duplicate, unknown-major, Envelope mismatch, fact mismatch, rollback, lease
  takeover, and consumer-commit/publisher-crash tests;
- a malformed quota `aggregate_type` that cannot escape the canonical Group
  partition, plus a multi-consumer replay where only the consumer with the exact
  direct-predecessor Inbox receipt converges as duplicate;
- settlement fact/event/outbox/audit commit-point fault injection and exact replay;
- Architecture Tests proving the Context Map and Host loading boundaries; and
- the repository quality and security gates for the exact candidate commit.

The permanent approval must bind the full candidate commit and the SHA-256 of
`docs/contracts/group-quota-events-v1.json`. Acceptance alone does not authorize
remote database/Redis operations, deployment, M3-E4 closeout, M3 Exit, RC, GA,
or production acceptance.

## Contract and test files updated by this decision

- `docs/README.md`
- `docs/architecture/adr/README.md`
- `docs/architecture/adr/0012-freeze-quota-event-replay-ordering-and-projection-boundary.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/contracts/group-quota-events-v1.json`
- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/fixtures/group-quota-events-v1-*.json`
- `docs/database/0016_operations_delivery_and_fact_audit_m3_e4.sql`
- `docs/database/README.md`
- `docs/开发执行规格-v1.0.md`
- GroupQuota event-codec Contract/Unit tests
- Operations Outbox and Usage projection Unit/Integration/End-to-End tests
