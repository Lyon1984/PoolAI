# ADR 0018: Freeze the usage-request terminalization boundary

- Status: **Proposed**
- Date: 2026-09-02
- Decider: PoolAI architecture, GroupQuota, Gateway, database, Worker, and usage-fact owner (`@Lyon1984`); this proposal does not take effect without the exact approvals described below
- Relates to: [M4-E1 Issue #24](https://github.com/Lyon1984/PoolAI/issues/24), [M4-E5 Issue #28](https://github.com/Lyon1984/PoolAI/issues/28), ADR 0012, ADR 0015, D-004, D-027, D-028, AC-015, AC-039, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue architecture approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5510884292)
- Independent database approval: **Pending for the exact forward migration 0021 candidate**

## Context

`usage_requests` is the GroupQuota-owned request aggregate. The first quota
reservation inserts its request row as `accepted` in the same short transaction,
and every dispatched settlement advances `attempt_count`; a settlement may
either leave the request non-terminal for failover or finish it with a
`final_attempt_id`. A pre-dispatch release correctly returns reserved Tokens and
creates no `usage_attempts` fact, but the current release entry point never
changes the request row.

That behavior exposes two live lifecycle gaps:

1. A single attempt can reserve successfully and then fail or be cancelled
   before the dispatch fence. `ReleaseAsync` commits the zero-consumption release
   while `usage_requests.status` remains `accepted` indefinitely.
2. M4-E5 can settle a dispatched, safely retryable attempt with a null request
   outcome, correctly leaving the request `in_progress`. If the next canonical
   read, Supply read, route, Account lease, deadline, or retry-budget gate fails
   before another reservation is created, no GroupQuota operation can finish
   the request without inventing a settlement fact.

There is also a crash gap around an intentional failover continuation. After a
pre-dispatch release or non-terminal settlement, the process can stop before it
creates the next reservation. The database has no durable request lease, yet
every reservation already has a persisted absolute `max_expires_at`. The latest
reservation's maximum lifetime is sufficient to bound continuation without a
new column or table, provided every new attempt and release decision uses it and
Worker closes abandoned request-only continuations after it expires.

Changing release to always finish the request is invalid because a pre-dispatch
release may intentionally continue as another same-Group attempt. Settling zero
usage before dispatch is also invalid: it would fabricate an upstream attempt,
contradict the dispatch fence, and make Usage count work that never occurred.
The correction therefore needs explicit release disposition, versioned reserve
and expiry functions that enforce the durable continuation frontier, a narrow
request-only terminalizer, and bounded Worker recovery. All writes remain owned
by GroupQuota and preserve the accepted `poolai.quota.v1` Published Language.

## Decision

This proposal becomes effective only after `@Lyon1984` approves the exact ADR
candidate in a permanent Issue #44 comment, the independently reviewed forward
migration 0021 receives its own exact database approval, and this ADR is
backwritten to `Accepted` with both evidence links. Until then no implementation
or migration may rely on this boundary.

### Minimal GroupQuota application boundary

`GroupQuota.Abstractions` adds one immutable terminalization value, one command,
and one result:

```text
UsageRequestTerminalization(
    UsageRequestOutcome Outcome,
    string ErrorCode,
    int ExpectedAttemptCount,
    EntityId? ExpectedFinalAttemptId)

FinalizeUsageRequestCommand(
    EntityId RequestId,
    EntityId GroupId,
    int ExpectedNextAttemptIndex,
    UsageRequestTerminalization Terminalization)

UsageRequestTerminalizationResult(
    EntityId RequestId,
    UsageRequestOutcome Outcome,
    int AttemptCount,
    EntityId? FinalAttemptId,
    DateTimeOffset CompletedAt)
```

`ReleaseReservationCommand` gains a nullable
`UsageRequestTerminalization`. Null means **continue the same inbound request**;
non-null means **release this pre-dispatch reservation and finish the request in
the same GroupQuota Unit of Work**. `IGroupQuotaLedger.ReleaseAsync` remains the
only public release operation.

`IGroupQuotaLedger` adds exactly one request-only method:

```text
ValueTask<Result<UsageRequestTerminalizationResult>> FinalizeRequestAsync(
    FinalizeUsageRequestCommand command,
    CancellationToken cancellationToken)
```

It is used only after the preceding reservation is terminal and the next attempt
fails before a reservation is created. Therefore
`ExpectedNextAttemptIndex` is in `1..5` and denotes both the number of existing
reservations and the absent next index. It changes no reservation or quota
counter. Existing `ReserveAsync` keeps its public command/result shape while its
PostgreSQL adapter moves to the v2 function described below.

The GroupQuota Worker graph adds an internal, non-public continuation-expiry
candidate selector and processor. The selector exposes only request ID, Group
ID, latest reservation ID, expected next reservation index, expected immutable-
fact count/latest fact ID, and expected continuation expiry. It exposes no
repository, `IQueryable`, DbContext, or SQL executor to Gateway, Routing, Supply,
Usage, or endpoints.

Terminalization accepts only `failed` or `cancelled`. `succeeded` remains legal
only in `SettleAsync`, where a successful dispatched attempt fact and request
terminal state commit atomically. Live Gateway errors must match the bounded
low-cardinality internal code shape `^[a-z][a-z0-9_]{0,127}$`; Worker uses only
the fixed codes named in this ADR. These values are request-ledger evidence, not
permission to expose a new public error code. They never contain exception text,
upstream bodies, URLs, credentials, or request content.

The v2 release reason retains the existing non-blank internal-event meaning but
is capped at 256 UTF-8 bytes and rejects control characters. It is selected from
Gateway's bounded disposition vocabulary rather than copied from an exception.

### Forward migration 0021 and exact function ABI

Forward migration
`0021_group_quota_usage_request_terminalization_m4_e5.sql` adds these five exact
function identities:

```text
poolai_quota_reserve_v2(
    uuid, uuid, uuid, integer, uuid, uuid, uuid, uuid,
    uuid, uuid, numeric, boolean, text, uuid, uuid, text)

poolai_quota_release_v2(
    uuid, uuid, uuid, uuid, text, text,
    text, text, integer, uuid)

poolai_quota_expire_v2(
    uuid, uuid, uuid, uuid, text, text)

poolai_quota_finalize_request(
    uuid, uuid, integer, integer, uuid, text, text)

poolai_quota_expire_request(
    uuid, uuid, uuid, integer, integer, uuid, timestamptz)
```

`poolai_quota_reserve_v2` has the exact same 16 PostgreSQL parameter types and
argument order as `poolai_quota_reserve`: reservation ID, attempt ID, request ID,
attempt index, User ID, API Key ID, Subscription ID, Group ID, Account ID,
Channel ID, estimated Tokens, streaming flag, lease owner, quota-event ID,
Outbox ID, and idempotency key. The distinct function name prevents an old
schema-20 binary from silently receiving new lifecycle semantics. Its database
contract rejects every attempt index outside `0..4`; runtime configuration may
choose fewer attempts but cannot raise this hard five-attempt ceiling.

The `poolai_quota_release_v2` arguments, in order, are Group ID, attempt ID,
quota-event ID, Outbox ID, idempotency key, release reason, nullable request
terminal status, nullable request error code, nullable expected immutable-fact
count, and nullable expected final fact-bearing attempt ID. All four terminal
fields are null for continuation. For terminal release, status is `failed` or
`cancelled`, error/count are non-null, and expected final attempt is null iff the
expected fact count is zero.

`poolai_quota_expire_v2` has the exact same six types and order as
`poolai_quota_expire`: Group ID, attempt ID, quota-event ID, Outbox ID,
idempotency key, and reason. Its pre-dispatch and post-dispatch behavior is
defined below.

The `poolai_quota_finalize_request` arguments, in order, are Group ID, request
ID, expected next reservation index, expected immutable-fact count, nullable
expected latest fact-bearing attempt ID, terminal status, and terminal error
code. It returns request ID, terminal status, `attempt_count`,
`final_attempt_id`, and the first committed `completed_at`.

The Worker-only `poolai_quota_expire_request` ABI is exactly Group ID, request
ID, expected latest reservation ID, expected next reservation index, expected
immutable-fact count, nullable expected latest fact-bearing attempt ID, and
expected continuation expiry. It accepts no caller-selected status, error code,
completion timestamp, event ID, Outbox ID, or reason.

Migration 0021 adds no table, column, trigger, or data backfill. It adds only the
five versioned functions, their exact grants/revokes and audits, and the covering
index defined below. It does not rewrite any signed migration. The release
manifest advances to exact PostgreSQL compatibility `21..21`; SQL bytes,
checksum, PostgreSQL 18 evidence, role audit, and manifest digest remain under
the independent database-signing gate.

This ADR explicitly amends ADR 0006's **Family A exact function-identity
registry** by adding `poolai_quota_reserve_v2` as the schema-21 canonical-
admission member. The schema-20 `poolai_quota_reserve` identity remains a signed
historical member but loses runtime execute permission. The v2 member has exactly
the predecessor's cross-context tables, columns, read-only direction, consumer,
and lock direction; it adds no cross-context field or write. The other four new
functions read and write only GroupQuota-owned state and the already registered
event/Outbox append path, so they do not join a cross-context family. Adding the
reserve-v2 identity is nevertheless a real registry change and must be reflected
atomically in the coupled architecture documents, policy checks, and tests named
below. This is an explicit one-member function-identity expansion of Family A.

### Durable continuation frontier and v2 reserve

For an active request with at least one reservation, the latest reservation by
greatest `attempt_index` supplies the durable continuation expiry:

```text
continuation_expires_at = latest_reservation.max_expires_at
```

It is not a second quota lease, Redis key, configurable timeout, or extension of
an active upstream attempt. It is the already persisted absolute boundary within
which the next reservation may be committed. Every newly committed reservation
becomes the next latest reservation and supplies the following continuation
frontier. Process-local total deadline, maximum-attempt and retry-budget checks
may stop earlier; none may extend this database boundary.

After basic null/shape validation, including the database-enforced attempt-index
range `0..4`, `poolai_quota_reserve_v2` acquires the Group quota-root lock and
performs the **existing-attempt exact-replay branch first**. It locates the
original reservation from `attempt_id`, locks its original period/reservation,
and verifies every immutable reservation, event, Outbox, and idempotency binding
from persisted facts. An exact binding returns the original result without
locking the request, current canonical rows, current Supply rows, or current
period and without applying any current-state or continuation-deadline gate. A
changed binding fails with a stable conflict. Quota-root existence and Group
identity still have to match, but mutable quota `enabled` state cannot reject the
historical replay.

Only when no reservation exists for that attempt ID does the **new-attempt
branch** proceed. In the ADR 0006 Family A order, it acquires every potentially
blocking lock first: UsageRequest and the complete reservation/fact frontier;
immutable User/Role/API Key/Subscription/Group identity rows; current Supply
Configuration, binding/Account, and configured Channel; and finally the selected
quota Period. These lock reads deliberately omit mutable status and time filters.
After the last such lock has returned, the function samples one fresh PostgreSQL
`clock_timestamp()` and, against the locked rows and that same time, re-evaluates
all of the following before any quota mutation:

1. `p_attempt_index` is in `0..4`, the owning `usage_requests` row is `accepted`
   or `in_progress`, and all immutable request identity fields exactly match;
2. reservation count equals `p_attempt_index` and its indices are exactly the
   contiguous set `0..p_attempt_index-1`; there is no pending reservation, and a
   non-empty frontier's greatest reservation is terminal (`settled`, `released`,
   or `expired`) at index `p_attempt_index-1`;
