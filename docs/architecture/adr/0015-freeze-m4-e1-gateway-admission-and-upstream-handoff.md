# ADR 0015: Freeze M4-E1 Gateway admission and upstream handoff

- Status: **Accepted**
- Date: 2026-09-02
- Decider: PoolAI architecture, public-contract, GroupQuota, Gateway, Supply, Routing, and database owner (`@Lyon1984`); this candidate does not take effect without the approval evidence below
- Relates to: [M4-E1 Issue #24](https://github.com/Lyon1984/PoolAI/issues/24), ADR 0006, ADR 0007, ADR 0009, ADR 0010, ADR 0011, and sign-off control Issue #44
- Compatibility window ID: `m4-e1-group-rpm-policy`
- Base Git commit: `08860ac639fa5eb8627e1264c02e138162691c98`
- Base OpenAPI SHA-256: `9ab3765ac644a665373e34d716ffb53a9ac6fdc7abdd28408d9f398fb9a362bf`
- Target OpenAPI SHA-256: `9969ff4d8eb9558bf1d315d00f1ee2a648dc5e4f374c3c16276e69cd1c6a5aa9`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5506358261)
- Architecture approval evidence: [Issue #44 permanent ADR approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5506352845), binding candidate `b44ba9764133ec56d0399c4728c0303f59c2eea9`, tree `1806c5797def51acebf849ed1aca2959c40415c2`, and pre-evidence-backwrite ADR SHA-256 `2144090ec54d85968c0148d5c06fc9ccbef7635bd19a208c399c285bd9f45ac1`
- Independent database approval evidence: [Issue #44 permanent migration 0019 approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5506361132), binding candidate `b44ba9764133ec56d0399c4728c0303f59c2eea9`, tree `1806c5797def51acebf849ed1aca2959c40415c2`, SQL SHA-256 `7bfa4412c899037ac6b0531ade60fa75f6a2afa721fef1ce7511a245f9e63f43`, release manifest SHA-256 `76c75a1f415650b5f39308010cfd4641df1efaac37044f92cbf2f1d97bd7c8d8`, PostgreSQL major `18`, and compatibility `19..19`
- Allowed diagnostic: `#/components/schemas/Group/required: property requests_per_minute became required`
- Allowed diagnostic: `#/components/schemas/GroupUpdateRequest/allOf/0: existing schema alternative was removed or tightened`
- Allowed diagnostic: `#/components/schemas/GroupUpdateRequest/anyOf: shared-component anyOf alternatives changed`

## Context

M4-E1 is the first milestone that composes the already shipped Identity,
SubscriptionAccess, GroupQuota, Supply, and Routing capabilities into one
protocol-neutral Gateway attempt. The repository already freezes the outer
bulkhead order, canonical PostgreSQL admission, Redis Group RPM primitive,
Account lease, quota reservation and dispatch fence, lossless settlement facts,
Account credential envelope, and connection-time upstream SSRF controls. It does
not yet freeze enough information to implement that composition without choosing
new behavior in source code.

The unresolved boundaries are coupled:

1. Group RPM has a Redis ABI but no canonical per-Group limit source. A global
   setting, a Supply field, a Subscription field, or a Redis-derived default
   would either lose per-Group policy or create a second authority.
2. API Key `allowed_cidrs` cannot be enforced safely behind a reverse proxy until
   the socket trust anchor, trusted proxy configuration, and exact forwarded-IP
   parsing algorithm are fixed.
3. Crash settlement already requires a persisted estimate, but no deterministic
   R1 estimator defines how a normalized request becomes input and output Token
   estimates.
4. The current Gateway abstractions can narrow upstream usage to `long` and do not
   carry all provider usage components, even though the accepted database and
   public contracts require lossless `numeric(78,0)`/`BigInteger` facts.
5. The Supply candidate contains route facts, while the Adapter attempt context
   contains only IDs. The credential is an internal Supply lease. Without a
   frozen handoff, an implementation would either leak secrets into DTOs, make an
   Adapter depend on Supply implementation, or bypass ADR 0010/0011 SSRF fences.

These are architecture and contract choices, not endpoint conveniences. They
must be fixed before implementation, while keeping the OpenAPI compatibility
window, forward database candidate, Redis contract, and runtime operations under
their independent approval controls.

## Decision

This proposal freezes the following M4-E1 boundary. It becomes effective only
after `@Lyon1984` explicitly approves the exact candidate in Issue #44 and this
ADR is changed to `Accepted` with permanent evidence. Until then it is not an
architecture sign-off, an OpenAPI-window approval, a database approval, an M4-E1
closeout, or release evidence.

### Per-Group RPM authority and lifecycle

`GroupQuota` remains the sole owner of Group lifecycle and runtime admission
policy. The public Group create, update, and response contracts expose
`requests_per_minute` with these exact semantics:

- the inclusive range is `1..1,000,000`;
- create omission means the fixed R1 default `6000`;
- every Group response returns the effective integer value;
- update omission leaves the value unchanged;
- any PATCH body that contains `requests_per_minute` must also contain a valid
  non-blank `reason`, whether or not `status` is present; a PATCH containing
  either `status` or `requests_per_minute` therefore triggers the same reason
  requirement; and
- a successful change remains one GroupQuota command with `If-Match`,
  `Idempotency-Key`, one short PostgreSQL Unit of Work, one Group version change,
  and the existing audit/idempotency/outbox discipline. Replaying the same
  binding returns the original result; a different binding conflicts.

The application idempotency ABI remains upgrade-safe for commands still inside
the retention window: a create whose effective value is the default `6000`, an
update with no `requests_per_minute` member, and an activation metadata patch
with no RPM member keep their schema-18 request-hash shape byte-for-byte.
Only a command that binds a non-default create RPM or explicitly patches RPM
uses the extended hash shape. Old successful response bodies that omit the new
field replay with the effective default `6000`; malformed or out-of-range replay
state still fails closed.

The persisted `groups.runtime_policy` object has exactly these two keys and no
others:

```json
{"schema_version":1,"requests_per_minute":6000}
```

`schema_version` must equal JSON number `1`; `requests_per_minute` must be an
integral JSON number in the range above. Unknown, missing, duplicate, null,
string, fractional, or extra-key representations fail closed. The canonical
limit is read from the current PostgreSQL Group snapshot. Configuration,
Subscription, Supply, Account, Redis state, an activation token, or an in-memory
fallback must never replace it. Group RPM is a capacity and abuse-control limit,
not a cumulative Token quota, purchasable entitlement, pricing attribute, or
personal quota.

The forward database candidate preserves the existing function identities and
adds explicit v2 functions for the schema-19 application:

```text
poolai_group_create_v2(uuid,text,text,integer,uuid,numeric,uuid,uuid,uuid,text,text)
poolai_group_update_v2(uuid,bigint,boolean,text,boolean,text,boolean,integer,text,text,text,timestamptz)
```

The argument order and PostgreSQL types above are part of the ABI. The old
`poolai_group_create` and `poolai_group_update` functions remain present and keep
their schema-18 call behavior for an explicitly built bridge or corrective
runtime; this does not make the actual schema-18 binary schema-19 compatible.
The schema-19 application calls only the v2 functions after the forward migration
is present. There is no same-name overload or silent reinterpretation of old
calls. Migration 0019 preflight accepts only
the exact pre-upgrade value `{}`. Any non-empty value—including an object that
already resembles the future canonical two-key shape—is not governed legacy
state and makes the complete migration fail atomically before any backfill or DDL.
Only after that preflight may every exact `{}` be backfilled to the two-key object
and the constraint be validated. Each backfilled existing Group advances its
Group version exactly once and observes a new PostgreSQL `updated_at`, so stale
ETags fail rather than hiding the representation change; the migration does not
fabricate application idempotency, audit, or outbox commands for the mechanical
backfill. The migration adds no relational table, column, or index.

There is no old/new binary coexistence window. The schema-18 release manifest
rejects future migration 0019 at readiness, while the schema-19 build requires
0019 and the v2 functions. Rollout therefore drains and fences every schema-18 Api
and Worker from traffic/work, executes 0019 with the schema-19 Migrator, starts
only schema-19-compatible hosts, verifies readiness, and only then restores
traffic and publishes the target contract. An optional bridge build would need
its own schema-19 manifest, target-compatible Group response, CI evidence, and
release approval; no such bridge is part of this candidate. A post-migration or
post-publication rollback must use a contract-compatible schema-19 repair/current
build, or receive a new forward migration/public-contract decision. Routing work
back to the schema-18 binary is neither readiness-compatible nor contract-
compatible.

ADR 0006 Family C is extended only by the exact new function identity
`poolai_group_update_v2 -> subscriptions(group_id,status,expires_at)`. Its archive
branch follows the same `Quota -> Group -> Template/Subscription` order, fields,
fresh post-wait PostgreSQL clock, and read/row-lock-only boundary as
`poolai_group_update`. `poolai_group_create_v2` does not add a cross-context read.
No other function, table, field, lock direction, or cross-context write is added.

After canonical Group access succeeds, each authenticated `/v1/responses` or
`/v1/chat/completions` inbound request invokes the existing Group RPM Redis
primitive exactly once with that Group ID and current PostgreSQL limit. Models,
Usage, authentication failures, validation failures before canonical admission,
and failover attempts do not increment it. A later M4-E5 failover redoes all
canonical PostgreSQL and Supply reads but never recounts the inbound RPM. Redis
`TIME`, `rate:group:v1`, the 120-second TTL, script ABI/version, and fail-closed
dependency behavior remain unchanged; this ADR creates no Redis key, Lua script,
argument, return shape, or manifest version.

The exact three compatibility diagnostics in this ADR are the complete allowed
set for the stated base commit/base digest and target digest. Missing, additional,
or changed diagnostics fail closed. Approval of this ADR does not itself approve
the compatibility-window registry entry; both decisions must be independently
and explicitly enumerated in permanent evidence before the target can be treated
as authorized. One Issue comment may carry the immutable evidence URL only when
it contains two separately scoped APPROVED decisions—one for this ADR and one for
the exact compatibility window. After protected merge the target becomes the
ordinary v1 baseline and the window is inert.

### Trusted proxy and API Key CIDR enforcement

The Api Host adds only these startup-time ingress settings:

| Key | Type | Default | Validation |
|---|---|---:|---|
| `Gateway:Ingress:TrustedProxyCidrs` | `string[]` | `[]` | At most 64 unique canonical CIDRs; an exact address is represented as `/32` or `/128`; reject host bits, `/0`, IPv6 zone IDs, IPv4-mapped IPv6 entries, duplicates, and non-canonical text. |
| `Gateway:Ingress:ForwardedForLimit` | `int` | `1` | Inclusive range `1..8`. |

The raw `X-Forwarded-For` field value is additionally capped at 1024 UTF-8 bytes;
that cap is fixed and has no configuration override. The client-IP resolver uses
this exact algorithm:

1. The connection socket `RemoteIpAddress` is the initial trust anchor. A null
   value fails closed. An IPv4-mapped IPv6 socket address is normalized to IPv4.
2. If the immediate socket peer is outside `TrustedProxyCidrs`, ignore every
   forwarding header and use the socket peer as the client address.
3. If the immediate peer is trusted, accept exactly one logical
   `X-Forwarded-For` field and never consult RFC `Forwarded`, `X-Real-IP`, or any
   other client-IP header. Multiple field instances, an overlong value, or an
   empty value fail closed.
4. Split only on commas, trim optional whitespace, and require `1..ForwardedForLimit`
   non-empty bare IP literals. Reject ports, brackets, quotes, zone IDs,
   `unknown`, obfuscated identifiers, whitespace inside a token, non-canonical
   or ambiguous IPv4 text, and any other syntax. Normalize IPv4-mapped IPv6
   literals to IPv4 before comparison.
5. Starting at the socket peer, peel parsed hops from right to left. A hop may be
   consumed only while the current hop is in `TrustedProxyCidrs`. The first
   non-trusted parsed hop is the client address and everything to its left is
   ignored. If all parsed hops are trusted, the leftmost parsed hop is the client
   address.
6. A malformed trusted-proxy chain fails closed. API Key CIDR enforcement then
   treats `allowed_cidrs=[]` as unrestricted; otherwise the resolved client must
   match at least one canonical API Key CIDR. A malformed chain or mismatch maps
   to the existing `401 invalid_api_key` response without revealing which check
   failed.

Raw client addresses and forwarding headers never enter logs, metrics, traces,
audit payloads, or problem details. Where correlation is necessary, use the
existing keyed safe-IP hash only. This decision changes no API Key table, OpenAPI
shape, error code, Redis state, or public response. The Gateway admission snapshot
may carry only the resolved address needed for the check and its non-secret
decision; the raw header is discarded.

### Conservative-v1 Token estimate

M4-E1 uses a pure deterministic `conservative-v1` estimator before Supply
routing. It serializes the normalized Gateway payload with one fixed compact
`System.Text.Json` configuration, UTF-8 encoding, no indentation, and the
normalized property/value representation already produced by the protocol
boundary. It then computes:

```text
estimated_input_tokens = max(1, compact_utf8_byte_length + 64)
estimated_output_tokens = explicit max_output_tokens or max_completion_tokens,
                          otherwise Gateway:DefaultMaxOutputTokens
estimated_tokens = estimated_input_tokens + estimated_output_tokens
```

All arithmetic uses checked `BigInteger` intermediates before conversion to the
safe reservation input. `Gateway:DefaultMaxOutputTokens` remains `4096` and
`Gateway:MaxEstimatedTokensPerAttempt` remains `2,000,000`; startup validation
requires `DefaultMaxOutputTokens <= MaxEstimatedTokensPerAttempt`. The estimator
accepts no database, Redis, HTTP, tokenizer service, provider SDK, Account, or
model-dependent fallback.

An explicit output limit outside its field contract, or a total above
`MaxEstimatedTokensPerAttempt`, fails with the existing `422 validation_failed`
before route/lease/reservation/upstream activity. Output-caused failure points at
the submitted max field; input-only excess points at `/input` for Responses or
`/messages` for Chat. This estimate is intentionally conservative settlement
evidence for ambiguous dispatch, not an exact provider tokenizer or a cumulative
quota definition.

### Lossless normalized upstream usage

Lossless usage is an existing accepted contract correction, not a new quota or
database decision. `NormalizedUpstreamResult` must stop narrowing input/output
usage to nullable `long` values and instead carry one immutable
`NormalizedUpstreamUsage` whose numeric components use `BigInteger`: input,
output, cache-read, cache-creation, and thinking Tokens, plus bounded raw usage
evidence needed by the protocol fixture.

Protocol adapters parse upstream JSON integer text without passing through
`double`, `decimal`, `long`, or JavaScript number. Negative, fractional,
exponent-form, signed, malformed, or semantically inconsistent usage is rejected
as an upstream protocol failure. Reliable usage is settled losslessly first.
Only after the authoritative fact commits may an OpenAI response expose usage as
a JSON safe-integer number. A component above `2^53-1` terminates the public
response or stream with the existing `upstream_usage_out_of_range` behavior; it
is never rounded, saturated, or discarded. A value beyond PostgreSQL
`numeric(78,0)` fails the complete settlement transaction and raises the existing
`token_numeric_overflow` P0 path while leaving the reservation recoverable.

No new table, column, Redis state, public usage field, or stable error is needed.
M4-E1 freezes the lossless abstraction and process seam; M4-E2 and M4-E3 own the
provider-specific Responses/Chat parser fixtures and outward mapping.

### Route snapshot, credential handoff, and outbound transport

Every Supply `AccountCandidate` used by M4-E1 adds the non-secret
`credential_revision`. The immutable selected route contains exactly the facts
needed to bind an attempt: Group, Channel, and Account IDs; provider; client and
upstream model; canonical upstream base URI; capability snapshot; Supply
Configuration, Channel, and Account versions; and credential revision. Route
objects, DTOs, logs, traces, `ToString()` output, exceptions, and audit metadata
must never contain a credential, envelope, Authorization value, key material, or
lease buffer.

Supply exposes through `Supply.Abstractions` a bounded, revision-fenced,
single-use credential lease port. Acquisition rechecks that the selected Account
is current and active and that its Account version, credential revision, provider,
and canonical authority still match the route. It decrypts only after those
checks and returns no reusable secret snapshot. Gateway acquires this lease after
the quota reservation commits and before Adapter prepare/dispatch-fence commit,
so an acquisition failure is still a pre-dispatch releasable attempt.

Gateway wraps the Supply lease in an opaque, one-use credential handle defined in
`Gateway.Abstractions`; the Adapter continues to depend only on
`Gateway.Abstractions`. The handle permits applying the credential only to the
single bound outbound request and zeroizes the buffer on use, failure,
cancellation, or `Dispose` in `finally`. It cannot expose bytes/string, be cloned,
serialized, cached, logged, reused by another request/attempt/authority, or cross
an async lifetime after disposal.

Before that handle can attach Authorization, the outbound transport applies the
ADR 0010/0011 connection-time SSRF fence to the exact route authority: resolve and
classify every DNS answer; reject none, mixed, or disallowed answers; connect
directly to a vetted address while retaining the original Host header, TLS SNI,
and certificate verification; require production HTTPS; disable proxy-environment
inheritance and redirects; and forbid credential or connection-pool reuse across
authorities. Authorization is attached only after the destination is vetted. The
existing health address classifier/configuration may be extracted for shared
Supply Infrastructure use, but Gateway transport rules may not be weaker than
the active probe rules.

This handoff changes no Account credential storage, Envelope v1 format, keyring,
database permission, public API, or Redis contract. It also does not authorize a
real credential read, decrypt, upstream call, egress-rule change, or key
operation.

The already frozen Account lease lifetime remains part of the same attempt. While
upstream work is active, Gateway renews it every 20 seconds. A typed `Lost` result
immediately cancels upstream work; two consecutive coordination failures also
cancel it. Account-lease cancellation and the existing reservation lifetime
coordinator race into one linked attempt cancellation source. Cancellation starts
at most 15 seconds of bounded drain, then the Process Manager settles or releases
strictly according to the dispatch phase before disposing the credential and
Account lease. Renew, cancel, drain, settle/release, and disposal never keep a
PostgreSQL transaction open across their waits.

### Process Manager and ownership

The fixed outer order remains correlation/observability -> admission bulkhead ->
authentication -> request validation/body limit -> Gateway Process Manager. For
each model request M4-E1 performs one canonical admission and one Group RPM check,
then creates an `AttemptContext` with phase, route, Account lease, reservation
handle, credential handle lifetime, dispatch state, downstream output boundaries,
deadline/retry budget, and settlement evidence.

The single M4-E1 attempt seam is:

```text
canonical access -> Group RPM once -> map/estimate -> current Supply read ->
route/Account lease -> reserve -> credential acquire/Adapter prepare ->
dispatch fence -> upstream -> settle/drain -> credential and Account lease release
```

No PostgreSQL Unit of Work spans Redis, credential decryption, HTTP, SSE, drain,
or backoff. M4-E1 may expose a typed outcome from this one-attempt seam but does
not implement a recursive retry loop. M4-E5 alone may create a later attempt,
after settling the previous one, redoing canonical/Supply reads, and respecting
the phase/capability/deadline/budget contract. Adapter, timeout, breaker, and HTTP
libraries never create another attempt themselves.

## Alternatives considered

### One immutable global RPM default

Rejected. It cannot express the selected complete per-Group policy and would make
the control-plane Group resource disagree with runtime admission.

### Put RPM on Subscription, Supply, Account, or Redis

Rejected. Subscription grants access but owns no quota; Supply/Account describe
upstream capacity; Redis coordinates a window but is not the durable policy
authority. Any of them would create a second source of Group policy.

### Reuse an old Group function with a same-name overload

Rejected. Named v2 functions prevent silent reinterpretation and keep an explicit
bridge/corrective-build seam. They do not authorize or enable the schema-18 binary
to remain ready after migration 0019.

### Trust forwarding headers whenever they are present

Rejected. An untrusted direct client could spoof an allowed address. Trust begins
only at the socket peer and moves right-to-left through configured proxies.

### Use a provider tokenizer or character divisor for estimation

Rejected for M4-E1. Provider calls introduce I/O and failure into pre-reservation
admission, while heuristic division risks under-accounting. The fixed byte-plus-
overhead algorithm is deterministic and conservative; later replacement requires
a new versioned decision and migration plan for reproducibility.

### Pass decrypted credentials or Supply leases to the Adapter

Rejected. Plain values are easy to retain or log, and a Supply type would reverse
the allowed Adapter dependency. The opaque Gateway handle preserves both secret
lifetime and module direction.

### Keep `long` usage and clamp large values

Rejected. It contradicts the accepted lossless Token contract and can silently
undercharge or corrupt immutable facts.

## Consequences

- Group administrators can set a bounded, auditable per-Group RPM policy while
  Group remains the only cumulative Token quota subject.
- Migration 0019 requires a drained, fenced cutover: schema-18 hosts stop before
  migration, and only schema-19-compatible hosts start afterward. Preserved old
  function identities provide an explicit future bridge seam, not online binary
  coexistence.
- Deployments with no trusted proxy configuration safely use the direct socket
  peer. Production proxy topology must be explicitly configured and restart-
  validated before forwarded client addresses are trusted.
- Estimates are reproducible across hosts and crash recovery, at the cost of
  intentionally overestimating byte-heavy payloads.
- The Adapter receives sufficient route and credential capability without
  learning Supply implementation details or widening secret exposure.
- Lossless upstream usage may cause a response to fail after authoritative
  settlement when a provider returns a value that cannot be represented safely
  by the public OpenAI number shape; this is preferable to corrupting facts.
- M4-E5 failover remains independently scoped and cannot be smuggled into the
  M4-E1 implementation.

## Migration and rollback impact

This accepted decision itself executes no migration. The independently governed
forward candidate `0019_group_runtime_policy_m4_e1.sql` owns the exact runtime-
policy backfill/constraint, v2 function ABI, grants, checksums, and release-
manifest entry. It has received its own [Issue #44 database approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5506361132).
ADR approval does not approve migration 0019; database approval does not accept
this ADR or the OpenAPI window. Neither approval authorizes remote execution.

Before any target is shipped, cancellation requires permanent withdrawal evidence
and a new governed decision; the accepted ADR/window and database approval remain
immutable history. Once 0019 has
executed, its bytes and checksum are immutable: correction uses a new forward
migration. Once the public target merges, its OpenAPI compatibility window is
inert and reversal requires a new public-contract decision rather than editing
this history. Runtime rollout must first drain and fence all schema-18 Api and
Worker instances, execute 0019 with the schema-19 Migrator while they remain out
of service, start and verify only schema-19-compatible hosts, and then restore
traffic and publish the target response contract. Before 0019 begins, rollback
may restart the unchanged schema-18 release. After 0019 begins, rollback is
forward-only through a schema-19-compatible repair/current build or a new governed
migration; the preserved old functions do not make the old binary ready or its
Group response contract-compatible.

No down migration, remote data rewrite, Redis flush/script mutation, credential
rotation, egress change, deployment, or upstream operation is authorized here.

## Security impact

The accepted decision closes spoofed client-IP, ambiguous quota-estimate, credential-
leakage, cross-authority Authorization, SSRF rebinding, and Token-truncation
paths. All failures are fail-closed before external dispatch where possible.
Security telemetry must remain bounded and redacted: no raw client IP, forwarding
header, request payload, credential, envelope, Authorization value, private host
material, or provider response body may appear in logs or governance evidence.

Exact failure-shape behavior continues to use the accepted error catalog. This
ADR does not introduce a debug bypass, trusted-proxy wildcard, insecure HTTP
production fallback, direct secret accessor, permissive unknown runtime policy,
or best-effort quota/RPM mode.

## Coupled contract and test files

This architecture decision is coupled to the following authoritative assets,
which retain their own approval scope:

- `docs/contracts/openapi-v1.yaml` and
  `docs/contracts/compatibility-windows-v1.json` for the exact Group field,
  conditional PATCH reason, response requirement, and three-diagnostic window;
- `docs/database/0019_group_runtime_policy_m4_e1.sql` and
  `docs/database/README.md` for the exact two-key backfill/constraint, old ABI
  preservation, v2 functions, permissions, and ADR 0006 Family C identity;
- `docs/release-manifest-v1.json` and the Migrator embedded-resource catalog for
  the independently approved migration checksum and schema window;
- `docs/architecture/design-pattern-baseline.md`, `docs/开发执行规格-v1.0.md`,
  `docs/系统重构方案-v1.0.md`, and
  `docs/traceability/release-1-traceability.json` for ownership, order,
  configuration, milestone, and planned evidence; and
- the existing Redis contract/scripts/manifest, which must remain byte-for-byte
  unchanged for this decision.

Implementation must update or add the following precise verification surfaces:

- `tests/PoolAI.ContractTests/GeneratedContractRoundTripTests.cs` and
  `tests/PoolAI.EndToEndTests/GroupQuotaEndpointContractTests.cs` for Group
  create/update/response/default/range/conditional-reason behavior;
- `tests/PoolAI.IntegrationTests/PostgresMigrationTests.cs` and a focused
  `PostgresMigrationTests.M4E1GroupRuntimePolicy.cs` partial for backfill,
  constraint, old/new ABI, grants, replay, CAS, and Family C concurrency;
- `tests/PoolAI.UnitTests/ConfigurationValidationTests.cs` for trusted-proxy and
  estimator startup invariants;
- new focused `GatewayTrustedProxyTests.cs`, `GatewayConservativeEstimatorTests.cs`,
  and `GatewayCredentialHandoffTests.cs` in `tests/PoolAI.UnitTests`;
- new `M4E1GatewayPipelinePostgresRedisTests.cs` in
  `tests/PoolAI.IntegrationTests` for one inbound RPM, canonical policy, stage
  order, lease/reservation/credential cleanup, and dependency failure; and
- new `GatewayBoundaryTests.cs` in `tests/PoolAI.ArchitectureTests` plus M4-E2/E3
  Adapter contract fixtures for Abstractions-only dependencies, no secret/raw-IP
  surfaces, lossless `BigInteger` usage, output-safe-number behavior, and no
  Adapter-owned retry.

Those tests are planned evidence until implemented and green. This Accepted ADR
does not promote any DEC/AC state, authorize source changes, or claim M4-E1
complete.
