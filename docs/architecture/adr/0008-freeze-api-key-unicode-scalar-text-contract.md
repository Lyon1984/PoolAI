# ADR 0008: Freeze the API Key Unicode scalar text contract

- Status: **Accepted**
- Date: 2026-07-23
- Decider: PoolAI public-contract, Identity, and database owner (`@Lyon1984`); this candidate does not take effect without the approval evidence below
- Relates to: M1-E5 Issue #14, ADR 0007, and sign-off control Issue #44
- Compatibility window ID: `m1-e5-api-key-unicode-text-validation`
- Base Git commit: `f91b11950c117c382d2a6b96ac531fa864124101`
- Base OpenAPI SHA-256: `14380eab5b05f3b58ecb879969314868ef9cfdf23e6b6e39b3a283e211ebc58c`
- Target OpenAPI SHA-256: `1c9dee2fe48cd3e2f0fa5a00805e07e21d303b5a4fa070faeab66f3be6132141`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5055204931)
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyCreateRequest/properties/name/pattern: pattern changed from <none> to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyCreateRequest/properties/reason/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyUpdateRequest/properties/name/pattern: pattern changed from <none> to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyUpdateRequest/properties/reason/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/ApiKeyCreateRequest/properties/name/pattern: pattern changed from <none> to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/ApiKeyRotateRequest/properties/reason/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/components/schemas/ApiKeyUpdateRequest/properties/name/pattern: pattern changed from <none> to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/parameters/header:x-change-reason/schema/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/parameters/header:x-change-reason: reference changed from #/components/parameters/XChangeReason to #/components/parameters/ApiKeyChangeReason`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/parameters/header:x-change-reason/schema/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/parameters/header:x-change-reason: reference changed from #/components/parameters/XChangeReason to #/components/parameters/ApiKeyChangeReason`

## Context

The accepted M1-E5 contract did not give API Key names one exact non-blank
definition: OpenAPI froze only `minLength`/`maxLength`, the .NET candidate used
UTF-16 length plus `Trim`, and signed migration 0008 used PostgreSQL
`btrim`/`char_length`. API Key audit reasons had the same conflict:
OpenAPI used the runtime-dependent `.*\S.*` pattern while the application and
0008 applied different whitespace and control-character rules.

Those differences are observable at the public boundary. Supplementary Unicode
values consume two UTF-16 code units but one JSON Schema/PostgreSQL character;
the meaning of `\S` depends on the regex implementation; and trimming changes
an otherwise legal audit value before it is hashed, persisted, or replayed.
The signed bytes of migration 0008 cannot be edited to hide this conflict.

## Decision

### One frozen scalar contract

All governed API Key name and reason inputs use Unicode scalar values, not
UTF-16 code units or bytes:

- a name contains `1..100` scalar values;
- a reason contains `1..500` scalar values;
- each value contains at least one scalar outside this frozen Unicode
  White_Space set: U+0009..U+000D, U+0020, U+0085, U+00A0, U+1680,
  U+2000..U+200A, U+2028, U+2029, U+202F, U+205F, U+3000;
- C0 U+0000..U+001F, DEL/C1 U+007F..U+009F, U+2028, U+2029, surrogate
  code points, malformed encodings, and values outside the Unicode scalar range
  are rejected;
- every other scalar is legal, including supplementary values and legal
  leading/trailing White_Space; the decoded scalar sequence/string is preserved
  exactly and is never trimmed or normalized.

The explicit set is version-independent. Implementations must not replace it
with `Trim`, `IsWhiteSpace`, `\s`, `\S`, a database locale, or another
Unicode-version-dependent shorthand.

### Exact public scope

The name rule applies atomically to these four input properties:

1. `ApiKeyCreateRequest.name`;
2. `AdminUserApiKeyCreateRequest.name`;
3. `ApiKeyUpdateRequest.name`;
4. `AdminUserApiKeyUpdateRequest.name`.

The reason rule applies atomically to
`AdminUserApiKeyCreateRequest.reason`,
`AdminUserApiKeyUpdateRequest.reason`, and
`ApiKeyRotateRequest.reason`. Self/Admin API Key DELETE operations use a new
dedicated `ApiKeyChangeReason` header parameter with the same reason rule.
The shared `XChangeReason` component remains byte- and behavior-compatible for
unrelated Group, Subscription, and Supply operations.

The OpenAPI validator freezes every named pointer, both DELETE references, and
the shared-component non-change. Its behavioral tests cover exact supplementary
boundaries, all-White_Space values, C0/C1, U+2028/U+2029, isolated surrogates,
and legal leading/trailing White_Space.

### Application and database parity