3. request `attempt_count` equals the actual immutable `usage_attempts` count,
   `final_attempt_id` is null, and every fact/reservation identity is consistent;
4. quota enabled/current-period state and every registered canonical Identity,
   SubscriptionAccess, Group, current Supply configuration, Account lifecycle,
   health/cooldown, provider, and configured-Channel predicate still pass;
5. for `p_attempt_index > 0`, the shared post-lock database time is strictly
   earlier than the greatest reservation's persisted `max_expires_at`; and
6. quota arithmetic, estimate, and lease inputs pass the existing frozen rules.

The function must not sample the admission/continuation clock before waiting for
any Request/frontier, canonical, Supply, or Period lock, and it must not reuse a
selector, application, or transaction-start timestamp. It then inserts exactly
one new reservation/event/Outbox tuple. Any failed check rolls back the complete
transaction.

Attempt index zero retains the existing atomic creation rule: the repository
inserts `usage_requests` and invokes `poolai_quota_reserve_v2` in the same short
Unit of Work. Reserve failure rolls back the request insert, reservation, quota
delta, event, and Outbox together. A non-initial attempt never inserts or
upserts another request row. Request terminalization that commits later blocks
only a new attempt; within the v2 `0..4` domain it never makes a previously
committed exact reserve replay unclaimable.

