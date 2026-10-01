# M4-E3 Chat Completions runtime

This is an implementation map, not a new protocol authority. The governing assets remain
[`openapi-v1.yaml`](../contracts/openapi-v1.yaml), the [error catalog](../contracts/error-catalog.md),
the [canonical fixtures](../contracts/fixtures/), the [execution specification](../开发执行规格-v1.0.md),
and accepted [ADR 0017](../architecture/adr/0017-freeze-shared-post-stream-admission-discriminator.md).
M4-E3 formally depends on M4-E1, not M4-E2. This candidate is temporarily stacked on the
M4-E2 code base to reuse its shared POST admission boundary; that does not accept or close M4-E2.

## Admission and ownership

`POST /v1/chat/completions` and Responses use one fenced preparation/execution owner.
Only the strict protocol parser and HTTP output projection vary. The shared discriminator,
selected partition, authentication precedence, replay cleanup, and late-continuation fence
are described in [Responses runtime](responses-runtime.md) and governed by ADR 0017.
Canonical authorization and one Group RPM charge precede the existing single-attempt process.
Endpoints own no database, Redis, routing, quota, or credential implementation.

## Protocol and streaming

`ChatProtocolAdapter` validates the frozen text-message and function-tool subset, including
system/developer/user/assistant/tool history, text parts, tool choice, mathematical safe-integer
completion limits, and Unicode-scalar string lengths. Tool calls and results remain data;
the gateway never executes functions. Unknown features and duplicate JSON names fail closed.
`ChatUpstreamAdapter` rewrites the configured provider model and binds `/chat/completions`
through the existing vetted, revision-fenced, one-use credential/transport boundary.

The response parser validates strict UTF-8, identity, choice/tool indices, stable function
identity, finish ordering, usage ordering, and the data-only SSE wire format. It uses defensive
upstream bounds of 16 MiB JSON, 1 Mi characters per SSE frame, 4096 retained identities,
and 16 Mi retained metadata characters. Streamed text and function arguments are not retained
or reconstructed. Comments and partial bytes do not satisfy the first-valid-chunk deadline.
Backpressured writes use `IGatewayResponseOutput`; business-output evidence remains owned
by the shared Gateway attempt output wrapper.

The final usage chunk and `data: [DONE]` are held until successful quota settlement. There
are no Responses `event:` fields. Once headers have started, failure emits only the frozen
Chat error object and never `[DONE]`; before headers it uses the existing Gateway problem
projection. Provider error messages and arbitrary provider metadata are not forwarded.

## Usage and cancellation

Counters are parsed from canonical non-negative integer lexemes into `BigInteger`, without
floating-point rounding. Total/cache/thinking relations are checked. Known cache and prediction
details preserve absence rather than inventing public zero values. Unsafe OpenAI integer values
remain exact internal evidence but cannot escape as a successful response; the existing database
integer bound and atomic rollback remain unchanged. Missing usage uses conservative settlement.

Streaming requests ask the upstream for `include_usage=true` to obtain final accounting evidence
even when the client chooses `include_usage=false`. The client option controls public projection:
false hides the independent usage chunk; true emits it before `[DONE]` and gives prior chunks
explicit `usage=null`. This adds no client field or changes to the public schema.

Client disconnect suppresses further output while the existing independently bounded upstream
drain can collect known usage, settle the cancelled attempt, zeroize credentials, and release
the Account lease. No database transaction spans HTTP streaming or the drain. Chat does not
record Responses affinity and adds no database migration or Redis ABI.

## Verification and delivery boundary

Contract tests read the canonical fixtures directly, including text/function SSE, half-packets,
errors, usage, Unicode lengths, malformed identity/order, timeouts, and parser bounds. HTTP
admission tests cover both selected partitions, authentication precedence, saturation, parser
disagreement, and the shared preparation deadline. Architecture tests enforce one shared
admission owner and vendor-neutral ports. Real local PostgreSQL 18/Redis public-API tests
exercise production authorization/routing/dispatch/settlement/audit/outbox/lease cleanup and
a loopback mock upstream, including gated cancellation and unsafe usage.

AC-028 and AC-045 record only their Chat slices as `implemented-local`; both overall criteria
remain partial. Repository-development evidence does not imply real-provider acceptance,
remote migration, deployment, M4 Exit, or physical release certification.

Protected delivery remains open in [Issue #26](https://github.com/Lyon1984/PoolAI/issues/26).
The shared code base still has the [M4-E2 blockers](../project-memory/open-items.md), including
selected-provider usage authority and pre-DOM JSON allocation; this candidate introduces no
unapproved usage-quarantine policy or client JSON-node budget. The Host image prerequisite,
exact-base review if the stack is rebased, and protected final-main gates remain independent.
