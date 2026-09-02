# ADR 0017: Freeze the shared-POST stream admission discriminator

- Status: **Proposed**
- Date: 2026-09-02
- Decider: PoolAI architecture, Gateway, protocol-contract, and operational-safety owner (`@Lyon1984`); this proposal does not take effect without the exact approval described below
- Relates to: [M4-E2 Issue #25](https://github.com/Lyon1984/PoolAI/issues/25), [M4-E3 Issue #26](https://github.com/Lyon1984/PoolAI/issues/26), ADR 0015, D-029, AC-028, AC-043, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: **Pending an exact permanent approval by `@Lyon1984`**
- Required public-contract window: `m4-e2-e3-model-discriminator-overload` (**Pending independent exact OpenAPI/error-catalog approval**)

## Context

The public Responses and Chat Completions operations each use one shared POST
path for both response modes. Their request bodies make `stream` optional, with
omission meaning `false`, and JSON object member order is not significant. The
Api therefore cannot choose the `data-nonstream` or `data-stream` admission
partition from route metadata, method, headers, or a bounded prefix alone.

The existing architecture simultaneously requires:

1. correlation/observability, then admission, then authentication, then request
   body limit and semantic validation;
2. independent bounded NonStream and SSE permits, with SSE never consuming a
   NonStream permit and vice versa;
3. exactly one selected data-policy lease for each routed request that proceeds
   beyond data admission, with no fallback to the other partition; and
4. the `stream` value accepted by the protocol parser to agree with the
   partition held for the complete response lifetime.

These requirements conflict unless a narrowly scoped step may inspect the body
only to select the admission partition. In the worst case a valid request omits
`stream` or places it last, so any order-independent and contract-preserving
classifier must inspect through end-of-body. A prefix guess cannot distinguish
that request from an otherwise identical request whose final member is
`"stream":true`.

Reading an unbounded body, binding a request DTO, or running the normal validator
before admission would turn the classifier into a bulkhead bypass. Acquiring a
default permit and later swapping it would consume two policies or let one
partition depend on the capacity of the other. A bounded, replayable,
admission-only discriminator is therefore an architecture decision rather than
an endpoint implementation detail.

## Decision

This proposal becomes effective only after `@Lyon1984` approves the exact
candidate in a permanent Issue #44 comment and this ADR is backwritten to
`Accepted` with that evidence. Until then M4-E2/M4-E3 implementation must not
rely on this exception.

The two shared POST operations use this fixed outer sequence:

```text
correlation / observability
  -> fail-fast model-discriminator resource gate
  -> bounded admission-only stream discriminator and complete raw-body spool
  -> exactly one selected data admission bulkhead
  -> authentication
  -> authoritative media-type and body-size enforcement
  -> strict JSON and semantic request validation
  -> dispose replay storage and release model-discriminator gate
  -> classification consistency guard
  -> Gateway Process Manager
```

The discriminator is part of admission routing, not authentication, public
request validation, normalization, or a Gateway attempt. It never calls a
module port, PostgreSQL, Redis, the Group RPM primitive, Routing, Supply, an
Adapter, or an upstream. Its resource guard may write only the exact overload
response frozen below; body classification never chooses a business error.

Only routed `POST /v1/responses` and `POST /v1/chat/completions` requests use
this discriminator. Static endpoint metadata continues to classify every other
route.

### Fail-fast model-discriminator resource gate

Each Api process owns one separate `model_discriminator` guard with exactly
eight permits and a queue limit of zero. These values are fixed for R1.1 and
have no configuration override. This is a pre-admission CPU/memory/spool guard,
not a fifth business workload partition and not a NonStream or SSE policy
lease.

A shared-POST request makes one non-blocking acquisition attempt before opening
replay storage or reading a body byte. Saturation fails immediately. A
successful guard lease exclusively owns that request's lexical reader, memory
buffer, temporary file, and replay feature. It remains held until the
authoritative parser has consumed the complete spooled body and the replay
storage and file descriptor have been disposed. A selected data-policy lease
may overlap it during authentication and parsing, but the guard is always
released before the Process Manager starts and is never held for upstream or
response streaming.

One fixed 30-second monotonic wall-clock deadline begins when the guard is
acquired and remains active through classification, complete spooling, selected
data admission, authentication, authoritative parsing, replay-file close, and
guard release. It is linked to `RequestAborted` and any earlier server request
deadline; there is no retry, queue, deadline extension, or fallback. The
following resource failures happen before authentication and before any
NonStream/SSE acquisition:

- all eight guard permits are active;
- the private spill file cannot be opened or written, including permission,
  file-descriptor, quota, and `ENOSPC` failures.

If the client has not aborted, guard saturation or spill open/write failure
returns the independently governed Gateway-shaped `429 gateway_overloaded`
response with `Retry-After: 1`. It owns no selected data lease and performs no
authentication, validation, canonical read, Redis call, reservation, or
upstream I/O. If `RequestAborted` wins the race, the implementation disposes any
partial spool and guard lease and writes nothing.

Deadline expiry has the same independently governed 429 behavior whether it
occurs during body upload, selected data admission, database-backed
authentication, or authoritative parsing. These stages must observe the linked
deadline token and may not commit response headers while deadline-governed work
is still in flight. Authentication/validation terminal results and selected-
policy overload are projected only after replay storage and the guard are
released. On expiry, one atomic request-lifecycle transition from active to
timed-out fences every normal completion and makes the timeout owner responsible
for output and cleanup. It signals the linked cancellation token, disposes the
PoolAI-owned spool/file descriptor and releases the guard, then releases any
selected NonStream/SSE lease, and only then writes the 429 if the client remains
connected. A late authentication/parser completion cannot hold or reacquire
those local resources, write a response, or enter the Process Manager. If
headers have nevertheless started, that is an invariant breach: the connection
is aborted and a content-free operations diagnostic is emitted rather than
appending a false 429 body. Client abort always writes nothing.

The 30-second fence is a bound on the PoolAI request lifecycle and PoolAI-owned
resources, not a promise that an operating-system operation, Npgsql operation,
or third-party task is physically terminated at T+30. Database authentication
receives the linked token and uses Npgsql cancellation together with the
independent validated `Data:Postgres:CommandTimeoutSeconds` and connection-pool
bounds. The PoolAI JSON parser checks cancellation between every fixed-size
input block, so its maximum cancellation-observation latency is the time to
process one block and is deterministic in tests. Any underlying operation that
completes later is detached from the timed-out request state and cannot recover
local ownership or produce an externally visible effect.

Admission metrics add exactly one fifth internal kind and label,
`model_discriminator`, used only by this guard. The existing active gauge may
emit `bulkhead=model_discriminator`. The existing rejected counter may emit
only the three fixed outcomes `saturation`, `deadline`, and `storage_failure`
for that kind. Cancellation and normal release only decrement the active gauge;
they do not increment rejected, create an outcome, or add a new instrument.
Metrics contain no request byte, property value, client address, credential,
model, or prompt. The four existing workload kinds and labels retain their
meanings.

### Bounded classification algorithm

The discriminator examines the original request bytes with one forward-only,
incremental UTF-8 JSON token reader. It does not build a DOM, bind a DTO,
normalize a request, inspect `model`/`input`/`messages`, or retain decoded string
values. While syntax remains potentially executable, it spools the complete
body before selected data admission. Lexical decision work is linear in the
bytes examined and reaches one of:

- the first top-level property whose JSON-unescaped name is the exact,
  case-sensitive string `stream` and whose value token is `true` or `false`,
  followed by bounded raw-byte spooling through end-of-body;
- end-of-body with a complete JSON object and no `stream` member;
- a lexical failure that makes execution impossible, followed by bounded raw
  spooling through end-of-body; or
- observation of `Gateway:MaxRequestBodyBytes + 1` bytes.

Nested properties named `stream` do not participate. An exact top-level
`stream` whose value is null, a string, number, array, or object is
unclassifiable. An empty body, a non-object root, malformed/truncated JSON, an
unsupported content encoding, and a body known or observed to exceed the limit
are also unclassifiable. The discriminator records only this internal sealed
result:

| Observation | Selected policy | May reach the Process Manager |
|---|---|---|
| first exact top-level `stream: true` | SSE | only after the strict parser confirms one unique boolean `true` |
| first exact top-level `stream: false` | NonStream | only after the strict parser confirms one unique boolean `false` |
| complete valid JSON object with `stream` absent | NonStream | only after the strict parser confirms omission/default `false` |
| unclassifiable, malformed before a decisive boolean, non-boolean, or oversized | NonStream quarantine | never |

After finding the first decisive boolean or a lexical failure, the discriminator
may stop decoding tokens but must continue the bounded byte-for-byte spool to
end-of-body or the size sentinel before attempting selected data admission. The
authoritative parser later consumes the complete spool and rejects duplicate
property names, malformed tails, trailing JSON, or any other invalid shape.
Therefore a later duplicate cannot change the execution mode: the already
selected policy remains held, but the request becomes invalid and never reaches
execution. Escaped spellings that JSON-decode to `stream` are the same property
for both the discriminator and strict parser.

The NonStream quarantine is not a permissive default. It exists so malformed,
unsupported, and oversized requests still pass through exactly one admission
policy before authentication and the existing error projection. A quarantined
request is marked in a sealed request feature and is structurally forbidden
from entering the Process Manager.

### Exact replay and resource bounds

Before examining any body byte, the Api wraps the original non-seekable body in
a replay stream. Examined bytes are preserved byte-for-byte; JSON is never
re-serialized. Before the next middleware runs, the wrapper is rewound to byte
zero so the authoritative parser sees precisely the original body, including
whitespace and escape sequences, followed by any bytes not yet read from the
underlying transport.

The replay implementation has these fixed bounds:

- each active guard lease may retain at most 64 KiB in memory and may spool at
  most `Gateway:MaxRequestBodyBytes + 1` bytes to one runtime-private,
  non-shared temporary file;
- across the fixed eight permits, discriminator memory is therefore at most
  512 KiB, temporary spool bytes are at most
  `8 * (Gateway:MaxRequestBodyBytes + 1)`, and the validated 32 MiB maximum
  makes the latter no greater than 256 MiB + 8 bytes;
- scanning uses a fixed-size pooled block and a bounded-depth incremental token
  reader with maximum nesting depth 64, identical to the strict parser, never
  an allocation proportional to the number of JSON values; and
- the spill file is owner-only, delete-on-close, excluded from logs/backups,
  and its single file descriptor is disposed on admission rejection,
  authentication/validation completion, exception, deadline, or cancellation.

The guard stays held while its replay file exists, so no more than eight such
files or descriptors and no more than the aggregate bounds above can exist per
Api process. Opening a second spill file, detaching storage from its guard
lease, or releasing the guard before the file is closed is forbidden.

If `Content-Length` already exceeds the configured body limit, the
discriminator does not read the body and records the oversized quarantine
result. For an unknown-length body, observing the sentinel byte records the
same result and stops reading. The discriminator itself does not emit 413; after
one NonStream quarantine permit and successful authentication, the ordinary
body-limit stage performs the single payload-too-large projection. No
application code drains an unbounded rejected body merely to keep the
connection reusable.

For a body within the limit, the discriminator completes the raw spool under
the lifecycle deadline, rewinds it, and only then attempts the selected policy.
The ordinary bounded body reader and strict parser consume only that complete
spool while both leases are held; they cannot trigger a later temporary-file
open or write. After parsing, the spool is disposed and the discriminator guard
is released, while the selected NonStream/SSE lease remains held through the
complete response lifecycle. The 30-second timer is stopped only after spool
close and guard release have completed.

Network ingress before policy acquisition remains bounded by the Api server and
front-proxy connection, header, request-body data-rate, and request timeout
controls. This ADR does not claim that the NonStream/SSE application bulkheads
protect socket upload resources. Those transport controls must not be disabled,
and the discriminator's fixed eight-request concurrency and full-lifecycle
30-second deadline also bound PoolAI-owned resources during pre-admission and
parser work without claiming physical termination of underlying operations.

### Validation, error precedence, and consistency

Classification is not a public validation result. Once the selected lease is
held, the existing order remains observable. Guard saturation or spill
open/write failure occurs before that point and owns no selected data lease.
The full-lifecycle deadline can also expire after data admission during
authentication or parsing; its ordered cleanup and connected-client 429
behavior remain exactly as frozen above. After successful classification:

1. a saturated selected partition returns `429 gateway_overloaded` before
   authentication or body validation;
2. authentication failure takes precedence over media-type, size, syntax, and
   semantic body errors;
3. authenticated unsupported media, oversized body, malformed JSON, and valid
   JSON with invalid fields use the existing 415, 413, 400, and 422 contracts;
   and
4. the discriminator adds no new status, code, header, response schema,
   database state, or Redis state, but its new 429 causes require the independent
   OpenAPI/error-catalog compatibility-window approval defined below.

The strict parser publishes an immutable effective mode only after it has
enforced the full body limit, duplicate-name rejection, JSON syntax, and request
schema. Immediately before the Process Manager, a consistency guard requires:

```text
effective stream=true  <=> held policy=SSE and discriminator=declared-true
effective stream=false <=> held policy=NonStream and discriminator=declared-false-or-absent
```

Quarantine, a missing/replaced feature, a changed body, or any mismatch fails
closed before canonical reads, Group RPM, route selection, Account lease, quota
reservation, credential acquisition, dispatch, or upstream I/O. An internal
classifier/parser disagreement uses the existing internal-error path and emits
a bounded security/operations diagnostic without request bytes.

### Cancellation and lease lifetime

- Client abort during guard acquisition or discrimination disposes replay
  storage and the guard lease, acquires no selected data lease, and writes no
  response. Client abort during authentication or parsing uses the same
  no-output cleanup rule.
- The guard has zero queue, is acquired at most once, owns at most one replay
  file descriptor, and is released exactly once. It is never retried and never
  falls back to an unguarded classifier.
- Cancellation while waiting for the selected policy cannot retry or fall back
  to the other policy. A lease won concurrently with cancellation is disposed
  exactly once.
- The lifecycle deadline is enforced around cancellation-aware data-admission,
  database authentication, and parser operations. The atomic lifecycle fence,
  rather than physical task termination, prevents them from retaining local
  request resources or publishing a late result.
- On deadline, cleanup order is invariant: dispose replay storage/file
  descriptor and release the discriminator guard first, then release any
  selected data lease, then project the connected-client 429.
- After data-policy acquisition, one atomic cleanup owner releases that lease on
  every authentication, validation, Process Manager, response, disconnect, and
  exception path. Normal `finally` and timeout race through the lifecycle fence;
  only the winner disposes resources and the loser is a no-op.
- The guard remains held only through authentication and authoritative body
  parsing so its spool bounds remain real; parser completion disposes the spool
  and releases the guard before the Process Manager begins.
- The selected lease remains held for the whole non-stream response or the
  complete SSE response/drain lifetime. It is not released after body parsing.
- Failover attempts remain inside the same inbound lease and never perform a
  second admission acquisition.

### Permit-bypass proof obligations

The implementation must make the following properties executable invariants:

1. The separate fixed guard is attempted at most once, has no queue, and no body
   byte or replay file is created without its lease. Guard failure owns zero
   NonStream/SSE leases and cannot fall back to unguarded classification.
2. After successful classification, the data admission controller is invoked
   at most once. Every non-cancelled request that passes data admission owns
   exactly one selected NonStream or SSE lease.
3. The selected kind is immutable. There is no alternate-policy probe,
   release-and-reacquire, transfer, shared overflow pool, or recursive endpoint
   dispatch.
4. Only the consistency guard can open the Process Manager boundary. It requires
   a valid strict parse and an exact match between effective mode and held lease.
5. Every invalid, quarantined, oversized, missing-feature, or mismatch path has
   zero canonical reads, Redis Group RPM calls, routes, Account leases,
   reservations, credential leases, dispatch fences, and upstream calls.
6. Guard active count, memory, spool bytes, and file descriptors never exceed
   8, 512 KiB, 256 MiB + 8 bytes, and 8 respectively at the validated maximum.
   Both guard and data-policy counts return to their prior values after every
   completion, rejection, cancellation, and injected exception.
7. One monotonic deadline covers guard acquisition through parser completion,
   spool/file-descriptor disposal, and guard release. Expiry cannot leave a
   PoolAI-owned replay store, guard, or selected data lease owned by the request,
   and cleanup follows the frozen guard-before-data order. The lifecycle fence
   blocks late database/parser completion without asserting that its underlying
   task has physically terminated.

These invariants prove that a body cannot request SSE while consuming only a
NonStream permit, cannot request non-stream execution while consuming only an
SSE permit, and cannot use malformed or oversized input to reach either
execution path without its matching policy.

## Consequences

- The public `stream` semantics and arbitrary JSON object-member order remain
  unchanged; no ordering convention or new header is imposed on clients.
- Requests that omit `stream` may require bounded scanning through end-of-body
  before admission. This is unavoidable without changing the public protocol.
- The Api performs a lightweight lexical pass over examined bytes and may use
  bounded temporary storage before authentication. The fixed eight-permit/zero-
  queue guard, full-lifecycle 30-second deadline, byte/descriptor ceilings,
  owner-only delete-on-close storage, cancellation, and no-DOM rule bound that
  cost.
- The discriminator is intentionally a shared pre-admission choke point for the
  two model POST routes. Saturating it makes both modes fail fast with overload,
  which is measured and tested separately. After classification succeeds,
  AC-043 still requires full NonStream/SSE isolation: saturating one selected
  data partition cannot consume capacity in or reject the other.
- The strict parser remains the sole source of normalized request data and
  public validation errors. No parsed request or prompt content is retained in
  the admission feature, metrics, logs, traces, or audit metadata.
- This decision changes no database migration, Redis contract, release
  manifest, quota authority, RPM counting rule, or Adapter capability.

## Rejected alternatives

### Classify from route metadata, `Accept`, or another header

Rejected. Both response modes share each route, and the frozen request-body
boolean is authoritative. A header would create a second signal and mismatch
policy not present in the public contract.

### Inspect only a fixed prefix and default when `stream` is not found

Rejected. Object-member order is unrestricted. A late valid `stream:true` would
either execute under NonStream capacity or be rejected despite satisfying the
current public request schema.

### Put all model POSTs in the SSE partition

Rejected. It eliminates NonStream/SSE isolation and lets long streams consume
the capacity reserved for short non-stream calls.

### Acquire one partition and swap or reacquire after model binding

Rejected. It violates the one-policy-lease rule, introduces a race or deadlock,
and makes admission in one partition depend on capacity in the other.

### Deserialize or semantically validate before admission

Rejected. It duplicates the protocol parser, performs attacker-controlled
allocation/work outside the selected bulkhead, and risks classifier/parser
drift. The discriminator is restricted to bounded lexical mode discovery and
exact byte replay.

### Spool without a separate fixed guard

Rejected. A per-request byte limit alone does not bound aggregate unauthenticated
memory, temporary storage, or file descriptors. The fixed eight-permit,
zero-queue, 30-second guard is required; storage failure cannot fall back to
memory, another directory, direct streaming, or either selected data policy.

## Migration and rollback impact

This decision adds no PostgreSQL migration, table, column, permission, Redis
key/script/version, release-manifest entry, durable state, or public data
conversion. It is an Api process-local admission and body-replay change only.

A rollout must drain affected Api instances, start the accepted implementation,
and verify the discriminator and four existing admission metric kinds before
restoring model traffic. Mixed instances preserve the same HTTP schema, but
operational evidence must distinguish instances that do and do not implement
the guard. Rollback requires draining the affected instance and returning to a
contract-compatible prior Api build; there is no data rollback. A rollback must
not route shared model POSTs through the current static SSE metadata shortcut or
remove NonStream/SSE isolation. Partial replay files and guard/data leases are
process-local and are discarded on process exit; they are never recovery facts.

## Security impact

The guard bounds unauthenticated classifier work per Api process to eight active
requests, 512 KiB memory, eight spill descriptors, and at most 256 MiB + 8 bytes
of temporary spool at the maximum configured body size. Its zero queue and
full-lifecycle 30-second deadline bounds how long the request may own PoolAI
spool, guard, and selected data resources during slow-body, authentication, and
parser stages. It does not bound physical Npgsql/OS/third-party task lifetime;
those operations remain constrained by cancellation plus their independent
timeouts and pools. Saturation, deadline, file open/write, descriptor
exhaustion, quota failure, and `ENOSPC` fail closed; no failure may switch to an
unguarded, in-memory-only, NonStream, or SSE fallback. Deadline cleanup fences
any late database/parser result and frees the guard before the selected data
lease.

Replay files contain potentially sensitive prompts and tool data. They must be
runtime-private, owner-only, delete-on-close, never logged, traced, audited,
backed up, or exposed through diagnostics, and closed on every success/failure/
cancellation race. Metrics expose only the internal `model_discriminator` kind
and bounded outcome, never body content or identities. The shared guard is an
intentional availability tradeoff and may be targeted to overload both model
modes; fail-fast behavior, alerting, and separate saturation tests make that
risk explicit while selected-policy isolation remains unchanged afterward.

## Coupled contract and test files

Acceptance and implementation must update these coupled architecture/runtime
descriptions atomically with the code:

- `docs/architecture/design-pattern-baseline.md` for the exact pre-admission
  exception, fixed guard, one selected policy, and AC-043 boundary;
- `docs/开发执行规格-v1.0.md` and `docs/系统重构方案-v1.0.md` for the shared-POST
  stage order, fixed resource limits, metrics kind, and failure behavior; and
- `docs/architecture/adr/README.md` plus the normal project-memory navigation
  only after exact approval and verified implementation state.

This ADR does not claim that the current public contract already covers
discriminator deadline or storage failure. Before implementation, one separate
exact compatibility window named
`m4-e2-e3-model-discriminator-overload` must update and bind:

- `docs/contracts/openapi-v1.yaml`, extending only the existing 429 description
  on both shared POST operations to enumerate guard saturation, full-lifecycle
  deadline, and replay-storage failure;
- `docs/contracts/error-catalog.md`, extending only `gateway_overloaded` to
  freeze those three causes and `Retry-After: 1`; and
- `docs/contracts/compatibility-windows-v1.json`, binding the exact base commit,
  base/target OpenAPI and error-catalog digests, and the complete compatibility
  diagnostic set.

That window introduces no new status, code, header, or response schema and does
not alter the single ordinary payload-too-large behavior. Nevertheless its
semantic expansion requires an independent permanent `@Lyon1984` approval in
Issue #44. ADR approval and public-contract-window approval do not substitute
for one another; both must exist before implementation. A fixture change,
database sign-off, and Redis sign-off remain unnecessary unless the exact
candidate introduces a further coupled change. Any implementation that needs a
new public status/code/header or changes `stream` semantics must stop and open
new governance rather than widening this window.

The minimum coupled test locations are:

- `tests/PoolAI.UnitTests/GatewayModelDiscriminatorTests.cs` for lexical,
  deadline, storage, aggregate-bound, and cleanup behavior;
- `tests/PoolAI.EndToEndTests/GatewayAdmissionPipelineTests.cs` for exact error
  precedence, one selected lease, no fallback, and zero business work;
- `tests/PoolAI.LoadTests/AdmissionBulkheadLoadTests.cs` for separate guard
  saturation plus post-classification AC-043 NonStream/SSE isolation; and
- `tests/PoolAI.ArchitectureTests/GatewayBoundaryTests.cs` for immutable
  classification, single selected-policy acquisition, and Process Manager guard.

## Verification required before acceptance can be claimed complete

- Unit tests cover absent/false/true `stream` at every property position,
  escaped and nested names, chunk boundaries, whitespace, large preceding
  values, duplicate and non-boolean values, malformed/truncated/trailing JSON,
  known and chunked oversize bodies, and both shared routes.
- Replay tests use a non-seekable chunked source and prove that downstream reads
  the exact original bytes; spill files are removed after success, rejection,
  cancellation, deadline, open/write/`ENOSPC`, selected-policy rejection, and
  injected exceptions, with no leaked file descriptor.
- Pipeline tests prove overload-before-auth/validation precedence, auth-before-
  body-error precedence, zero business dependencies for quarantine/mismatch,
  one guard attempt, one selected-policy attempt, and exact release on every
  terminal path. Guard failure must prove zero selected-policy acquisition.
- Isolation tests saturate SSE while valid NonStream traffic proceeds, and
  saturate NonStream while valid SSE traffic proceeds. They also prove there is
  no alternate-policy fallback. A separate test saturates all eight
  `model_discriminator` permits, proves immediate zero-queue Gateway 429 with
  `Retry-After: 1` for both routes, then proves capacity is restored.
- One combined slow-upload test holds all eight guards with eight incomplete
  request bodies, proves a ninth request immediately receives the Gateway 429,
  advances the monotonic clock to 30 seconds, then proves every partial spool,
  file descriptor, guard lease, and any selected data lease is released before
  both guard and data capacity are reusable.
- Cancellation tests cover discrimination, selected-policy wait, post-acquire
  authentication/parsing, active non-stream response, active SSE, and bounded
  drain. Deadline injection separately covers data admission, blocked database
  authentication, and parser stages plus the spool/guard-before-data cleanup
  order.
- A deliberately cancellation-delayed fake authentication/parser operation
  remains incomplete beyond T+30 while the test proves the atomic late-result
  fence, immediate release of PoolAI-owned spool/file descriptor/guard/data
  leases, no response or Process Manager entry from its later completion, and
  restored capacity. The test must not assert that the fake task itself died at
  the deadline.
- Architecture tests forbid a second admission-controller call, mutable
  classification features, endpoint/body-parser bypass of the consistency
  guard, and request-body content in logs/metrics/traces.
- A bounded resource test places omitted `stream` at the maximum legal body
  size and proves the 8/512 KiB/8/(256 MiB + 8 bytes), 30-second, linear-work,
  and cleanup bounds.

This proposed ADR is not approval to implement M4-E2/M4-E3, not an OpenAPI or
database sign-off, not an authorization for remote systems, and not M4 or
release acceptance. `@Lyon1984` must approve the exact candidate before its
status is backwritten as accepted, and must independently approve the exact
OpenAPI/error-catalog compatibility window before implementation begins.