### Schema-20 over-limit safety disposition

The five-attempt ceiling cannot be retroactively assumed for arbitrary schema-20
history. Before creating any schema-21 object or changing any privilege,
migration 0021 performs a read-only preflight over active requests only. If any
`accepted` or `in_progress` request owns more than five reservation rows, the
migration raises the fixed failure
`schema20_active_request_attempt_limit_exceeded` and the entire migration
transaction aborts. Applying 0021 then requires a separately reviewed and
explicitly authorized repair; 0021 neither chooses nor performs that repair.

Terminal schema-20 requests with more than five reservations do not block the
preflight and remain immutable historical evidence. They receive no backfill or
rewrite. Because reserve-v2 rejects an input index outside `0..4`, an attempted
v2 replay of such an out-of-domain legacy reservation returns the fixed internal
`attempt_index_out_of_range` conflict and performs no write. An active request
with exactly five supported, contiguous reservations can never create index 5;
Gateway terminalizes it when the retry budget is exhausted, or the request-only
sweep closes an abandoned continuation at its durable frontier.

### Exact lifecycle transitions

#### Pure pre-dispatch terminal failure

For a pending pre-dispatch reservation whose request is known to be finished,
Gateway calls `ReleaseAsync` with a terminal disposition. One database
transaction:

1. locks Quota, Period, Reservation, then UsageRequest;
2. verifies the target is the request's greatest reservation index and no other
   reservation is pending;
3. verifies the expected immutable-fact count/latest fact ID;
4. subtracts the estimate from `reserved_tokens` and marks the reservation
   `released`;
5. changes the active request to `failed` or `cancelled`, stores the bounded
   error code, sets `final_attempt_id` to the verified latest fact or null, and
   sets `completed_at` from the same post-lock PostgreSQL clock; and
6. appends exactly one existing `released` GroupQuota event and Outbox envelope.

