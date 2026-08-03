# ADR 0013: Freeze quota-reconciliation layers, checkpoint alignment, alerting, and the recovery boundary

- Status: **Accepted**
- Date: 2026-08-03
- Decider: PoolAI architecture, GroupQuota, Usage, Operations, public-contract, database, and security owner (`@Lyon1984`)
- Relates to: [M3-E5 Issue #23](https://github.com/Lyon1984/PoolAI/issues/23), ADR 0002, ADR 0012, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Compatibility window ID: `m3-e5-quota-reconciliation-bad-request`
- Base Git commit: `0ee892bf26713326cea667b524f59005b03ea9fd`
- Base OpenAPI SHA-256: `e0a72d2827318ce6f53e520292f3aea5bbaa9b9979ae0bc5b27d7b74a5d640d4`
- Target OpenAPI SHA-256: `9ab3765ac644a665373e34d716ffb53a9ac6fdc7abdd28408d9f398fb9a362bf`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5160055395)
- Architecture approval evidence: [Issue #44 permanent final-candidate approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5164002774), binding candidate `ca4381053995cc0e586a36a91396ee6bb541b4d0` and pre-evidence-backwrite ADR SHA-256 `2190efc78f6398bc2489a4053c850378871437aa62f43abff7ae2e3cd8014e49`; the [prior superseding approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5162578270) binds only candidate `23eb5338bf3aeca8adf7d4645812ec8d3ea21577` and ADR SHA-256 `b22d0625d5410950e973cbc53db29e6c7b35e414d0668cc7577b634624067d6a`, while the [initial approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5160040824) binds only candidate `e4fea33d517c436e5f26f75d79dd675fd8aa63af`; both remain permanent history and are superseded for the final candidate.
- Database/no-migration approval evidence: [Issue #44 permanent database boundary approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5160055552)
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1groups~1{groupId}~1quota~1reconciliation/get/responses/400: new response status was added to an existing operation`

## Context

M3-E5 must make quota-versus-usage discrepancies, reservation leakage,
overage, and aggregation backlog discoverable, attributable, alertable, and
recoverable. Recovery must never silently overwrite an immutable usage fact or
quota ledger entry. M3-E5 is a delivery Epic without a direct Release 1.1
DEC/AC association; this ADR freezes the boundary needed to implement that Epic
and does not invent a new traceability association or constitute M3 Exit.

Three existing descriptions use the word reconciliation for different checks:

- the Admin reconciliation response compares the current period counters with
  effective attempt/adjustment facts and pending reservations;
- the execution SLO compares `quota.consumed` with `usage.period.total` after
  excluding aggregation lag and known adjustments; and
- the database specification also requires event-ledger, duplicate-attempt,
  Outbox, and numeric-boundary integrity checks.

Those checks cannot be collapsed into one signed delta. A current quota counter
compared with a projection at an older checkpoint creates a false mismatch. A
positive discrepancy for one Group and a negative discrepancy for another can
cancel in an aggregate gauge. Delivery lag can explain a stale projection, but
it cannot make a damaged authoritative ledger healthy. Conversely, a healthy
authoritative ledger does not prove that its Usage projection or Outbox delivery
has converged.

ADR 0012 already freezes the required ordering boundary. Physical Outbox
`event_sequence` identifies a delivery attempt, while immutable
`source_event_sequence` identifies logical quota order. The per-Group
`usage-hourly-v1` watermark stores the greatest accepted logical source sequence,
and projection replacement plus checkpoint advancement commit atomically. M3-E5
must use that logical checkpoint and must not reinterpret physical replay order
as usage order.

The existing route also accepts an optional UUID `period_id` but does not declare
the existing `BadRequest` response for malformed input. The repository's
compatibility checker deliberately reports a new response status on an existing
operation as incompatible, even though adding a response-only property is
otherwise additive. The malformed-period behavior therefore needs one exact,
separately approved compatibility window rather than an undocumented response or
silent input coercion.

Finally, the database specification currently says an authoritative mismatch
disables quota. No existing database command gives the detector authority to set
`group_token_quotas.enabled=false`, and granting that authority would require a
forward migration and a new cross-instance quarantine state transition. The
existing Admin Group update boundary can explicitly set the Group to `disabled`
with reason, idempotency, version fencing, and audit. This ADR chooses that
explicit control-plane action and keeps the detector read-only.

## Decision

This revised decision is effective through the permanent final-candidate
architecture approval bound to exact candidate
`ca4381053995cc0e586a36a91396ee6bb541b4d0` and pre-evidence-backwrite ADR
SHA-256 `2190efc78f6398bc2489a4053c850378871437aa62f43abff7ae2e3cd8014e49`.
The prior architecture approvals bind only exact candidates
`23eb5338bf3aeca8adf7d4645812ec8d3ea21577` and
`e4fea33d517c436e5f26f75d79dd675fd8aa63af`; they remain historical evidence,
not approval for the final indexed point-reconciliation candidate. The
independently approved OpenAPI compatibility window and database/no-migration
boundary remain valid only for their unchanged exact hashes, base, diagnostic,
and migration bounds; the final-candidate architecture approval does not widen
either one.

### Three independent reconciliation layers

M3-E5 defines exactly three layers. Every result, metric, operational event, and
runbook step must identify its layer rather than publishing an ambiguous global
`reconciled` value.

#### Layer 1: authoritative quota integrity

GroupQuota owns immutable attempts and adjustments, reservations, quota counters,
quota events, and the business identity referenced by their transactionally produced Outbox facts. For one exact
Group and period, one short PostgreSQL read snapshot computes:

- `fact_consumed_tokens` as the exact sum of effective attempt Tokens, where the
  one immutable adjustment replaces the base attempt Token fields when present;
- `pending_reservation_tokens` as the exact sum of `estimated_tokens` for every
  reservation in `pending`, including pending reservations that belong to a
  closed period;
- `consumed_variance = ledger_consumed_tokens - fact_consumed_tokens`;
- `reserved_variance = ledger_reserved_tokens - pending_reservation_tokens`;
- whether the latest validated quota-event state for the period agrees with the
  period total and the current quota consumed/reserved counters;
- whether each fact-producing transaction has the required quota event; GroupQuota
  exposes the selected period's exact event-sequence identities through bounded,
  strictly ordered keyset pages, and Operations separately correlates every identity
  with exactly one immutable original Outbox lineage without treating a later replay
  as a new fact; and
- duplicate identity, cross-Group/period reference, non-canonical integer, and
  PostgreSQL `numeric(78,0)` boundary violations.

All arithmetic is exact integer arithmetic. Implementations must not convert a
Token value or variance through floating point or a platform integer narrower
than the contract permits. A duplicate protected by a database constraint is
still classified as an authoritative integrity failure if privileged fault
injection or corrupted restored data demonstrates it.

The existing top-level `GroupQuotaReconciliation` fields remain Layer 1 fields.
Their meanings do not change:

- `is_reconciled` is true exactly when both `consumed_variance` and
  `reserved_variance` are canonical string `"0"`;
- it does not assert event/Outbox integrity, Usage projection convergence,
  delivery health, or absence of overage; and
- `data_watermark` is the `occurred_at` of the newest validated authoritative
  quota event for the selected period that is included in the snapshot.

`checked_at` comes from the PostgreSQL clock used by the same snapshot. Application
wall-clock time is not substituted. Omission of `period_id` selects the current
period. A valid UUID that does not name a period owned by the path Group returns
the closed `404 resource_not_found`; existence in another Group is not disclosed.

Layer 1 also identifies two operational conditions that do not redefine
`is_reconciled`:

- a reservation leak candidate is either a non-zero reserved variance or a
  pending reservation still unrecovered more than 60 seconds after its database
  lease expiry; and
- overage Tokens are
  `max(ledger_consumed_tokens - ledger_total_tokens, 0)`.
  Overage caused by an authorized total reduction is a capacity condition, not
  by itself proof of ledger corruption.

#### Layer 2: checkpoint-aligned Usage projection convergence

Usage owns the projection, aggregation watermark, reconciliation use case, and
the Admin endpoint implementation. It does not query GroupQuota tables, Outbox,
or another context's DbContext directly. It reads its selected period projection
and the Group's `usage-hourly-v1` watermark in one short PostgreSQL statement or
snapshot, then calls a narrow immutable reader declared by
`GroupQuota.Abstractions`.

The comparison point is exactly the watermark's logical
`last_event_sequence`. GroupQuota returns the expected consumed total for the
selected period at that checkpoint from the latest validated
`group_quota_events.consumed_tokens_after` whose logical event sequence is less
than or equal to the checkpoint. A later event is excluded even if it committed
before the reconciliation request. When a Group checkpoint has already advanced
into a later period, a selected closed period is clamped to its own last event;
that normal historical query is not a checkpoint-ahead failure. Because quota
events are immutable and the Usage projection/checkpoint commit is atomic, the
two contexts do not need a shared transaction for this historical comparison.

The Layer 2 variance is:

`expected_consumed_tokens_at_checkpoint - projected_consumed_tokens`

It is not current ledger consumed minus a stale projection. Known adjustments
are already represented in both the event state and the effective Usage
projection; implementations must not apply an undocumented second adjustment
exclusion.

The public v1 schema gains one additive, optional `usage_projection` object. The
M3-E5 target emits it on every successful response, including non-healthy states,
but keeping the property optional avoids retrospectively strengthening the
existing v1 response requirement. Its closed object contains:

- `status` with exactly `not_started`, `lagging`, `reconciled`, `mismatched`, or
  `blocked`;
- `expected_consumed_tokens` and `projected_consumed_tokens` as
  `AggregateTokenCount` decimal strings;
- `consumed_variance` as a `SignedAggregateTokenCount` decimal string;
- `checkpoint_source_event_sequence` and `latest_source_event_sequence` as
  non-negative decimal-string aggregate counts; and
- nullable `data_through`, equal to the watermark's `completed_through` when
  present.

Status classification has this precedence:

1. `blocked` when Layer 1/event-chain validation failed, the checkpoint is ahead
   of the latest logical source sequence for the Group as a whole, or a safe
   checkpoint expectation cannot be established;
2. `not_started` when no `usage-hourly-v1` watermark exists or its initial
   checkpoint has not accepted a quota event;
3. `mismatched` when the exact checkpoint-aligned variance is non-zero;
4. `lagging` when the checkpoint-aligned variance is zero but the checkpoint is
   behind the selected period's latest logical source sequence; and
5. `reconciled` only when the checkpoint-aligned variance is zero and the
   checkpoint has caught up to the latest logical source sequence.

The precedence makes a true projection defect visible even while newer events
are waiting. Lag is separately observable and cannot suppress a mismatch at the
checkpoint already claimed by Usage.

#### Layer 3: delivery and recovery health

Operations owns physical Outbox state, Inbox/delivery diagnostics, replay, and
backlog/dead telemetry. Usage may consume only a narrow delivery-health port; it
does not query Outbox rows directly. Layer 3 reports whether an unresolved
logical lineage, dead physical message, due pending row, expired processing
lease, or consumer checkpoint backlog explains why Layer 2 has not caught up.

Existing Outbox pending, oldest-age, dead, and replay metrics remain the source
of truth for delivery health. A dead or delayed delivery is not folded into the
signed Token variance, and a healthy delivery state cannot clear a Layer 1 or
Layer 2 mismatch. Structured diagnostics may carry bounded Group/period/message
identifiers needed by an authorized operator, but those identifiers never become
metric labels.

For every exact selected-period logical source identity at or below the accepted
`usage-hourly-v1` checkpoint, Operations also requires one exact durable Inbox
receipt on any physical message in that logical lineage: the original or a replay.
The narrow reader verifies the literal consumer name, quota topic, physical Outbox
`event_sequence`, and schema version. It does not parse payloads or return payload
hashes; the existing Inbox appender remains responsible for the transactional
payload-hash invariant. No receipt is required for a source identity newer than
the checkpoint. A missing receipt or any receipt metadata conflict is a Layer 3
P0 and contributes to the bounded blocking source identity and oldest diagnostic
age without being reclassified as a Token variance.

### Runtime ownership and scheduling

The HTTP route remains tagged `AdminGroups`, but the tag does not transfer domain
ownership. Endpoint code calls a Usage Application query. The on-demand API
query composes:

- a Usage-owned projection/checkpoint reader;
- a narrow `GroupQuota.Abstractions` reconciliation-fact reader.

The API role deliberately cannot read Outbox/Inbox and the public response does
not claim Layer 3 health. The continuous Worker scan additionally composes the
narrow Operations delivery-health reader. It keyset-pages the exact
selected-period source sequences from GroupQuota, submits only a bounded page of
those identities to Operations, and correlates each with exactly one original
Outbox lineage. A same-Group event from another period cannot fill a missing
identity, and a duplicate original is an authoritative failure. Only envelope
metadata is read; neither Usage nor GroupQuota parses or queries Operations
persistence directly.

No returned port exposes a repository, queryable, DbContext, SQL fragment, or
mutable entity. Each reader uses its own short read UoW. No database transaction
is held across another port call, alert emission, metrics collection, retry,
network I/O, or backoff.

Continuous scanning runs only in `PoolAI.Worker` as the versioned job
`WorkerJobs.QuotaReconciliation`. One dedicated PostgreSQL session advisory lock
provides single active ownership and crash takeover. Work is bounded by stable
keyset batches; it must not materialize all Groups or retained event history on
every poll. At the start of a candidate lineage, the Worker freezes the complete
projection/fact snapshot and its Group/period/fact/checkpoint identity in the
continuation. At most one candidate lineage continuation is retained globally,
and every candidate visit reads at most one 1000-identity page. Intermediate
visits reuse that frozen snapshot and must not rescan the complete selected period
for every page. An incomplete lineage does not advance the Group cursor or scan a
later candidate. Immediately before completing the candidate and publishing its
metrics, the Worker exactly rereads the projection/fact snapshot and compares its
Group/period/fact/checkpoint identity with the frozen identity. Lock ownership
loss, an incomplete-lineage invariant failure, a missing or failed final reread,
or any identity change discards the partial continuation, Group cursor, and
unpublished pass aggregate before restarting the complete candidate keyset pass
from the beginning. `PoolAI.Api` serves the on-demand query but never starts this
loop.

The scanner is read-only. It does not adjust consumed/reserved/total, synthesize
an event or Outbox row, change a reservation, rewrite a projection, rewind an
Inbox/checkpoint, disable a Group, or call an Admin command on an operator's
behalf. Alert or telemetry failure cannot change reconciliation truth.

### Public API and the exact compatibility window

`GET /api/v1/admin/groups/{groupId}/quota/reconciliation` retains its existing
admin/operator/auditor read authorization, current-period default, success body,
and existing error responses. The M3-E5 candidate adds:

- the additive `usage_projection` response property described above; and
- the existing closed `BadRequest` response at status 400 for a malformed
  `period_id`, using `error_code=invalid_request`.

No new stable error code is introduced. A syntactically valid UUID that is
absent or belongs to another Group remains `404 resource_not_found`.

Adding status 400 to an existing operation must be governed by exactly one
registry entry named `m3-e5-quota-reconciliation-bad-request`. That entry and
this ADR pin the exact comparison-base commit, base OpenAPI SHA-256, target
OpenAPI SHA-256, and complete sorted diagnostic set recorded in the metadata
above. No other diagnostic is expected or permitted.

The actual compatibility command is authoritative. If it produces another
diagnostic, implementation stops; the window must not be broadened by wildcard,
partial pointer, guessed hash, or reused approval. While either the registry or
this ADR remains proposed, the compatibility gate must fail as pending approval
and waive nothing. Acceptance is one atomic governance transition after a
permanent Issue #44 approval exists: exact hashes and diagnostics, registry
status/evidence, ADR status/evidence, generated contracts, fixtures, and release
manifest must agree.

### Metrics and alert semantics

M3-E5 emits the existing and additive metrics with only bounded labels:

- `poolai_quota_reconciliation_delta_tokens{group_tier="default"}` is the sum of
  absolute Layer 1 and checkpoint-aligned Layer 2 Token deltas represented by
  that observation, never a signed net that can cancel across Groups;
- `poolai_quota_reconciliation_mismatched_groups{kind}` is a gauge with `kind`
  exactly `authoritative`, `projection`, or `delivery`;
- `poolai_quota_reservation_leak_candidates{kind}` is a gauge with `kind`
  exactly `counter_variance` or `overdue`;
- `poolai_quota_reservation_oldest_overdue_seconds` is a non-negative gauge;
- `poolai_quota_overage_tokens{group_tier="default"}` is a non-negative exact
  total converted to telemetry only with a finite, explicitly tested policy; and
- the existing Outbox pending, oldest-age, dead, and replay metrics continue to
  represent delivery backlog.

Release 1 defines no pricing or subscription-derived Group tier. Therefore the
only allowed `group_tier` value is the literal low-cardinality `default`.
`group_id`, `period_id`, event/message/reservation/attempt IDs, raw model names,
deduplication keys, reasons, exception text, payloads, credentials, and connection
data are prohibited as metric labels.

Alert classification is fixed as follows:

- any authoritative counter/fact/event/Outbox contradiction, duplicate identity,
  cross-Group fact, non-canonical value, or 78-digit risk emits an immediate P0
  operational event and blocks release evidence;
- a non-zero checkpoint-aligned Layer 2 variance alerts after it remains non-zero
  for five minutes; the monitoring rule, rather than a new application table,
  owns the sustained window and recovery notification;
- aggregation lag, unresolved Outbox backlog, dead lineage, and checkpoint-covered
  Inbox receipt faults alert separately from projection mismatch;
- a reservation still pending more than 60 seconds after lease expiry is a
  critical recovery-SLO violation; and
- overage without integrity damage is a capacity warning, not an integrity P0.

The repository emits metrics and bounded operational events and documents alert
rule semantics. Until the production telemetry and paging destinations are
approved separately, M3-E5 must not claim that an external page was delivered or
acknowledged.

### Fail-closed and recovery boundary

Detection preserves evidence and never repairs in the observation transaction.
The required runbook first classifies the condition as authoritative,
projection, or delivery:

1. For an authoritative P0, an authorized Admin explicitly changes the affected
   Group to `disabled` through `adminUpdateGroup`, with `If-Match`,
   `Idempotency-Key`, bounded reason, and append-only audit. The detector does not
   impersonate the Admin and does not mutate `group_token_quotas.enabled`.
2. Authoritative recovery uses only a formal immutable usage adjustment,
   reviewed fix-forward migration/repair, or tested PostgreSQL PITR as appropriate.
   Operators never directly UPDATE/DELETE counters, attempts, adjustments,
   reservations, quota events, Outbox, or Inbox rows.
3. A projection-only mismatch may run a Usage-owned, single-Group/single-period,
   fenced bounded rebuild over at most 744 ordered exact UTC-hour buckets. It
   requires the dedicated `poolai:r1:worker:usage-rebuild:v1` session lock, claims the existing
   `usage-hourly-v1` checkpoint lease, and recomputes each bucket only from
   immutable terminal facts and adjustments visible at that unchanged accepted
   logical checkpoint. It replaces or deletes only the selected derived Group
   and Account hourly rows. For each bucket it borrows a short PostgreSQL UoW from
   the same session that owns the advisory lock, heartbeats the checkpoint
   owner/version fence, writes or deletes the derived projection, and commits all
   of those actions atomically. Lease expiry, owner/version takeover, or lock
   session termination therefore rolls back the bucket and cannot leave a stale
   projection write. It verifies the aligned variance before reporting completion.
   It does not create, advance,
   rewind, or otherwise change a checkpoint, GroupQuota fact, Outbox row, or
   Inbox receipt. A missing/damaged checkpoint or Inbox requires a separately
   reviewed fix-forward; M3-E5 does not add a general rewind facility. M5-E1
   retains ownership of general rebuild capability.
   The only production entry is a default-disabled, one-shot Worker mode selected
   by `WorkerJobs:UsageRebuild:Enabled=true` with exact UUID `GroupId`/`PeriodId`
   and exact UTC-hour `FirstBucketStart`/`LastBucketStart` values. That mode starts
   no normal Worker loops, attempts the rebuild exactly once, stops the Host, and
   exits non-zero for busy, ownership/lease loss, invalid authoritative state,
   remaining variance, cancellation, or failure. Operators must return `Enabled`
   to false or remove it before restarting the normal Worker.
4. A delivery-only fault uses the existing owner/generation fences, retry, poison,
   and Admin Outbox replay boundary. Replay preserves logical
   `source_event_sequence`; it does not become a second usage fact.
5. Re-enabling a Group is another explicit Admin action after exact Layer 1 and
   Layer 2 zero variances, acceptable delivery lag/backlog, recovered reservation
   SLO, and retained evidence are verified.

This is the no-migration fail-closed boundary: detection blocks acceptance and
demands explicit Group disable, but it creates no hidden automatic quarantine
state. Automatic cross-instance quota quarantine would require a new durable
state transition, command/function, runtime permissions, event semantics,
recovery command, migration, and separate database/API approval. It is not
authorized by this ADR.

## Alternatives considered

### Compare the current quota counter with the current Usage projection

Rejected. A healthy projection behind its logical checkpoint would appear
corrupt whenever a newer quota event had already committed.

### Treat lag, delivery failure, and fact mismatch as one reconciliation delta

Rejected. The causes have different owners and recovery paths, and signed
aggregation can cancel unrelated Group failures.

### Let Usage query GroupQuota and Outbox tables directly

Rejected. It violates the Context Map, makes ownership implicit in SQL, and
couples a projection use case to two foreign persistence models.

### Automatically overwrite counters or projections when a mismatch is found

Rejected. It destroys evidence, can turn a detector bug into ledger corruption,
and violates the immutable-fact and explicit recovery requirements. Only a
bounded derived-projection rebuild is allowed, outside the detector read UoW.

### Automatically set `group_token_quotas.enabled=false`

Not selected. There is no existing authorized state-transition function, audit
contract, or recovery path for detector-driven quota quarantine. Adding it under
a no-migration implementation would be an undeclared privilege and
cross-instance consistency bug.

### Return malformed `period_id` as 404 or coerce it

Rejected. Malformed syntax is an invalid request, while 404 deliberately covers
only a syntactically valid identifier that is unavailable within the Group
boundary. Coercion makes the public filter ambiguous.

### Add status 400 without a compatibility window

Rejected. The repository compatibility gate explicitly classifies a new response
status on an existing operation as incompatible. An undocumented or locally
waived response would bypass public-contract governance.

## Consequences

- An operator can distinguish a damaged authoritative ledger, a damaged Usage
  projection, and ordinary delivery lag without interpreting one ambiguous
  delta.
- Existing top-level reconciliation clients keep their field meanings; the
  projection diagnostic is additive and the only expected compatibility
  diagnostic is the exact new 400 response status.
- Checkpoint-aligned comparison remains correct during normal aggregation lag,
  replay, late usage, adjustment, and period rollover.
- GroupQuota remains the fact owner, Usage owns projection/reconciliation, and
  Operations owns delivery/replay.
- M3-E5 adds no schema, runtime-role grant, Redis key, event version, or automatic
  mutable quarantine state.
- Explicit Group disable can take longer than an automatic database mutation, but
  it preserves authorization, idempotency, version fencing, and audit. Immediate
  P0 emission and the runbook make that manual safety action explicit.
- Production paging destinations remain a separate open operational decision;
  repository evidence is limited to emitted telemetry and tested alert semantics.

## Migration, compatibility-window, and rollback impact

The chosen design requires no PostgreSQL migration. Existing quota, period,
reservation, attempt, adjustment, event, Outbox, projection, and watermark tables
and existing runtime SELECT/DML ownership are sufficient. Candidate PostgreSQL 18
query-plan tests must prove bounded scans use the existing indexes. If that proof
fails, an additive index migration needs its own candidate and database approval;
this ADR cannot be cited as implicit authorization.

No Redis contract or GroupQuota event-schema change is permitted. The release
manifest changes only for the exact OpenAPI SHA-256; PostgreSQL migration bounds
and GroupQuota event-contract digest remain unchanged under this decision.

The accepted compatibility window is effective only while its exact hashes, the
complete diagnostic, permanent approval evidence, and accepted statuses agree.
Before protected merge, rollback is to withdraw the candidate
OpenAPI/registry/generated artifacts and supersede this decision through normal
governance. After acceptance and protected merge, the window closes for every
other base/target pair and cannot authorize a later response change. A later
change to a top-level field meaning, checkpoint ordering, recovery authority, or
automatic quarantine needs a superseding ADR and normal contract governance.

## Security impact

- Reconciliation readers are least-privilege, read-only, owner-scoped ports; no
  cross-context repository or mutable entity escapes its module.
- Malformed and cross-Group period identifiers fail closed without disclosing
  whether another Group owns the period.
- Exact integer strings prevent precision loss from hiding large discrepancies or
  overflowing a metric conversion.
- Operational events contain bounded classifications and only the identifiers
  required for authorized response. Metrics, traces, logs, and error bodies do
  not contain secrets, raw prompts/responses, credentials, private host material,
  database connection data, or arbitrary exception/payload text.
- The detector cannot gain Admin identity, mutate a ledger, disable quota, replay
  Outbox, or rebuild a projection. Those remain explicit, fenced recovery actions.
- A projection rebuild is bounded to one Group/period and replaces only derived
  values from immutable facts; it cannot become a ledger-repair backdoor.

## Acceptance evidence required before `Accepted`

The exact candidate must provide all of the following:

- ADR, OpenAPI, error-catalog wording, compatibility registry, fixtures,
  deterministic generated contracts, release manifest, architecture/database/
  execution specifications, and reconciliation runbook updated atomically;
- exact base commit, base/target OpenAPI SHA-256 values, the complete compatibility
  diagnostic set, and permanent architecture/OpenAPI/database-no-migration
  approvals in Issue #44;
- contract cases for admin/operator/auditor success, User forbidden, malformed
  period 400, valid absent/cross-Group period 404, canonical decimal strings, and
  closed response/problem bodies;
- exact-integer unit tests for effective adjustments, pending sums, signed
  variances, leak candidates, overage, status precedence, absolute metric
  aggregation, bounded labels, and sustained-alert/recovery semantics;
- PostgreSQL 18 integration tests proving atomic authoritative and
  projection/checkpoint snapshots, current and closed periods, checkpoint-aligned
  comparison, and no false mismatch from newer source events;
- privileged fault injection for counter, fact, pending reservation, event,
  Outbox, checkpoint-covered Inbox receipt, projection, checkpoint, duplicate,
  cross-Group, and numeric-boundary discrepancies, with correct layer
  classification and no detector mutation;
- PostgreSQL 18 `EXPLAIN` evidence that normal scans and point reconciliation use
  existing indexes and do not scan unbounded published/event history;
- Worker advisory-lock ownership, crash takeover, globally bounded cross-round
  continuation, alert-sink failure, and Api/Worker host-boundary tests;
- bounded projection rebuild and delivery replay tests proving only derived or
  delivery state changes; same-session rollback tests for lease expiry,
  owner/version takeover, and lock-session termination; default-disabled one-shot
  entry tests; plus a runbook exercise proving explicit Group disable, evidence
  preservation, verification, and explicit re-enable; and
- the complete repository quality, architecture, contract, integration,
  end-to-end, security, and image-evidence gates for the exact candidate.

Acceptance does not authorize a remote database or Redis operation, deployment,
automatic Group/quota disable, production paging configuration, M3-E5 closeout,
M3 Exit, RC, GA, or production acceptance.

## Contract and test files coupled to the accepted candidate

- `docs/README.md`
- `docs/architecture/adr/README.md`
- `docs/architecture/adr/0013-freeze-quota-reconciliation-layers-checkpoint-alignment-alerting-and-recovery-boundary.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/error-catalog.md`
- `docs/contracts/compatibility-windows-v1.json`
- reconciliation success and malformed-period fixtures plus fixture inventory
- deterministic generated C# and TypeScript contracts
- `docs/database/README.md`
- `docs/开发执行规格-v1.0.md`
- `docs/release-manifest-v1.json`
- `ops/monitoring/quota-reconciliation-alert-rules-v1.json`,
  `ops/runbooks/quota-reconciliation.md`, and `ops/runbooks/README.md`
- Usage/GroupQuota/Operations Unit, Architecture, Contract, Integration,
  End-to-End, and Worker ownership tests
