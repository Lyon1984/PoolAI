# Quota reconciliation incident response

Use this runbook when the M3-E5 detector reports an authoritative, projection, reservation-leak, overage, or delivery-health signal. It is an execution control, not authorization for a production mutation. Record the approved environment, Group, period, owner, rollback boundary, and evidence location before acting. Never paste credentials, raw prompts, raw upstream payloads, database dumps, or private host details into incident evidence.

The repository monitoring contract is [`../monitoring/quota-reconciliation-alert-rules-v1.json`](../monitoring/quota-reconciliation-alert-rules-v1.json). It evaluates at most every 30 seconds, opens the projection incident only after the aggregate `kind="projection"` mismatch gauge remains above zero for exactly 300 seconds, and requests a resolved notification when the expression returns to zero. It carries no Group or period metric label; use the authorized reconciliation API and retained bounded operational evidence to locate the affected Group. External routing and paging destinations require separate approval and are not configured by this file.

## 1. Preserve evidence and stop unsafe work

1. Record the exact application digest, PostgreSQL migration range, reconciliation `checked_at`/`data_watermark`, Group/period IDs, logical checkpoint/latest source sequences, bounded metric values, and relevant request/correlation IDs.
2. Do not run ad-hoc `UPDATE` or `DELETE` against quota counters, reservations, attempts, adjustments, events, Outbox, Inbox, projections, or watermarks. Do not replay an event until its immutable lineage and intended consumer are proven.
3. For an authoritative integrity signal, stop new model admission and quota-management writes. Use the existing Admin Group PATCH with an explicit reason, current strong ETag, and a fresh idempotency key to place only the affected Group in `disabled`; retain the command audit. The detector must never perform this transition itself.
4. For a projection/delivery-only signal with authoritative values intact, pause the affected consumer or rollout as required by the error budget; do not disable unrelated Groups.

## 2. Classify the divergence

- **Authoritative integrity:** compare period ledger consumed to effective attempt tokens (late adjustment replaces the base attempt), ledger reserved to pending estimates, and the selected period's adjacent event deltas plus latest event post-state. Any duplicate/cross-Group fact, numeric-range risk, or unexplained counter/event difference is P0.
- **Projection convergence:** pin the Usage Group checkpoint first. Compare the period projection only with GroupQuota `consumed_tokens_after` at or before that exact logical `source_event_sequence`. A newer ledger event means lag, not a projection mismatch. A nonzero aligned difference persisting five minutes is a projection incident.
- **Delivery health:** inspect the exact selected-period source-event identities in bounded keyset pages, together with quota-topic backlog, oldest age, dead lineage, replay state, Inbox proof, and checkpoint. Require exactly one original Outbox lineage per identity; an interleaved event for another period cannot substitute for a missing original, and replay is not a second original. For every identity at or below the accepted `usage-hourly-v1` checkpoint, require an exact receipt on the original or one replay with consumer `usage-hourly-v1`, topic `poolai.quota.v1`, matching physical Outbox `event_sequence`, and schema version `1`. Do not inspect or copy payloads to prove this diagnostic. A missing or conflicting checkpoint-covered receipt is delivery P0; a poison/dead lineage can block one Group without implying a ledger difference.
- **Reservation leak / overage:** separate overdue pending reservations from reserved-counter variance. `max(consumed-total,0)` is overage and a capacity signal unless an integrity check also fails.

## 3. Recover through the owning boundary

1. If immutable upstream evidence proves a usage correction, append the formal GroupQuota adjustment through its approved command path. Never rewrite the base attempt, event, or counter manually.
2. If the authoritative ledger or event chain is corrupt, keep the Group disabled and use an approved fix-forward migration/function or PITR/restore plan. Application rollback alone is not a data repair.
3. If delivery is blocked, validate the original immutable envelope and consumer state. Use the Admin replay command only for an eligible dead message; a replay retains the original logical source sequence and does not delete or mark the predecessor published.
4. If only Usage derived rows are wrong and the logical checkpoint is aligned, run the Usage-owned bounded period rebuild/replace path with one exact Group, period, and an ordered range of no more than 744 exact UTC-hour buckets. Acquire the dedicated `poolai:r1:worker:usage-rebuild:v1` session lock and the existing `usage-hourly-v1` checkpoint lease; do not create a missing watermark. Each bucket heartbeat and projection replacement/deletion runs in one short transaction borrowed from the same PostgreSQL session holding the advisory lock, so lease takeover or lock-session loss rolls back the whole bucket. The rebuild must not write GroupQuota facts or Outbox/Inbox, and it must not create, advance, or rewind the checkpoint. Treat ownership/lease loss, unhealthy authoritative facts, or a remaining aligned variance as an incomplete recovery.
5. If reservations are merely overdue, let the session-locked reservation sweeper perform the existing fenced expire path. Confirm whether dispatch occurred before interpreting the resulting zero/conservative usage fact.

### Controlled one-shot Usage rebuild

Use the normal Worker artifact only after the incident owner approves the exact Group, period, first hour, last hour, and evidence destination. Supply these process-scoped settings; do not persist them in a shared deployment manifest:

```text
WorkerJobs__UsageRebuild__Enabled=true
WorkerJobs__UsageRebuild__GroupId=<approved-group-uuid>
WorkerJobs__UsageRebuild__PeriodId=<approved-period-uuid>
WorkerJobs__UsageRebuild__FirstBucketStart=YYYY-MM-DDTHH:00:00Z
WorkerJobs__UsageRebuild__LastBucketStart=YYYY-MM-DDTHH:00:00Z
```

With `Enabled=true`, the Worker starts no normal background loops, attempts exactly one rebuild, stops, and returns exit code `0` only for `Completed`; busy, ownership/lease loss, invalid authoritative state, remaining variance, cancellation, and failure return non-zero. Retain the structured disposition and checkpoint sequence. Before the normal Worker is restarted, remove these four selectors and remove or set `WorkerJobs__UsageRebuild__Enabled=false`; verify from startup evidence that the one-shot service is absent and the normal loops are registered. Never leave one-shot mode enabled as a retry mechanism.

## 4. Verify and re-enable

1. Re-run reconciliation for the same exact Group/period and retain the before/after snapshots. Require authoritative consumed/reserved variances of `0`, a consistent event chain/latest state, and an aligned Usage projection variance of `0`.
2. Require quota-topic backlog/oldest age within SLO, no unresolved dead lineage for the Group, exact checkpoint-covered Inbox receipt metadata, checkpoint lag below 60 seconds, and no overdue pending reservation outside the sweeper window.
3. Re-run the relevant PostgreSQL 18 integration/fault tests and application smoke checks against the repaired candidate. Confirm no high-cardinality Group/period labels or sensitive payloads entered telemetry.
4. After the incident owner approves the evidence, first prove that one-shot mode is disabled, then use the existing Admin Group activation path with current ETag, reason, idempotency key, and activation readiness proof. Retain the disable and re-enable audits together. The detector and rebuild cannot automatically re-enable the Group.
5. Close with P0/P1 classification, cause, exact recovery mechanism, remaining risk, and follow-up. A zero projection difference alone is insufficient if authoritative or delivery checks remain unhealthy.