For a first and only pre-dispatch attempt this leaves
`attempt_count=0/final_attempt_id=NULL`. It creates no `usage_attempts`,
adjustment, settlement audit, or Usage projection fact. Every injected failure
rolls back the counter, reservation, request, event, and Outbox together.

#### Pre-dispatch failover continuation

Gateway calls `ReleaseAsync` with all terminal fields null only after the live
phase/capability/deadline/maximum-attempt/retry-budget policy authorizes another
attempt. Under the quota-root lock, `poolai_quota_release_v2` samples database
time and requires:

```text
clock_timestamp() < reservation.max_expires_at
```

It then releases the reservation and reserved counter, emits one existing
`released` event, and leaves the request `accepted` or `in_progress` with
`final_attempt_id=NULL`. The next attempt keeps the request ID, uses the next
contiguous reservation index, repeats canonical/Supply/route/lease/reserve, and
does not recount inbound Group RPM. If the database time has reached the
maximum, continuation release fails atomically; Gateway must use terminal
release instead.

#### Failure after a non-terminal settlement

A safely retryable dispatched attempt first calls `SettleAsync` with null
`RequestOutcome`. That transaction creates exactly one immutable attempt fact,
settles the reservation, increments request `attempt_count`, and leaves the
request `in_progress/final_attempt_id=NULL`.

If the next-attempt gate fails before a reservation is created, Gateway calls
`FinalizeRequestAsync` with the intended next reservation index and exact fact
frontier. On both an initial active-to-terminal transition and a terminal-state
exact replay, `poolai_quota_finalize_request` requires
`ExpectedNextAttemptIndex` in `1..5`, reservation count exactly equal to that
value, and reservation indices exactly `0..ExpectedNextAttemptIndex-1`; because
the frontier is non-empty, its greatest index must be exactly
`ExpectedNextAttemptIndex-1`. It also proves every reservation is terminal, the
immutable-fact count/latest fact match, and no reservation exists at the absent
next index or beyond. A terminal row whose outcome/error appears to match is not
an exact replay unless this complete reservation/fact frontier still matches.
The first transition writes only request status, error, verified final fact ID,
and database completion time; an exact replay returns that first committed
result. Neither path changes a counter, reservation, fact, adjustment, quota
event, Outbox, Inbox, audit, or Usage projection.

If prior dispatched facts exist and a later pre-dispatch reservation becomes the
terminal failure, terminal release similarly retains the latest fact-bearing
attempt rather than pointing at the released reservation.

#### Reservation expiry

The existing reservation sweeper calls `poolai_quota_expire_v2`.

- A pre-dispatch expiry atomically returns reserved Tokens, marks the reservation
  `expired`, publishes exactly one existing non-conservative `expired` event,
  and finishes the active request as `failed` with fixed internal code
  `reservation_lease_expired_before_dispatch`. It preserves `attempt_count` and
  sets `final_attempt_id` to the verified latest fact-bearing attempt, or null
  when there is none. It creates no attempt or adjustment fact.
- A post-dispatch expiry is unchanged: it conservatively consumes the persisted
  estimate, creates the existing immutable failed attempt fact, advances
  `attempt_count`, makes that fact the final attempt, and publishes the existing
  conservative `expired` event. Late reliable usage still uses adjustment.

Both branches use one quota-rooted transaction and database completion time.
Expiry cannot leave an active request after its reservation becomes terminal.
The post-dispatch ledger, counter, fact, adjustment, and consumer semantics are
unchanged; the only v2 addition on that branch is the optional replay-proof
metadata marker described below.

### `attempt_count`, `final_attempt_id`, and reservation index

The existing columns keep these exact meanings:

- `attempt_count` counts immutable dispatched `usage_attempts` facts. Release,
  pre-dispatch expiry, live request-only finalization, and request-only Worker
  expiry never increment it.
- An active request always has `final_attempt_id=NULL`, including after one or
  more non-terminal settlements.
- A terminal request has `final_attempt_id=NULL` iff `attempt_count=0`.
  Otherwise it references the fact-bearing attempt with the greatest
  `attempt_index`. A pre-dispatch released/expired reservation cannot be the
  final fact because it intentionally has no attempt fact.
- `ExpectedAttemptCount` must equal both the locked request value and the actual
  immutable-fact count. The expected latest fact must be null iff that count is
  zero; otherwise it must identify the greatest fact-bearing attempt index.
- The next reservation index counts all reservations, including pre-dispatch
  release/expiry. It equals the size of a contiguous `0..N-1` reservation
  history and is independent of `attempt_count`.

No terminalization function repairs a corrupted count, gap, status, or final
pointer. Any mismatch is an invariant failure and produces no write.

### Request-only continuation sweep

The existing 30-second single-owner reservation sweep also performs a bounded
request-only continuation pass. It drives from the existing partial index:

```text
ix_usage_requests_in_progress(received_at, request_id)
WHERE status IN ('accepted', 'in_progress')
```

Each page contains at most the existing fixed 100 rows and uses the exclusive
keyset `(received_at, request_id)`, never `OFFSET`. The page CTE advances its
cursor over rows inspected, not only rows returned as expiry candidates, so a
front page of live requests cannot starve later expired rows. A completed pass
wraps to the beginning on the next 30-second round; ownership loss stops without
a compensating write.

