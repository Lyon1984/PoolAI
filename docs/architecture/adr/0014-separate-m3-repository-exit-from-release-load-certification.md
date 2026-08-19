# ADR 0014: Separate M3 repository exit from Release 1 load certification

- Status: **Proposed**
- Date: 2026-08-19
- Decider: PoolAI architecture, GroupQuota, quality, performance, and release owner (`@Lyon1984`) — pending explicit approval
- Relates to: M3 Exit, [M4-E1 Issue #24](https://github.com/Lyon1984/PoolAI/issues/24), [M6-E2 Issue #36](https://github.com/Lyon1984/PoolAI/issues/36), ADR 0002, ADR 0012, ADR 0013, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: **Pending explicit approval**

## Context

The M3 exit sentence in the execution specification currently requires the
same-Group hotspot to meet section 8.2. Section 8.2 is the Release 1 physical
capacity-certification contract. Its same-Group scenario requires 100 requests
per second for 15 minutes and includes the Gateway-added latency SLO; every
scenario must run independently three times on one build in the declared
reference environment, and the evidence must bind the three executable image
digests, hardware, configuration, data scale, scripts, raw metrics, and durable
archive.

That reference creates a circular stage dependency:

1. M4 may start only after M1, M2, and M3 have exited.
2. The Gateway Process Manager and public model-call path needed to measure
   Gateway-added latency do not exist until M4.
3. The certification plan assigns the complete section 8.2 campaign to M6-E2,
   after the M4 and M5 production paths exist, and the reference environment is
   intentionally not yet provisioned.
4. Local Compose and Testcontainers can prove repository correctness, but the
   release-evidence contract explicitly forbids treating them as the dedicated,
   production-equivalent section 8.2 environment.

None of these requirements may be silently ignored. Claiming a local database
stress test as physical certification would weaken the release gate; requiring
the M4 Gateway before allowing M4 to start would make the dependency graph
unexecutable.

M3 already has a separate correctness obligation that is valid before Gateway
exists: prove, against real PostgreSQL 18 and production GroupQuota ports, that
same-Group contention cannot overspend quota, create negative counters, or
duplicate a reservation transition or immutable settlement fact. That proof is
a repository exit gate, not a throughput, latency, hardware, or release-capacity
claim.

The real-PostgreSQL readiness exercise also exposed a separate correctness
defect: PostgreSQL wall clock may move backward between a committed reserve and
a later otherwise-valid dispatch. Lease and maximum-lifetime decisions must use
the wall clock observed after the existing locks are acquired, but persisting
that earlier value can violate the frozen reservation temporal constraint. The
forward database candidate
`0018_group_quota_monotonic_dispatch_timestamp_m3_exit.sql` corrects only that
defect by clamping the persisted dispatch timestamp to the reservation's already
committed temporal frontier. Its database governance is independent of this
ADR's milestone-evidence decision.

## Proposed decision

This decision has no effect while its status is `Proposed`. M3 Exit remains
blocked, and M4-E1 must not start, until `@Lyon1984` explicitly approves the
exact candidate through Issue #44, this ADR is changed to `Accepted` with the
permanent approval evidence, the resulting readiness change passes the protected
delivery path, and M3 Exit receives its own later approval.

If accepted, the M3 and M6 gates are separated as follows.

### M3 repository exit

M3 Exit is a repository correctness and recovery gate. Its same-Group hotspot
proof must:

1. use the manifest-locked PostgreSQL 18 runtime in an isolated real database;
2. enter through the production `IGroupQuotaLedger` Application port and its
   production PostgreSQL Unit of Work/repository adapters, never through direct
   test SQL calls to quota transition functions;
3. use a fixed, checked-in seed and bounded barrier-started concurrent schedules
   so repeated runs exercise the same estimates, commands, duplicate replays,
   and terminal transitions without relying on thread ordering or elapsed-time
   luck;
4. make aggregate estimates contend for more than the same Group period can
   admit, and verify every accepted and rejected result against the canonical
   public quota classification;
5. replay successful reserve, dispatch, release where still pre-dispatch, and
   settlement operations using their original identities, proving each logical
   transition changes counters and every transition-defined immutable fact,
   quota event, audit intent, and Outbox intent at most once;
6. read the final PostgreSQL authority and prove exact integer conservation,
   `consumed >= 0`, `reserved >= 0`, no committed admission exceeded the total
   available at its serialization point, no pending reservation leaked, no
   duplicate request/attempt/reservation/fact/event identity exists, and no
   counter or fact passed through a representation narrower than the frozen
   `numeric(78,0)`/`BigInteger` contract; and
7. run together with the existing deterministic concurrency, idempotency,
   dispatch-fence, renewal, sweeper, late-usage adjustment, transactional
   Outbox/Inbox, and three-layer reconciliation tests in the repository quality
   gate.

The proof must also include a compiled architecture/catalog test named
`OnlyGroupDefinesCumulativeTokenQuota`. It must show that User, API Key,
Subscription, and Account public contracts, production types, configuration,
and PostgreSQL-owned records contain no personal cumulative Token quota field;
GroupQuota remains the only cumulative quota owner. Account lease and Group RPM
remain capacity/security coordination and are not cumulative Token quotas.

This gate deliberately has no requests-per-second, duration, CPU, RSS, database
pool latency, network RTT, reference-hardware, image-digest, or release-archive
pass condition. CI or a developer machine may prove these repository
invariants, but that result must be described only as M3 repository exit
evidence. It is not section 8.2 certification, a capacity recommendation, a
Release Candidate result, or production acceptance.

M3 Exit still requires a separate permanent `M3 EXIT: APPROVED` comment in
Issue #44 after the exact readiness candidate is merged and its independent
`main` quality and security workflows pass. Accepting this ADR does not itself
approve M3 Exit or authorize M4.

### M6-E2 physical certification

Section 8.2 remains unchanged as the Release 1 physical capacity-certification
gate owned by M6-E2. In particular:

- the reference topology and hardware remain one 4-vCPU/8-GiB API instance,
  one 2-vCPU/4-GiB Worker instance, PostgreSQL at 4 vCPU/16 GiB with local SSD,
  Redis at 2 vCPU/4 GiB, and service RTT p95 no greater than 1 ms;
- the minimum dataset remains 100 Groups, 1,000 users, 5,000 API Keys,
  100 Accounts, and 10,000,000 attempt aggregation records;
- the same-Group hotspot remains exactly 100 RPS, non-streaming, for 15 minutes,
  with no overspend, negative counter, or duplicate settlement, platform error
  rate no greater than 0.1%, and the frozen Gateway-added latency SLO;
- every section 8.2 scenario, including the same-Group hotspot, still runs three
  independent times on the same candidate build and all three runs must pass;
- M4/M5-dependent mixed traffic, SSE, bulkhead, breaker, long-call, Usage,
  backlog, failover, and process-crash scenarios remain in the same M6-E2
  campaign; and
- reports still bind commit and Api/Worker/Migrator image digests, redacted
  configuration, actual hardware, data scale, scripts, raw metrics, per-run
  conclusions, SHA-256, and the authoritative GitHub Release asset.

No local Compose, Testcontainers, GitHub Actions runner, average-only summary,
or M3 Exit comment may replace or pre-approve any part of that campaign. This
ADR does not change `r1.1-certification-plan.json`, provision the reference
environment, populate the certification index, or change any M6 entry/exit
condition.

### Contract and ownership boundary

This ADR changes only milestone evidence ownership and sequencing. Considered
on its own, it does not change:

- public HTTP routes, fields, status codes, headers, Gateway error shapes, or
  SSE fixtures;
- a table, column, constraint, index, database function, runtime permission, or
  migration checksum;
- a Redis key, Lua ABI, TTL, lease, RPM, or breaker rule;
- GroupQuota, Usage, Operations, Gateway, Routing, or Supply ownership and
  dependency direction;
- the M3 quota arithmetic, reservation, dispatch, settlement, recovery, event,
  or reconciliation semantics; or
- any Release 1 SLI, SLO, reference hardware, physical load, archival, or
  production-acceptance threshold.

The same readiness delivery may carry migration 0018 as a separately governed
database correction. That candidate does not derive authority from this ADR:
it does not rewrite 0001/0002 or any prior signed migration, and it preserves
the function ABI, Quota → Period → Reservation lock order, provider fence,
lease/max-lifetime decision clock, event/Outbox exact replay, NOLOGIN owner, and
API-only `EXECUTE` boundary. Its exact SQL SHA-256 and manifest `18..18` require
a distinct permanent database approval in Issue #44. ADR approval does not
approve migration 0018, and migration approval does not accept this ADR or M3
Exit.

## Alternatives considered

### Require literal section 8.2 certification before M4

Rejected. Gateway-added latency and several required physical scenarios cannot
be measured before the M4/M5 paths exist. This makes the frozen dependency graph
circular rather than stricter.

### Treat local Compose or Testcontainers as section 8.2 certification

Rejected. Those environments do not prove the declared hardware isolation,
network RTT, data scale, image identity, three independent runs, or durable
release archive. Calling them certification would create false release evidence.

### Start M4 while leaving M3 Exit pending

Rejected. It bypasses the explicit M4 entry condition and makes later evidence
unable to distinguish a completed dependency from work performed out of order.

### Move or weaken the physical thresholds

Rejected. The 100 RPS by 15 minute hotspot, three-run rule, Gateway SLO, full
scenario matrix, environment, and archive remain valuable Release 1 gates and
stay unchanged in M6-E2.

## Consequences

- M3 can produce an honest, reproducible exit decision using only capabilities
  that exist at the end of M3.
- M4 remains blocked until both this clarification and the independent M3 Exit
  are approved and synchronized through protected `main`.
- Repository CI proves correctness under deterministic contention, but it must
  never be cited as capacity or release certification.
- M6-E2 continues to own the expensive, environment-specific and protocol-level
  performance campaign after the complete vertical slice exists.
- Traceability may promote DEC-009 only when the named ownership test exists;
  later M4/M5/M6 acceptance items remain partial or planned until their own
  evidence exists.

## Migration and rollback impact

The M3/M6 evidence split itself requires no database, Redis, deployment, data,
or credential migration. Before ADR approval, rollback of that split is deletion
of this Proposed ADR and its candidate contract wording. After acceptance, a
reversal requires another reviewed contract decision; it cannot silently
relabel M3 repository evidence as section 8.2 certification or remove an M6
threshold.

Migration 0018 is an independent forward-only database correction and remains
pending until its exact SQL and manifest have their own permanent database
approval. Before any authorized execution, it can be omitted as a candidate;
after execution, normal forward-only migration rules apply and no prior signed
SQL may be rewritten. Neither this ADR nor its protected merge authorizes
remote migration execution or data repair.

## Security impact

The separation preserves fail-closed quota semantics and prevents two unsafe
claims: starting M4 without an approved M3 correctness gate, and presenting a
developer environment as production-equivalent capacity evidence. Test and
report output must contain no credentials, connection strings, private host
material, request content, API Key secrets, or raw identifiers prohibited by
the observability contract. This ADR does not authorize remote PostgreSQL or
Redis mutation, migration execution, data repair, deployment, real upstream
credentials, KMS/key operations, RC, GA, or production acceptance.

## Coupled contract and test files

The candidate decision is coupled to:

- `docs/开发执行规格-v1.0.md`;
- `docs/release-evidence/README.md`;
- `docs/README.md` and `docs/architecture/adr/README.md`;
- `docs/traceability/release-1-traceability.json` when the named evidence is
  implemented; and
- the real-PostgreSQL hotspot and cumulative-quota ownership tests added by the
  M3 Exit readiness implementation.

The independently governed database correction is coupled to
`docs/database/0018_group_quota_monotonic_dispatch_timestamp_m3_exit.sql`,
`docs/database/README.md`, `docs/release-manifest-v1.json`, and the Migrator
embedded-resource list. These files are not approved by accepting this ADR.

OpenAPI, the error catalog, fixtures, Redis contracts/scripts, and project
memory are intentionally unchanged. Database SQL and the release manifest
change only for the separate 0018 database candidate, not as an effect of this
Proposed architecture decision.
