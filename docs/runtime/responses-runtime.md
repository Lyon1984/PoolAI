# M4-E2 Responses runtime

This is an implementation map, not another protocol source. The governing assets remain
[`openapi-v1.yaml`](../contracts/openapi-v1.yaml), the [error catalog](../contracts/error-catalog.md),
the [canonical fixtures](../contracts/fixtures/), the [execution specification](../开发执行规格-v1.0.md),
and accepted [ADR 0017](../architecture/adr/0017-freeze-shared-post-stream-admission-discriminator.md).

## Admission and ownership

`POST /v1/responses` first acquires the eight-permit, zero-queue model discriminator.
Its private bounded replay file and incremental lexer classify only the first top-level
`stream` value. Unclassifiable, encoded, or oversized bodies use the non-stream quarantine
path and cannot execute. Exactly one NonStream or SSE admission lease is selected, with
no fallback to another partition. Key/CIDR authentication precedes media, syntax, and
schema errors; successful strict parsing and the immutable mode-consistency fence release
the replay file and guard before canonical authorization, the single Group RPM charge,
Account lease, reservation, or upstream I/O. The entire preparation has the frozen
30-second monotonic deadline and a fence against late uncooperative continuations.

The guard uses at most 64 KiB per request, one delete-on-close `0600` replay file in a
private `0700` directory, and at most the configured body limit plus one byte. These
resources are independent of the four business bulkheads. Connected overload/deadline/
storage failures return `gateway_overloaded` and `Retry-After: 1`; an aborted client receives
no synthesized response. Only bounded internal active/rejection metrics are emitted.

## Protocol and output

`ResponsesProtocolAdapter` accepts the frozen text/message/function-history subset;
the gateway does not execute tools. `ResponsesUpstreamAdapter` binds the configured
provider model and `/responses` path, and uses the existing revision-fenced, one-use
credential and vetted transport. API and Worker remain separate Hosts.

The response parser validates strict UTF-8 JSON, duplicate names, the typed SSE union,
contiguous sequence numbers, response/item/content identity, lifecycle order, and exact
text/function argument reconstruction. It bounds JSON responses to 16 MiB, individual
SSE frames to 1 Mi characters, retained lifecycle text to 16 MiB, and items/parts to 4096.
These are defensive upstream-parser bounds, not additional client-request schemas.
Comments or partial bytes do not satisfy the first-valid-event deadline. Backpressured
output uses the vendor-neutral `IGatewayResponseOutput` port; attempted first business
writes make the attempt non-replayable even if a frame write fails partway through.

`response.completed` is held until quota settlement succeeds. There is no `[DONE]`.
Provider error bodies/messages are not forwarded. Before downstream headers, failures
use the Gateway JSON problem projection; afterwards, only a contract-allowed flat terminal
`error` event is written. First-byte and idle timeouts retain their precise contract codes.

## Usage, disconnect, and minimal affinity

Usage counters are parsed losslessly as canonical non-negative integer lexemes into
`BigInteger`. The total and cache/thinking relations must be consistent. Missing usage
uses the existing conservative estimate. Values above the OpenAI safe-integer bound
remain available for exact internal settlement but never become a successful public
usage payload; the existing 78-digit database bound and atomic rollback remain unchanged.

Client disconnect suppresses further output but does not cancel the independent bounded
upstream drain. Known usage can still settle a cancelled request, followed by credential
zeroization and Account-lease cleanup. No database transaction spans the stream or drain.

`previous_response_id` produces a Group/API-Key-scoped HMAC affinity key. Successful,
settled responses record the chosen Account using the existing version-fenced advisory
Redis affinity store. Failure to store affinity cannot roll back a settled request;
stale/unavailable affinity cannot bypass canonical route validation or cross Group boundaries.
This is not a response-history store and adds no database migration or Redis ABI.

## Verification boundary

Contract tests read the canonical fixtures directly and cover half-packets, lifecycle
corruption, usage, errors, and parser bounds. Public-API end-to-end tests use real local
PostgreSQL 18 roles and Redis, real production composition/authorization/routing/settlement,
and a loopback mock upstream. They include text and function SSE, non-stream, missing
usage, premature EOF, unsafe usage, affinity persistence, and a deterministically gated
client disconnect. Guard tests separately prove partition isolation and late-continuation
fencing. These are repository-development evidence, not remote migration, deployment,
real-provider, physical certification, M4 Exit, or Release 1 approval.