For each page, a lateral latest-reservation lookup and bounded fact summary
produce a candidate only when the request is active, has a non-empty contiguous
reservation history, has no pending reservation, and the latest
`max_expires_at` is at or before the selector's database scan clock. The bound is
the frozen `Gateway:MaxAttempts` range of `1..5`: each reservation and fact
frontier lookup reads at most six index entries, where the sixth is an overflow
sentinel that rejects the candidate as an invariant violation instead of
silently truncating it. Migration 0021 adds the purpose-specific covering index:

```sql
CREATE INDEX ix_group_token_reservations_request_attempt_desc
    ON public.group_token_reservations(request_id, attempt_index DESC)
    INCLUDE (id, group_id, status, max_expires_at);
```

This index is not a new authority. The read-only selector neither locks nor
updates a request and its candidate may become stale immediately.

For each candidate, Worker calls the exact
`poolai_quota_expire_request(group_id, request_id,
expected_latest_reservation_id, expected_next_attempt_index,
expected_attempt_count, expected_latest_fact_id,
expected_continuation_expiry)` function. It locks
`Quota -> UsageRequest`, re-reads the reservation/fact frontiers in ascending
attempt order, confirms no pending reservation, requires the expected latest
reservation ID/index/count/fact/expiry to match, and samples a fresh database
time after the locks. Only when that time is at or after the exact persisted
continuation expiry does it set:

```text
status = failed
error_code = request_continuation_expired
final_attempt_id = verified latest fact-bearing attempt or NULL
completed_at = post-lock PostgreSQL clock
```

`request_continuation_expired` is a fixed low-cardinality internal ledger code,
not a new public Gateway error. The function emits no event or Outbox message and
changes no quota counter, reservation, attempt, adjustment, audit, or projection.
An exact terminal-state replay returns the first committed result; a stale or
different candidate loses the CAS without repair.

This pass closes schema-21 crash continuations and schema-20 active histories
whose reservation frontier is the supported contiguous `0..4` domain after the
latest persisted maximum expires. It does not repair or claim convergence for
an over-limit or otherwise malformed active history: more than five active
reservations prevent migration 0021 from applying, while any other frontier
corruption fails closed for separately authorized handling. The pass does not
infer a result before the durable boundary and performs no migration-time data
rewrite.

### Lock order and concurrency

After basic argument validation, all five functions take the exclusive
`group_token_quotas(group_id)` root used by the existing quota state machine and
perform no external I/O while holding it.

- Reserve v2 checks exact existing-attempt replay first. Only its new-attempt
  branch locks UsageRequest/frontier, canonical, Supply, and Period rows, then
  samples one fresh database clock and rechecks every mutable predicate plus the
  continuation frontier before writing.
- Release/expire v2 retain `Quota -> Period -> Reservation`, then lock the owning
  UsageRequest before request disposition.
- Live finalize and Worker request-only expiry use `Quota -> UsageRequest`, then
  read reservation/fact frontiers in ascending attempt index. They need no
  Period lock because they do not mutate counters or reservations.

The common exclusive quota root serializes every ordering variant for one Group,
so it does not introduce a Request/Period inversion with existing operations.
Cross-context rows are never read by the two request-only functions.

The decisive races are fixed:

- If terminalization commits first, a later **new** reserve observes a terminal
  request and fails `admission_request_not_active`; an exact replay of an already
  committed attempt in the v2 `0..4` domain still returns its original result.
- If a new reserve commits first, live/Worker request-only terminalization sees
  a changed frontier or pending reservation and loses without changing the
  request.
- If reserve and request-only expiry meet at the continuation boundary, the
  quota-root winner decides: reserve succeeds only with post-lock DB time
  strictly before expiry; expiry succeeds only at or after it.
- Terminal and continuation release cannot both commit for one pending
  reservation. Conflicting terminal outcomes cannot both commit.
- Pre-dispatch reservation expiry, terminal release, and new reserve cannot
  produce both a terminal request and a later reservation.

No compare-and-set miss becomes best-effort success, and transport cancellation
does not cancel a mandatory bounded terminalization transaction.

### Replay, IDs, and legacy convergence

Reserve v2 preserves the existing deterministic `reserve` event/Outbox/
idempotency identities and exact replay contract. The new name changes admission
semantics for new attempts, not the identity of already committed reservations.

Release v2 continues to publish event type `released`. The application derives
event ID, Outbox ID, and idempotency key deterministically from attempt ID with
distinct domains for `release:continue:v2` and `release:terminal:v2`. Its event
metadata includes an optional internal `terminalization_contract=usage_request_v2`
marker and exact `request_disposition=continue|failed|cancelled`; terminal
metadata also binds request error, expected fact count, and expected final fact
ID. Continuation metadata binds the persisted `continuation_expires_at`.
Exact replay validates event ID, Outbox payload/event link, idempotency key,
reason, marker, disposition, all supplied terminal fields, and continuation
expiry before returning the original result. It never applies a second counter
delta or request completion.