The Identity Domain/Application boundary enumerates Unicode scalar values,
applies the same frozen set, and returns `validation_failed` before opening a
mutation Unit of Work for an illegal name or reason. It preserves the accepted
string exactly. Admin create/update reason is an application/audit input and is
not part of the existing PostgreSQL API Key mutation ABI; this ADR does not add
an overload merely to pass audit text to a function that does not persist it.

Forward migration `0009_identity_api_key_text_validation_m1_e5.sql` adds one
private, immutable, strict, parallel-safe validator owned by
`poolai_runtime_owner NOLOGIN`. PostgreSQL UTF-8 rejects malformed encodings and
non-scalar representations before the validator runs; `char_length` and
`substr` then operate on scalar values, including supplementary values. The
validator freezes the same 25-value White_Space set and control exclusions.

0009 replaces the 0008 length-only name/revoke-reason constraints with
validator-backed constraints and validates every existing row without rewriting
it. It replaces the exact create/update/revoke/rotate signatures under their
existing owner; create/update validate names and revoke/rotate validate reasons,
returning `validation_failed` before locks or writes. Function ABI, lock order,
post-wait database clocks, dispositions, ownership, ACLs, and every other
function-body statement remain unchanged.

### Governance state

The compatibility registry binds exactly the eleven diagnostics above. While
this ADR/window remains `Proposed`/`proposed` with null approval evidence, the
compatibility command must verify the exact base, target, and diagnostic set and
then fail with `pending approval`. This candidate is not a waiver, release
authorization, database signature, or deployment authorization.

## Alternatives considered

### Keep `Trim`/`btrim`/`\S`

Rejected. They have different whitespace tables and count/normalization
semantics, so a request can pass one layer and fail or change at another.

### Count UTF-16 code units

Rejected. It would make .NET the public length authority and contradict JSON
Schema and PostgreSQL behavior for supplementary scalar values.

### Tighten the shared `XChangeReason`

Rejected. That component is used by unrelated bounded contexts. Tightening it
would silently expand this decision and its compatibility window.

### Edit signed migration 0008

Rejected. Its approved SHA-256 is immutable. A correction must be forward-only.

### Trim legal boundary whitespace before storage

Rejected. It changes client evidence, idempotency hashes, audit text, and replay
semantics. Validation is not normalization.

## Consequences

- OpenAPI, .NET, generated clients, PostgreSQL entry points, and table
  constraints have one reviewable API Key text definition.
- Exactly 100/500 supplementary scalars are accepted even though their UTF-16
  code-unit counts are 200/1000.
- Legal leading/trailing White_Space is significant and preserved.
- Existing illegal database rows make migration 0009 fail atomically; operators
  must run a read-only preflight and resolve any data under a separately
  authorized maintenance plan before applying it.
- The public change is input-tightening and therefore requires the exact pending
  compatibility window. No approval is inferred from local green tests.

## Migration, window boundary, and rollback impact

The compatibility window applies only from base commit
`f91b11950c117c382d2a6b96ac531fa864124101` and its exact OpenAPI digest to the
target digest above. A missing or additional diagnostic fails closed.

Migration 0009 is appended after immutable migration 0008 and moves the release
manifest compatibility range to `9..9`. Before 0009 is applied, rollback is
candidate withdrawal and restoration of the previous application/manifest.
After application, rollback is forward-only: disable the new application path
or publish a corrective migration. Do not edit 0008 or 0009 after signing. This
candidate performs no remote migration and authorizes none.

## Security impact

- Control characters and line/paragraph separators cannot forge logs, headers,
  audit lines, or terminal output.
- A frozen White_Space set prevents regex/runtime upgrades from silently
  changing which audit values are non-blank.
- The database constraint is a final invariant even if a privileged maintenance
  path bypasses the application; API/Worker cannot execute the validator
  directly and retain no direct API Key DML.
- Preserving legal boundary whitespace prevents hidden normalization between
  validation, idempotency hashing, encryption AAD binding, audit, and replay.
- Invalid Unicode encoding and surrogate representations fail closed at JSON,
  UTF-8, .NET scalar enumeration, or PostgreSQL text input before persistence.

## Contract and test updates

- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/compatibility-windows-v1.json`
- `docs/release-manifest-v1.json`
- `docs/database/0009_identity_api_key_text_validation_m1_e5.sql`
- `docs/database/README.md`
- `docs/architecture/adr/README.md`
- generated TypeScript and C# OpenAPI outputs
- `tools/contracts/lib/openapi.mjs`
- `tools/contracts/lib/self-tests.mjs`
- migration catalog, PostgreSQL integration, and SQL architecture tests
