# ADR 0017: Freeze the shared-POST stream admission discriminator

- Status: **Proposed**
- Date: 2026-09-02
- Decider: PoolAI architecture, Gateway, protocol-contract, and operational-safety owner (`@Lyon1984`); this proposal does not take effect without the exact approval described below
- Relates to: [M4-E2 Issue #25](https://github.com/Lyon1984/PoolAI/issues/25), [M4-E3 Issue #26](https://github.com/Lyon1984/PoolAI/issues/26), ADR 0015, D-029, AC-028, AC-043, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: **Pending an exact permanent approval by `@Lyon1984`**

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
3. exactly one admission-policy lease for each routed request that proceeds
   beyond admission, with no fallback to the other partition; and
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
  -> bounded admission-only stream discriminator and raw-body replay setup
  -> exactly one selected data admission bulkhead
  -> authentication
  -> authoritative media-type and body-size enforcement
  -> strict JSON and semantic request validation
  -> classification consistency guard
  -> Gateway Process Manager
```

The discriminator is part of admission routing, not authentication, public
request validation, normalization, or a Gateway attempt. It never calls a
module port, PostgreSQL, Redis, the Group RPM primitive, Routing, Supply, an
Adapter, or an upstream. It never writes a response or chooses an error code.

Only routed `POST /v1/responses` and `POST /v1/chat/completions` requests use
this discriminator. Static endpoint metadata continues to classify every other
route.

### Bounded classification algorithm

The discriminator examines the original request bytes with one forward-only,
incremental UTF-8 JSON token reader. It does not build a DOM, bind a DTO,
normalize a request, inspect `model`/`input`/`messages`, or retain decoded string
values. Work is linear in the bytes examined and stops after the first of:

- the first top-level property whose JSON-unescaped name is the exact,
  case-sensitive string `stream` and whose value token is `true` or `false`;
- a lexical failure that prevents safe continued scanning;
- end-of-body; or
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

The discriminator may stop at the first decisive boolean. The authoritative
parser still consumes the complete document and rejects duplicate property
names, malformed tails, trailing JSON, or any other invalid shape. Therefore a
later duplicate cannot change the execution mode: the already selected policy
remains held, but the request becomes invalid and never reaches execution.
Escaped spellings that JSON-decode to `stream` are the same property for both
the discriminator and strict parser.

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

- at most 64 KiB of examined bytes remain in memory;
- excess examined bytes spill to a runtime-private, non-shared temporary file;
- total pre-admission buffering is at most
  `Gateway:MaxRequestBodyBytes + 1`, whose validated maximum is 32 MiB + 1;
- scanning uses a fixed-size pooled block and a bounded-depth incremental token
  reader with maximum nesting depth 64, identical to the strict parser, never
  an allocation proportional to the number of JSON values; and
- the spill file is owner-only, delete-on-close, excluded from logs/backups,
  and disposed on rejection, exception, timeout, or cancellation.

If `Content-Length` already exceeds the configured body limit, the
discriminator does not read the body and records the oversized quarantine
result. For an unknown-length body, observing the sentinel byte records the
same result and stops reading. The discriminator itself does not emit 413; after
one NonStream quarantine permit and successful authentication, the ordinary
body-limit stage emits the existing `413 payload_too_large`. No application
code drains an unbounded rejected body merely to keep the connection reusable.

If a decisive boolean is found before end-of-body, the discriminator rewinds
immediately and acquires the selected policy. The ordinary bounded body reader
then enforces the complete limit while that permit is held. This keeps the
common explicit-`stream` case from being fully spooled before admission while
preserving arbitrary legal property order and omission semantics.

Network ingress before policy acquisition remains bounded by the Api server and
front-proxy connection, header, request-body data-rate, and request timeout
controls. This ADR does not claim that the NonStream/SSE application bulkheads
protect socket upload resources. Those transport controls must not be disabled,
and the discriminator always observes `RequestAborted` and the server request
deadline while reading.

### Validation, error precedence, and consistency

Classification is not a public validation result. Once the selected lease is
held, the existing order remains observable:

1. a saturated selected partition returns `429 gateway_overloaded` before
   authentication or body validation;
2. authentication failure takes precedence over media-type, size, syntax, and
   semantic body errors;
3. authenticated unsupported media, oversized body, malformed JSON, and valid
   JSON with invalid fields use the existing 415, 413, 400, and 422 contracts;
   and
4. no new public error code, status, header, OpenAPI field, database state, or
   Redis state is introduced by this decision.

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

- Client abort or server request-deadline cancellation during discrimination
  disposes the replay storage and acquires no policy lease.
- Cancellation while waiting for the selected policy cannot retry or fall back
  to the other policy. A lease won concurrently with cancellation is disposed
  exactly once.
- After acquisition, one `finally` owner releases that same lease on every
  authentication, validation, Process Manager, response, disconnect, and
  exception path.
- The selected lease remains held for the whole non-stream response or the
  complete SSE response/drain lifetime. It is not released after body parsing.
- Failover attempts remain inside the same inbound lease and never perform a
  second admission acquisition.

### Permit-bypass proof obligations

The implementation must make the following properties executable invariants:

1. The admission controller is invoked at most once for a shared-POST request.
   Every non-cancelled request that passes admission owns exactly one returned
   lease.
2. The selected kind is immutable. There is no alternate-policy probe,
   release-and-reacquire, transfer, shared overflow pool, or recursive endpoint
   dispatch.
3. Only the consistency guard can open the Process Manager boundary. It requires
   a valid strict parse and an exact match between effective mode and held lease.
4. Every invalid, quarantined, oversized, missing-feature, or mismatch path has
   zero canonical reads, Redis Group RPM calls, routes, Account leases,
   reservations, credential leases, dispatch fences, and upstream calls.
5. The admission lease count returns to its prior value after every completion,
   rejection, cancellation, and injected exception.

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
  bounded temporary storage before authentication. The fixed byte ceiling,
  server ingress controls, owner-only delete-on-close storage, cancellation, and
  no-DOM rule bound that cost.
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

## Verification required before acceptance can be claimed complete

- Unit tests cover absent/false/true `stream` at every property position,
  escaped and nested names, chunk boundaries, whitespace, large preceding
  values, duplicate and non-boolean values, malformed/truncated/trailing JSON,
  known and chunked oversize bodies, and both shared routes.
- Replay tests use a non-seekable chunked source and prove that downstream reads
  the exact original bytes; spill files are removed after success, rejection,
  cancellation, and injected exceptions.
- Pipeline tests prove overload-before-auth/validation precedence, auth-before-
  body-error precedence, zero business dependencies for quarantine/mismatch,
  and one acquire/one release on every terminal path.
- Isolation tests saturate SSE while valid NonStream traffic proceeds, and
  saturate NonStream while valid SSE traffic proceeds. They also prove there is
  no alternate-policy fallback.
- Cancellation tests cover discrimination, selected-policy wait, post-acquire
  validation, active non-stream response, active SSE, and bounded drain.
- Architecture tests forbid a second admission-controller call, mutable
  classification features, endpoint/body-parser bypass of the consistency
  guard, and request-body content in logs/metrics/traces.
- A bounded resource test places omitted `stream` at the maximum legal body
  size and proves the fixed memory, total spool, linear-work, and cleanup bounds.

This proposed ADR is not approval to implement M4-E2/M4-E3, not an OpenAPI or
database sign-off, not an authorization for remote systems, and not M4 or
release acceptance. `@Lyon1984` must approve the exact candidate before its
status or any dependent contract is backwritten as accepted.