Expire v2 preserves the deterministic `expire` identities and existing event
type. Its optional metadata marker binds `usage_request_v2` and the exact
pre-dispatch/post-dispatch disposition so exact replay cannot be confused with a
legacy event.

A reservation already `released` or `expired` by the legacy function has no v2
metadata proof. Neither v2 function may treat it as an exact v2 replay or append
a second release/expiry event. It returns the fixed internal conflict
`legacy_terminal_not_v2_replay`, mapped through the existing public error
boundary rather than added to the public catalog. If its request remains active,
the request-only sweep closes it only after the latest persisted
`max_expires_at`, with no replacement quota event.

Live and Worker request-only terminalization deliberately emit no quota mutation
and therefore invent no event/Outbox IDs. Their replay binding is the existing
server-generated request ID plus exact reservation/fact frontier, outcome and
error for live finalization, or expected continuation expiry and the fixed
Worker result. Exact terminal state returns its first committed `completed_at`;
a different binding conflicts.

Client `Idempotency-Key` and model-response replay are not introduced. A
terminalized request prevents only a later new attempt; exact reserve, dispatch,
settle, release-v2, and expire-v2 replays that carry their original complete
identities continue to converge before current-state checks. The reserve-v2
statement applies to its hard `0..4` domain; an out-of-domain schema-20 reserve
receives the stable no-write conflict defined above.

### Published Language and projection boundary

`docs/contracts/group-quota-events-v1.json` remains byte-for-byte unchanged.
The optional v2 disposition metadata is permitted by its existing
forward-compatible metadata rule; it adds no event type, required field, or new
consumer behavior.

- Release v2 emits exactly one existing `released` event, whose Usage behavior
  remains `none_assert_no_attempt_fact`.
- Expire v2 emits exactly one existing `expired` event. Pre-dispatch expiry still
  has `conservative_expiry=false` and no attempt fact; post-dispatch behavior is
  unchanged.
- Live and Worker request-only terminalization emit no event because they change
  no quota, reservation, immutable attempt fact, or Usage projection input.
- A prior `settled` event remains the only publication that rebuilds that
  dispatched attempt's completion hour.

This is not permission to hide a usage fact in request metadata or overload
`settled`, `expired`, `renewed`, or `released` as a generic request-status event.
A future request-only consumer requirement needs a new Published Language
decision rather than an unregistered v1 event.

### Database permissions

All five new functions are owned by `poolai_runtime_owner NOLOGIN`, are
`SECURITY DEFINER`, and fix
`search_path = pg_catalog, public, pg_temp`. `PUBLIC` receives no execute
permission.

The schema-21 runtime allowlist is exact:

- `poolai_api` receives execute on `poolai_quota_reserve_v2`,
  `poolai_quota_release_v2`, and `poolai_quota_finalize_request`;
- `poolai_api` loses execute on legacy `poolai_quota_reserve` and
  `poolai_quota_release`;
- `poolai_worker` receives execute on `poolai_quota_expire_v2` and
  `poolai_quota_expire_request`;
- `poolai_worker` loses execute on legacy `poolai_quota_expire`; and
- API cannot execute either Worker function, Worker cannot execute any new API
  function, and neither runtime role can execute an unlisted helper.

Every `GRANT`, `REVOKE`, and ACL assertion names the complete PostgreSQL argument
type list shown above; an unqualified name or an executable overload fails the
schema-21 audit.

Migration 0021 grants no table-level or column-level request, reservation,
attempt, event, Outbox, audit, or quota-counter DML to API/Worker. The functions
read/write only GroupQuota-owned rows plus the existing release/expiry
event-Outbox path. ADR 0006 still has exactly three cross-context families and
its table/field/direction allowlists do not expand, but its Family A exact
function-identity membership **does** expand by the one
`poolai_quota_reserve_v2` identity defined here. Schema-21 architecture and SQL
policy checks must recognize that exact member and reject every other new
cross-context function identity.

## Alternatives considered

### Always terminalize on release

Rejected. Safe pre-dispatch failover requires a continuation disposition and a
later same-request reservation.

### Keep release non-terminal and update the request from Gateway

Rejected. Gateway does not own the table, and two commits leave a crash window.
Direct API DML would bypass GroupQuota locking, replay, and permissions.

### Settle a synthetic zero-usage attempt

Rejected. A pre-dispatch path has no committed dispatch fence or upstream call.
The synthetic fact would falsify `usage_attempts`, `attempt_count`, Usage metrics,
and event semantics.

### Use process-local failover deadline only

Rejected. It cannot recover a stopped process and leaves no authoritative point
at which Worker may close an abandoned active request. The already persisted
latest `max_expires_at` supplies the narrow durable frontier without new state.

### Emit a new `request_terminalized` event in v1

Rejected. Request-only terminalization has no quota or usage-fact delta. A new
event type would break the strict v1 contract and is unnecessary.

