# ADR 0011: Freeze the upstream-health and shared-breaker boundary

- Status: **Accepted**
- Date: 2026-07-30
- Decider: PoolAI architecture, Supply, Routing, operations, and security owner (`@Lyon1984`)
- Relates to: DEC-030, DEC-037, DEC-041, AC-042, [M2-E4 Issue #18](https://github.com/Lyon1984/PoolAI/issues/18), ADR 0002, ADR 0010, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Architecture approval evidence: [Issue #44 permanent approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5135660266)
- Coupled OpenAPI target approval: [Issue #44 permanent approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5135813781)

## Context

M2-E4 must add proactive and passive Account health, an Account-scoped shared
circuit breaker, and one cluster-wide half-open probe. The frozen architecture
already assigns persistent Account health and credentials to Supply, shared
short-lived breaker state to Routing, generic Redis mechanics and Worker
session locks to Operations, and each executable's object graph to its own
Composition Root.

The existing Host allowlist nevertheless loads Routing only in Api, while the
Redis acceptance contract requires two Api instances and Worker to contend for
the same half-open probe. Moving breaker ownership into Supply would reverse the
Context Map and duplicate Routing policy. Putting business-aware Lua execution
in Operations would turn a technical adapter into a breaker owner. Letting
Worker load the complete Routing graph would unnecessarily add normal request
routing, affinity, Group RPM, or later Gateway capabilities to a background
Host.

Two health-state descriptions also disagree. The execution specification
previously grouped a newly created `unknown` Account with an expired cooling
Account and called both half-open. The higher-priority Redis contract explicitly
states that a new Account has no previous open generation and therefore is not
half-open. It needs a controlled active validation; only a previously opened
breaker may enter half-open.

Finally, ADR 0010 intentionally left connection-time DNS, redirect, and egress
controls to the first outbound milestone. M2-E4 is that milestone for the
Supply Health Worker. Stored Base URL syntax alone is not sufficient SSRF
protection, and no PostgreSQL transaction may remain open during DNS, HTTP,
TLS, backoff, or response parsing.

## Decision

This decision was accepted by `@Lyon1984` through the permanent architecture
approval linked above. The existing Account PATCH operation also gains a
normative description of the same-write credential-replacement rule below. Its
exact OpenAPI target SHA-256
`ba965851bd6b9b4996ecab2bdf9c947e77981a40e559005ff6618a5269015afe`
was separately approved through the coupled OpenAPI evidence linked above; the
compatibility analyzer reports no structural diagnostic and therefore no
compatibility window is opened. Architecture, OpenAPI, the independently
approved [migration 0012](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5135902002),
and the exact [Redis contract/scripts](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5135961558)
remain separate approvals and do not substitute for one another. None of these
approvals authorizes remote Lua execution, a remote database migration, a real
credential operation, deployment, M2-E4 closeout, or M2 Exit.

### Ownership and dependency direction

1. Supply remains the sole owner of Account lifecycle, credential access,
   persistent health, `upstream_rate_limited_until`, and health-transition
   audit intent. It exposes only bounded, immutable candidate/credential-lease,
   active-probe, and health-writer ports through `Supply.Abstractions`; it never
   references Routing implementation or Redis.
2. Routing remains the sole owner of Account breaker semantics, passive outcome
   classification, closed/open/half-open mapping, shared probe acquisition, and
   the decision to admit or reject an Account. Routing consumes the Supply ports
   and Operations coordination ports already allowed by the dependency DAG.
3. Operations owns only manifest-bound Redis connection, script loading,
   strict fixed-array evaluation, key safety, and PostgreSQL session advisory
   lock mechanics. Its breaker coordination port is exact and versioned; it is
   not a generic script executor and does not decide Account health, retry,
   failover, or persistent state.
4. Gateway will later report normalized passive upstream outcomes through the
   Routing port. A breaker observation never grants retry or failover; the
   phase/evidence/deadline rules remain Gateway-owned under DEC-041.
5. `PoolAI.Worker` may reference the Routing implementation only through a
   distinct `AddRoutingHealthModule(IConfiguration)` registration. That
   registration closes
   the breaker/probe and Supply-health coordinator graph and may register the
   `WorkerJobs.SupplyHealth` hosted loop. It must not register
   `IAccountRouter`, route affinity, Group RPM, public endpoints, Gateway,
   `PoolAI.Adapters.OpenAI`, or any protocol Adapter.
6. Api continues to call `AddRoutingModule()`. It receives the same
   manifest-bound breaker/probe coordinator for passive feedback and future
   Gateway use, but never receives the Supply Health hosted loop.

The Worker Composition Root therefore changes by one narrow, test-enforced
Routing health-only capability rather than by loading the full Api Routing
surface. `PoolAI.Worker/Program.cs` remains registration-only and contains no
health classification, key construction, HTTP, or scheduling business rules.

### New `unknown` versus breaker half-open

- A new Account and an Account whose credential was replaced have persistent
  health `unknown`, but no breaker open generation. They are excluded from
  ordinary routing and are eligible for a controlled active validation. A
  successful, version-fenced validation deletes any Redis breaker/cooldown
  generation belonging to the prior credential and may write `healthy` without
  manufacturing an `open_count` or half-open state. If the conditional writer
  detects a concurrent change, persistent `unknown` remains fail-closed and the
  new version must be validated again.
- Automatic initial validation may select retained `active` or `disabled`
  Accounts whose canonical health is `unknown` and whose credential/Base URL
  revision is current; `retired` is never eligible. A successful validation
  does not activate a disabled Account, change lifecycle, or make it routable.
- A transiently open Account becomes half-open only when the shared breaker has
  `open_count > 0`, `auth_blocked = 0`, and both Redis `open_until_ms` and the
  stricter persisted cooldown have expired, and canonical lifecycle is still
  `active`. It remains persistently `unknown` and excluded from ordinary
  routing while half-open; a disabled or retired Account cannot obtain a
  half-open owner.
- Every half-open attempt must acquire the unique 10-second probe owner and an
  ordinary Account concurrency lease. The first successful owner completion
  remains half-open; a separately acquired second consecutive success closes
  the breaker and writes `healthy`. Any attributable failure reopens it.
- A `401/403` observation sets `auth_blocked=1` and persistent `unhealthy`.
  Time passage never makes it half-open. Automatic rounds do not probe it.
  Credential replacement, an explicit administrator revalidation, or another
  version-bound controlled validation intent may authorize an active validation;
  only a successful `controlled_active` observation for canonical
  `unknown/last_health_at IS NULL` may retire the prior breaker generation.
  Routine healthy/degraded active checks use the non-resetting `passive`
  breaker mutation semantics; a passive or late success cannot clear an open
  generation.
- PostgreSQL and Redis are read independently. Missing, corrupt, unavailable,
  or contradictory coordination state uses the stricter result and rejects;
  no in-process breaker is a fallback.

### Controlled active health probe

The Supply Health job uses the existing versioned PostgreSQL session advisory
lock. A standby Worker that does not own the lock performs no probe. Lock loss
or connection loss stops the current round; it does not switch to a Redis
leader key.

For each eligible Account, the coordinator re-reads its current lifecycle,
health, Base URL, provider, public version, credential revision, and encrypted
credential through Supply-owned ports. The outbound call and all DNS/TLS/HTTP
work occur outside every PostgreSQL Unit of Work. The eventual persistent
health write is a separate short UoW and is conditional on the observed Account
version and credential revision, so a stale probe cannot overwrite a concurrent
disable, retire, credential replacement, or newer observation.

The R1 active operation is one authenticated `GET` of the Account Base URL with
exactly one `/models` suffix: remove only trailing `/` characters from the
already validated absolute Base URL and append `/models`. It sends the API key
only as `Authorization: Bearer <credential>` after the destination has passed
the connection policy. It sends no request body, Group identity, user prompt,
quota token, or Account identifier. A successful validation requires HTTP 200
and a bounded JSON object containing a `data` array; a malformed, oversized, or
truncated success response is a protocol failure.

Each probe has a fixed ten-second total deadline, a 1 MiB response-body limit,
no automatic redirect, no transport retry, and no nested resilience policy.
The Worker performs at most one active request for the same Account in one
round. A `/models` probe is not a model-generation attempt and creates no Group
reservation or usage fact, but it still holds an Account lease until response
classification and bounded drain complete.

The active and passive classifiers use the same normalized breaker outcomes:

- verified HTTP 200 protocol success: `success`;
- DNS/TCP/TLS failure, HTTP 408, 5xx, redirect, timeout, malformed protocol,
  oversized response, or truncated response: `transient_failure`;
- HTTP 429: `rate_limited`, with a syntactically valid delta-seconds or
  IMF-fixdate `Retry-After` normalized to milliseconds and finally bounded by
  Redis time to 1 second through 24 hours; missing or invalid input uses
  30 seconds;
- HTTP 401/403: `auth_failure`;
- another request-attributable 4xx, client cancellation, local Group
  quota/RPM rejection, or local bulkhead rejection: `ignored`.

An ignored controlled probe cannot promote `unknown` to healthy. It leaves the
health fact unchanged and emits a bounded operational failure classification.
Breaker recording remains independent from whether a later Gateway attempt is
replayable.

### Connection-time SSRF and credential boundary

For every new probe connection:

1. re-parse the canonical stored Base URL and reject any drift from ADR 0010;
2. resolve the original hostname at connection time, normalize IPv4-mapped IPv6,
   and classify every returned address against the immutable deployment egress
   policy;
3. reject the entire resolution when any answer is forbidden or when no answer
   is explicitly permitted; do not silently choose a permitted address from a
   mixed answer set;
4. connect directly to one vetted address while retaining the original
   authority for HTTP `Host`, TLS SNI, and certificate-name validation;
5. do not reuse a connection across probe executions and do not follow any
   redirect; a 3xx response is classified without sending the credential to a
   second authority.

Production permits only HTTPS. Exact loopback HTTP remains available only in
Development/Test for deterministic local fixtures and still passes the same
address classification. Proxy environment variables, ambient default
credentials, URI userinfo, query parameters, and redirect-carried credentials
are forbidden. Base URLs, resolved addresses, credentials, response bodies, and
authorization headers never enter logs, metrics, traces, audit, or readiness
details.

An Account credential is bound to the normalized Base URL authority. A PATCH
that changes scheme, case-folded host, or effective port must replace the
credential in the same atomic Account write; a path-only change may retain it.
The PostgreSQL row boundary enforces this after the Account row lock so a
concurrent update cannot rebind an old credential to an attacker-selected
authority. Rejected authority changes expose only the existing bounded
`validation_failed` projection and never echo either URL or credential.

### Persistence, observability, and readiness summary

Redis state is updated first. A returned non-zero health action is then mapped
through the Supply health writer to `healthy`, `degraded`, `cooling`,
`unhealthy`, or `unknown`; `cooling.retry_at` is the absolute Redis-time
deadline returned by the script. The short PostgreSQL health UoW advances the
public Account version but never changes lifecycle or credential revision. A
real state transition and its append-only audit entry commit together; a
repeated observation that leaves the canonical state unchanged does not create
an audit storm.

Metrics and traces use bounded labels for observation source, classification,
breaker state, action, and success/failure. Account IDs may be audit targets but
are not metric labels. Credential material, Base URLs, remote response bodies,
and arbitrary exception text are excluded everywhere.

`SupplyHealthReadinessSummary` is an internal, credential-free diagnostic
snapshot, not an HTTP or OpenAPI resource. Each completed or failed round
records:

- `observed_at`, `cycle_status` (`succeeded/partial/failed/standby`), and a
  bounded `failure_code`;
- counts for Accounts seen and each persistent health value;
- counts for auth-blocked, probe-eligible, attempted, succeeded, and failed
  Accounts.

The summary contains no Account ID, name, Base URL, provider credential,
resolved address, exception message, or response content. An unhealthy pool or
a standby Worker does not make an otherwise sound process fail
`/health/ready`; canonical Group/Supply readiness already excludes unusable
Accounts. PostgreSQL, Redis, manifest/script incompatibility, or startup
dependency failure still fails the existing Api readiness or Worker startup
gate. A later failed health round marks the internal summary and alerts
Operations but leaves the Worker alive to retry under the session-lock
protocol.

### Redis ABI and governance boundary

The exact v1 key fields, arguments, integer codes, cooldown JSON, TTL behavior,
and fixed return arrays for `breaker_record_v1`,
`breaker_probe_acquire_v1`, and `breaker_probe_complete_v1` are defined only in
`docs/runtime/redis-contract.md`. Lua source and the release manifest must match
that contract atomically and require their own exact Redis approval. This ADR
does not approve script bytes, SHA-256 values, manifest entries, a Redis
operation, or a deployment.

If a Supply-owned forward migration is needed to expose a bounded Account
health transition entry point or tighten API/Worker ACLs, its SQL, checksum,
manifest window, and execution require an independent database approval. No
table owner or cross-context database read direction changes under this ADR.

## Alternatives considered

### Move breaker ownership into Supply

Rejected. Persistent health belongs to Supply, but shared routing exclusion,
half-open ownership, and Account capacity coordination are Routing policy.
Combining them would reverse the established Context Map and make Gateway and
Worker maintain competing breaker semantics.

### Let Worker load the complete Routing module

Rejected. Worker does not need ordinary Group routing, affinity, Group RPM, or
future Gateway registrations. A health-only registration makes the changed Host
surface explicit and architecture-testable.

### Put a generic Lua executor in Operations

Rejected. A generic executor would let any context bypass versioned,
fixed-result coordination ports and construct arbitrary Redis business
protocols. Operations may evaluate only manifest-bound exact operations.

### Treat every `unknown` Account as half-open

Rejected. A new Account has no prior open generation or cooldown and therefore
cannot satisfy the half-open precondition. Manufacturing breaker history would
also let ordinary routing contend with initial credential validation.

### Use the OpenAI Adapter or a model-generation request for health

Rejected. Worker explicitly does not load protocol Adapters, and a generation
request would require Group selection, reservation, attempt, dispatch fence,
and usage settlement. The bounded authenticated `/models` validation proves
the R1 Account credential/protocol seam without creating a free model request.

### Follow redirects after revalidating each target

Rejected for R1. Even with repeated DNS checks, redirect handling widens the
credential exfiltration and test surface. Health probes classify 3xx and send
the credential to exactly one approved authority.

### Fail process readiness whenever no Account is healthy

Rejected. Account availability is a pool/business readiness fact, not proof
that an Api or standby Worker process is corrupt. Group activation and every
attempt already consume canonical Supply health; dependency and manifest
readiness remain process gates.

## Consequences

- Worker gains one explicit Routing project reference and one narrowly bounded
  health registration. Frozen project-reference and Host-composition tests must
  be updated with negative assertions for all excluded Routing/Gateway surfaces.
- Routing can expose one breaker implementation to Api passive feedback and
  Worker active health without copying Lua or state mapping.
- Supply retains all credential and persistent health writes, while Operations
  remains a technical coordination adapter.
- New `unknown`, transient half-open, and auth-blocked revalidation become three
  distinct, testable flows.
- Active health adds an outbound security surface and therefore requires DNS,
  TLS, redirect, response-size, cancellation, redaction, and stale-write tests.
- No public route or schema shape is added; the readiness summary is internal.
  The existing `AccountUpdateRequest.base_url` description is made normative
  for the same-PATCH credential rule and is bound to the independently approved
  exact OpenAPI digest above. Because the compatibility analyzer reports zero
  structural diagnostics, this description-only transition does not open or
  consume an OpenAPI compatibility window.

## Migration and rollback impact

The architectural change is rollbackable before release by removing
`AddRoutingHealthModule(IConfiguration)` from Worker and withdrawing the M2-E4
implementation
and script candidate together. Redis breaker/cooldown/probe keys are
short-lived and versioned; rollback must not reinterpret them with a different
v1 ABI. A replacement implementation must either preserve the exact v1 ABI or
use new logical names and key versions.

Any database health-writer change is forward-only once applied and must use a
new migration rather than modifying signed migrations 0001 through 0011. This
ADR neither supplies nor approves that migration and authorizes no remote
database, Redis, upstream, credential, deployment, or data-repair operation.
Before the independently approved coupled OpenAPI target is merged, its rollback is
candidate withdrawal and regeneration from the already approved M2-E2
contract. After merge, the target becomes the ordinary v1 baseline; this ADR
does not create a reusable exemption for later description or schema changes.

## Security impact

- The health-only Host graph minimizes Worker authority and prevents accidental
  Adapter/Gateway loading.
- Connection-time address classification, original-authority TLS validation,
  disabled redirects, bounded responses, and one-use credential leases close
  the SSRF and credential-forwarding gap intentionally left by ADR 0010.
- Version/credential-revision fencing prevents a stale health result from
  resurrecting a disabled, retired, or re-keyed Account.
- Shared Redis time and owner fencing prevent node-clock drift and duplicate
  half-open probes.
- Bounded telemetry and internal summaries prevent secrets, URLs, addresses,
  high-cardinality IDs, and arbitrary upstream content from becoming
  observability data.
- Fail-closed Redis and PostgreSQL handling can reduce availability during a
  dependency fault, but it never converts uncertainty into an upstream call.

## Contract and test updates

- `docs/architecture/adr/README.md`
- `docs/README.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/开发执行规格-v1.0.md`
- `docs/contracts/openapi-v1.yaml` and its generated C#/TypeScript projections,
  under an independent exact target-digest approval
- `docs/runtime/redis-contract.md`
- Architecture tests for the Worker health-only Routing graph and negative
  Gateway/Adapter/full-Routing registrations
- Redis 8 integration tests for all three exact Lua ABIs, multi-client probe
  ownership, TTL, corruption, `NOSCRIPT`, and fail-closed behavior
- Supply health persistence/ACL/UoW/audit and stale-observation integration
  tests, under any independently approved forward migration
- deterministic mock-upstream tests for active/passive classification,
  connection-time SSRF, redirect, response limit, timeout, cancellation, and
  redaction
- AC-042 integration evidence for two Api clients plus Worker contending for
  one half-open owner and requiring two consecutive successful completions