### Backfill or immediately close legacy active requests in migration 0021

Rejected. A pre-existing active row without a pending reservation can be a live
continuation. Migration cannot reconstruct its process phase. For a supported
contiguous history of at most five reservations, Worker waits for the already
persisted latest maximum and then revalidates under the quota root. Over-limit
active history instead blocks migration and requires separate repair authority.

### Add a request lease column/table or generic command ledger

Rejected for this correction. The latest reservation already provides the
needed durable frontier, and exact expected frontiers make the narrow functions
idempotent without another state machine or general execution surface.

## Consequences

- A live Gateway can terminalize every known terminal path without fabricating
  dispatch or usage.
- Once the schema-20 over-limit preflight passes, a stopped continuation with a
  supported frontier has a durable upper bound and is eventually closed by the
  owner-fenced Worker rather than remaining permanently active.
- Failover keeps one request ID while each upstream call remains a separately
  reserved and settled immutable attempt.
- `attempt_count` and Usage projection arithmetic retain their existing meaning.
- Existing exact attempt replays in the v2 `0..4` domain converge even after
  request completion, while a new attempt cannot cross terminal status or the
  continuation maximum.
- The covering index adds bounded storage/write overhead to reservations in
  exchange for deterministic latest-frontier lookup and a bounded active-request
  sweep.
- Request-only terminalization adds no Published Language or projection traffic.

## Migration and rollback impact

Migration 0021 is forward-only and may be applied only by `PoolAI.Migrator`. It
requires the exact approved schema-20 history. As its first operation, before
DDL or privilege changes, it checks for an `accepted`/`in_progress` request with
more than five reservations. Finding one raises
`schema20_active_request_attempt_limit_exceeded`; the transaction creates no
object, changes no ACL, and requires a separately approved repair before retry.
Terminal over-limit history is deliberately outside this preflight. Once it
passes, 0021 creates the five versioned functions and one covering index, applies
the exact grants/revokes, runs function owner/search-path/ACL/catalog/index
audits, and records its checksum in the manifest.

It performs no request-row backfill or repair. After the schema-21 Worker starts,
the request-only sweep may close a supported active legacy release/expiry
continuation only when the latest persisted maximum has expired and the locked
frontier still matches. It does not claim to close an over-limit history. This is
ordinary governed runtime recovery, not migration-time data rewriting.

There is no old/new binary coexistence window. Rollout drains and fences all
schema-20 Api/Worker instances, applies 0021, starts only schema-21-compatible
hosts, verifies readiness, and then restores traffic. Before migration starts,
rollback may restart the unchanged schema-20 release. After migration starts,
rollback is forward-only through a schema-21-compatible repair/current build;
regranting a legacy entry point is not a rollback plan.

This proposal and future approvals do not authorize remote migration, data
repair, Redis mutation, credential/key/KMS activity, deployment, M4-E5 closeout,
M4 Exit, RC, GA, or production acceptance.

## Security impact

The boundary removes durable state confusion in which completed or abandoned
requests appear active. Quota-root serialization, exact frontiers, a strict
`<`/`>=` continuation boundary, and revalidation protect against stale Gateway
and Worker writers. Versioned event metadata prevents a legacy terminal
reservation from masquerading as a v2 replay or causing a second event.

Only low-cardinality codes and non-secret UUID/timestamp frontiers cross the new
ports. No prompt, message, model output, upstream body, raw exception, URL, IP,
credential, envelope, Authorization value, or private host material enters the
request row, event, Outbox, logs, traces, metrics, or governance evidence.
Dynamic SQL is forbidden.

The functions fail closed on Group/request mismatch, terminal request with a
different binding, non-contiguous reservation history, pending/later
reservation, count/latest-fact mismatch, expired continuation, dispatched
release, wrong event/Outbox/reason/disposition identity, legacy terminal event,
or runtime-role drift. They do not relax dispatch, same-Group, quota arithmetic,
provider identity, or cross-context permissions.

## Coupled contract and test files

Acceptance and implementation require one governed change covering:

- `docs/architecture/adr/0018-freeze-usage-request-terminalization-boundary.md`
- `docs/architecture/adr/README.md`
- `docs/README.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/开发执行规格-v1.0.md`
- `docs/系统重构方案-v1.0.md`
- `docs/database/README.md`
- `docs/database/0021_group_quota_usage_request_terminalization_m4_e5.sql`
- `docs/release-manifest-v1.json`
- `eng/policies/validate-traceability.mjs`
- `src/Modules/PoolAI.Modules.GroupQuota.Abstractions/`
- `src/Modules/PoolAI.Modules.GroupQuota/Application/`
- `src/Modules/PoolAI.Modules.GroupQuota/Infrastructure/Persistence/`
- `src/Modules/PoolAI.Modules.GroupQuota/Worker/`
- `src/Modules/PoolAI.Modules.Gateway/`
- `tests/PoolAI.UnitTests/`
- `tests/PoolAI.ArchitectureTests/`
- `tests/PoolAI.IntegrationTests/`
- `tests/PoolAI.EndToEndTests/`

`docs/contracts/group-quota-events-v1.json`, OpenAPI, the public error catalog
and fixtures, and the Redis contract are explicit no-change assertions. A diff
in any of them requires separate governance rather than being folded into the
ADR or migration approval.

## Verification required before acceptance can be claimed complete

- Unit tests cover terminal/continuation release validation, fixed Worker codes,
  deterministic v2 operation identities and metadata, strict continuation
  boundary, exact replay, mismatch failures, and live post-settlement
  finalization.
- Reserve-v2 PostgreSQL tests prove attempt-zero insert/reserve all-commit or
  all-rollback; the hard `attempt_index` range `0..4`; contiguous next index,
  prior terminal/no-pending, and exact fact frontier; and existing exact replay
  before Request/current-canonical/current-Supply/current-Period locks or any
  active/deadline gate. Lock-delay tests hold each potentially blocking row past
  a state or time boundary and prove one fresh clock is sampled only after the
  last lock, followed by rechecks of every current predicate and
  `DB now < latest max_expires_at`.
- Pure pre-dispatch terminal-release tests leave zero reserved Tokens, one
  released reservation/event/Outbox row, terminal request with preserved
  count/final fact, and no new attempt/adjustment/audit fact at every kill point.
- Continuation tests prove release requires unexpired maximum, leaves the request
  active, and permits exactly the next same-request reservation without another
  request insert or inbound RPM count.
- Multi-attempt tests settle retryable attempts non-terminally, fail every later
  pre-reservation gate, and prove live finalization preserves count/latest fact,
  completion time, counters, facts and event/Outbox totals. Its initial transition
  and terminal exact replay both require reservation count exactly equal to
  `ExpectedNextAttemptIndex`, the exact contiguous indices `0..expected-1`, and
  greatest index `expected-1`; shorter, gapped, later, or changed frontiers fail.
- Expire-v2 tests prove pre-dispatch expiry atomically terminalizes with
  `reservation_lease_expired_before_dispatch` and no attempt fact, while
  post-dispatch expiry remains byte/ledger-equivalent to the existing
  conservative fact and adjustment path.
- Request-only sweep tests use the active partial index and exclusive
  `(received_at,request_id)` keyset in fixed pages, advance past non-candidates,
  read no more than five supported frontier rows plus one overflow sentinel,
  reject overflow, close only supported schema-21 and legacy active
  continuations at latest maximum, make no convergence claim for malformed
  history, and add no event/Outbox/counter/fact/audit/projection mutation.
- Exact replay tests vary every reserve/release/expire/finalize/expire-request
  ID, reason, metadata disposition, outcome, error, count, latest reservation,
  next index, latest fact and expiry. Legacy released/expired events must return
  stable conflict and must never gain a second event; an out-of-domain schema-20
  reserve replay returns `attempt_index_out_of_range` without a write.
- Two-connection tests race new reserve against live finalization and Worker
  expiry on both sides of the exact time boundary; race terminal/continuation
  release, pre-dispatch expiry, and conflicting outcomes; and prove deterministic
  deadlock-free states. Terminal-first blocks only new attempts, while all exact
  historical replays in the v2 `0..4` domain still converge.
- Crash tests stop after each release, non-terminal settlement, candidate read,
  request-only terminalization, event append, and before/after commit. Recovery
  must produce one terminal request, no pending reservation, no duplicate fact
  or event, and exact counter conservation.
- Corruption tests prove request/Group, fact-count/latest-fact, reservation gap,
  pending/later reservation, continuation-expiry, dispatch-state, and event
  metadata mismatches fail closed without repair.
- Real-role tests prove API can execute only reserve-v2/release-v2/live-finalize,
  Worker can execute only expire-v2/request-expire, legacy reserve/release/expire
  execution is revoked from the respective role, cross-role execution fails,
  helpers remain inaccessible, and direct DML remains denied.
- Architecture tests keep request writes inside GroupQuota, forbid Gateway SQL
  or persistence dependencies, preserve ADR 0006's exact three families, add
  exactly `poolai_quota_reserve_v2` to Family A's function identities without
  changing its table/field/read/lock-direction allowlist, reject every unknown
  identity, and prove one short UoW with no Redis/HTTP/stream/backoff wait.
- Migration tests cover empty schema and exact 20-to-21 upgrade, signed-history
  immutability, covering-index shape/use, function ABI/owner/search path, exact
  ACL, manifest/checksum validation, no backfill, and forward-only recovery.
  Preflight fixtures prove active reservation count greater than five aborts the
  whole migration before DDL/ACL changes and demands separate repair authority,
  while terminal over-limit history does not block and is not rewritten.
- Contract tests assert GroupQuota Event v1, OpenAPI/public-error/fixtures, and
  Redis sources/hashes are unchanged and accept the already permitted optional
  v2 event metadata without new consumer behavior.

This Proposed ADR is not architecture approval, database approval, permission to
implement against an unsigned schema, or milestone/release evidence.
