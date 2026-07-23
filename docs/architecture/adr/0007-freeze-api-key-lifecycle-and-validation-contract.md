# ADR 0007: Freeze the API Key lifecycle and validation contract

- Status: **Accepted**
- Date: 2026-07-23
- Decider: PoolAI public-contract and security owner (`@Lyon1984`); the decision takes effect only with the approval evidence below
- Relates to: M1-E5 Issue #14 and sign-off control Issue #44
- Compatibility window ID: `m1-e5-api-key-validation-contract`
- Base Git commit: `afa8519573e6965ff5611b479b1e09b00971dde3`
- Base OpenAPI SHA-256: `43b81083d47d5c228f49acd2bdbbc608df57a96df083e22cf64e2a59f65f0ada`
- Target OpenAPI SHA-256: `14380eab5b05f3b58ecb879969314868ef9cfdf23e6b6e39b3a283e211ebc58c`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5053216021)
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyCreateRequest/properties/allowed_cidrs/items/pattern: pattern changed from <none> to ^[0-9A-Fa-f:.]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$`
- Allowed diagnostic: `#/components/schemas/AdminUserApiKeyUpdateRequest/properties/allowed_cidrs/items/pattern: pattern changed from <none> to ^[0-9A-Fa-f:.]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$`
- Allowed diagnostic: `#/components/schemas/ApiKey/properties/allowed_cidrs/items/maxLength: maxLength tightened from <none> to 64`
- Allowed diagnostic: `#/components/schemas/ApiKey/properties/allowed_cidrs/items/pattern: pattern changed from <none> to ^[0-9a-f:.]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$`
- Allowed diagnostic: `#/components/schemas/ApiKey/properties/allowed_cidrs/maxItems: maxItems tightened from <none> to 50`
- Allowed diagnostic: `#/components/schemas/ApiKey/properties/allowed_cidrs/uniqueItems: uniqueItems tightened from false to true`
- Allowed diagnostic: `#/components/schemas/ApiKey/properties/prefix/pattern: pattern changed from <none> to ^sk-[A-Za-z0-9_-]{10,21}$`
- Allowed diagnostic: `#/components/schemas/ApiKey/required: property allowed_cidrs became required`
- Allowed diagnostic: `#/components/schemas/ApiKeyCreateRequest/properties/allowed_cidrs/items/pattern: pattern changed from <none> to ^[0-9A-Fa-f:.]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$`
- Allowed diagnostic: `#/components/schemas/ApiKeyCreated/properties/secret/minLength: minLength tightened from 32 to 48`
- Allowed diagnostic: `#/components/schemas/ApiKeyCreated/properties/secret/pattern: pattern changed from <none> to ^sk-[A-Za-z0-9_-]{44,55}[AEIMQUYcgkosw048]$`
- Allowed diagnostic: `#/components/schemas/ApiKeyUpdateRequest/properties/allowed_cidrs/items/pattern: pattern changed from <none> to ^[0-9A-Fa-f:.]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys/post/responses/201/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys/post/responses/201/headers/location: reference changed from #/components/headers/Location to #/components/headers/ApiKeyLocation`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/responses/204/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/responses/400: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/responses/412/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/delete/responses/412: reference changed from #/components/responses/PreconditionFailed to #/components/responses/ApiKeyPreconditionFailed`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/get/responses/200/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/patch/responses/200/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/patch/responses/412/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1users~1{userId}~1api-keys~1{apiKeyId}/patch/responses/412: reference changed from #/components/responses/PreconditionFailed to #/components/responses/ApiKeyPreconditionFailed`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys/get/responses/400: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys/post/responses/201/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys/post/responses/201/headers/location: reference changed from #/components/headers/Location to #/components/headers/ApiKeyLocation`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/responses/204/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/responses/400: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/responses/412/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/delete/responses/412: reference changed from #/components/responses/PreconditionFailed to #/components/responses/ApiKeyPreconditionFailed`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/get/responses/200/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/patch/responses/200/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/patch/responses/403: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/patch/responses/412/headers/etag: reference changed from #/components/headers/ETag to #/components/headers/ApiKeyETag`
- Allowed diagnostic: `#/paths/~1api~1v1~1me~1api-keys~1{apiKeyId}/patch/responses/412: reference changed from #/components/responses/PreconditionFailed to #/components/responses/ApiKeyPreconditionFailed`

## Context

The signed OpenAPI v1 baseline exposes API Key list, create, get, update and revoke operations for the current user and for an Admin acting on a target User. The lower-priority execution specification also requires credential rotation to create a new active Key and revoke the old Key in one transaction. No existing operation can express that atomic transition: PATCH cannot change credential or Group, while two independent create/revoke requests cannot share one Unit of Work.

Implementation review also found three security ambiguities. The database requires a positive `pepper_version`, but deployment configuration names only current and previous pepper bytes. `allowed_cidrs` has a transport shape but no canonical validation or empty-list meaning. The complete Key encoding, HMAC input and display-prefix derivation are not precise enough to verify a current/previous pepper rotation. Finally, three existing operations can receive malformed pagination or mutation headers but omit the required `400 BadRequest` response.

The M1-E5 implementation must not guess around these gaps, silently return undocumented errors, store unversioned digests, or expose a CIDR field that only appears to restrict access.

## Decision

### Public operations and lifecycle

PoolAI adds two optional v1 operations:

1. `POST /api/v1/me/api-keys/{apiKeyId}/rotate` for all four authenticated roles acting on their own Key;
2. `POST /api/v1/admin/users/{userId}/api-keys/{apiKeyId}/rotate` for Admin proxy management.

Both require `Idempotency-Key`, strong `If-Match` for the old Key, and a JSON body containing a non-blank `reason`. Success is `201 ApiKeyCreated`; `Location` and `ETag` identify the new Key and `Cache-Control` is `no-store`. The new Key copies the old Key's User, Group, name, expiration and canonical CIDR list. The old Key is revoked and the new Key is inserted in the same Identity-owned PostgreSQL Unit of Work. The full new secret exists only in this logical create response and its same-request encrypted idempotency replay.

Every self/Admin API Key item GET, successful PATCH/DELETE, create/rotate success and `412 version_conflict` returns a required strong ETag for the resource identified by that response. API Key operations use dedicated required Header/Response components rather than changing the shared optional component for unrelated resources; create/rotate additionally require Location and `Cache-Control: no-store`.

Rotation is permitted only when the old Key is not revoked, its expiration is null or later than the post-lock PostgreSQL time, and the point-in-time authorization gate observes an effective active Subscription for the immutable Group. A terminal revoked Key returns `409 api_key_revoked`; an already expired Key returns `409 resource_conflict`, and clients create a separate Key instead. A disabled but unexpired Key may rotate and produces an active new Key after the same access check. Rotation never changes `group_id` and never updates the old credential in place.

Create requires the point-in-time gate to observe an effective active Subscription and `expires_at` either null or later than PostgreSQL time. Enabling a disabled Key, moving an expired Key's expiration into the future/null, or rotating repeats that Subscription check. Missing/inactive access returns the already stable `403 subscription_required`/`subscription_inactive` for create, update and rotate. Any new mutation against a terminal revoked Key returns `409 api_key_revoked`; an expired rotate or another invalid non-terminal transition returns `409 resource_conflict`. PostgreSQL time is sampled only after any required Identity row-lock wait. Natural expiration remains derived and never writes status.

The compatibility registry permits only its exact response and schema diagnostics, including the previously omitted invalid-input responses and machine-readable credential/CIDR/strong-ETag response guarantees. The two rotate paths and their request schema are additive and consume no compatibility diagnostic.

### Credential and pepper format

Release 1 freezes this credential format:

- `ApiKeys:Prefix` matches `^sk-[A-Za-z0-9_-]{2,13}$`, defaults to `sk-pool-`, is 5..16 ASCII characters, and is immutable after external publication;
- the credential payload is exactly 32 cryptographically random bytes encoded as unpadded base64url;
- the presented secret is the configured prefix followed immediately by that 43-character payload;
- the stored digest is `HMAC-SHA256(pepper, UTF8("PoolAI:ApiKey:v1:" + presented_secret))`;
- the stored display prefix is the configured prefix plus the first eight payload characters; it is an identifier, not an authentication factor;
- plaintext credential bytes and the complete secret are cleared from temporary buffers where the runtime permits and never enter logs, traces, audit, outbox or ordinary idempotency JSON.

Configuration adds `ApiKeys:CurrentPepperVersion` and `ApiKeys:PreviousPepperVersion`. A version is `1..32767`. Current version and secret are mandatory and generate all new Key digests. Previous version and secret must both be absent or both present, must each differ from current, and are verification-only. The two pepper secrets must contain at least 256 bits and cannot reuse each other or any JWT, refresh, one-time-token, recovery-code, rate-scope, idempotency or envelope key material. Authentication selects exactly the configured secret whose version equals the row's `pepper_version`; an unavailable or unknown version fails closed and never falls back to trying every pepper.

Encrypted create/rotate replay uses Envelope purpose `idempotency-response`, `entity_type=idempotency-request-binding`, and an `entity_id` equal to lowercase SHA-256 of canonical JSON fields in this exact order: `actor_user_id`, `scope`, `idempotency_key`, `request_hash`, `response_resource_id`, `response_field`. Values are rebuilt from the current authenticated actor, normalized method+path scope, current Header and a request hash recomputed from the current canonical request; `response_resource_id` is the new Key ID. `response_field` and Envelope `field_name` are both `api_key_create_response` for create or `api_key_rotate_response` for rotate. After decryption, body `api_key.id`, Location resource ID and the bound ID must match, and ETag must match the body version. Moving ciphertext together with stored metadata across actor, target path, scope, idempotency key, request, create/rotate operation or new Key ID fails authentication, emits a security alert and never generates another credential.

### CIDR semantics

`allowed_cidrs` contains at most 50 IPv4 or IPv6 CIDRs. Input rejects invalid addresses/prefix lengths, zone identifiers, IPv4-mapped IPv6 and ambiguous IPv4 octets with leading zeroes. The server clears host bits, emits IPv4 as four no-leading-zero decimal octets and IPv6 as lowercase RFC 5952 compressed text, removes duplicates and sorts ordinally before hashing the idempotency request or persisting. On create, omission or `[]` stores the canonical empty list. On merge PATCH, omission means unchanged while explicit `[]` clears the restriction. Rotation copies the old canonical list. Every `ApiKey` response includes `allowed_cidrs`; `[]` means unrestricted by source address, never deny all.

M1-E5 persists, returns and authenticates this canonical snapshot but does not claim the later Gateway admission slice. Before any data-plane use in M4, the Gateway must compare the canonical client IP against this list after applying a deployment-configured trusted-proxy policy; untrusted forwarding headers never override the socket peer. Redis may prefilter but cannot authorize.

### Ownership and transaction boundary

API Key tables, credential functions and mutations remain Identity-owned. Forward migration 0008 may expose narrow fixed-search-path functions and revoke direct API table writes, but those functions do not read or lock GroupQuota or SubscriptionAccess tables. The cross-context control-plane gate runs through `PoolAI.Application.Orchestration` in this fixed order:

1. perform terminal idempotency replay preflight; an exact completed replay returns before resource, ETag or Subscription checks;
2. for create, take the immutable target User/Group from the authenticated command/request; for update/rotate, read an immutable owner/Group/lifecycle/version Snapshot through Identity.Abstractions, verify the path target owns the Key and return `api_key_revoked` immediately for a new mutation against terminal revoked state;
3. only when create, enable, expired-to-effective-active restoration or rotate requires access authorization, read canonical effective Subscription through SubscriptionAccess.Abstractions for that Snapshot/request User and Group; this successful point-in-time read is the authorization linearization point;
4. invoke the Identity owner command, which opens the sole UoW, locks and reloads the Key where applicable, rechecks owner/immutable Group/lifecycle/version and then atomically commits the Identity mutation, Audit and encrypted idempotency response. A changed Snapshot fails with the frozen resource/lifecycle error and never reuses the earlier read to mutate another Key.

This order makes terminal revoked precedence deterministic even when Subscription is also inactive and gives update/rotate a trusted Group without querying Subscription by path alone. The point-in-time Subscription result intentionally does not promise that access remains active until the later Identity commit; obtaining that stronger cross-context guarantee would require a separately approved ADR 0006 registry expansion. The data plane therefore always performs its own current canonical admission read before use. Orchestration owns no table, endpoint, repository or transaction, and this gate does not enlarge ADR 0006's existing database exception families.

Audit, encrypted idempotency response and both rotation mutations join the single Identity Unit of Work. Rotation records non-secret before/after facts for the old and new Key. No API Key Integration Event is invented because the frozen Identity v1 Published Language does not define one.

## Alternatives considered

### Model rotation as separate create and revoke calls

Rejected. A crash or authorization change between requests can leave both credentials active or leave the caller without the intended replacement, and the pair cannot satisfy the one-transaction state machine.

### Replace the old Key's hash in place

Rejected. It destroys credential lineage, weakens revocation evidence, conflicts with the immutable-Group/history model and cannot represent the old Key as terminal revoked.

### Keep pepper version implicit

Rejected. A previous pepper cannot be matched reliably to historical rows, and a second rotation could verify with the wrong key material.

### Store arbitrary CIDR strings until Gateway work

Rejected. It would publish a security control whose values have no stable meaning and would make later enforcement a silent input-breaking change.

### Return 400 without changing OpenAPI

Rejected. It repeats the undocumented-response problem already rejected by ADR 0005 and leaves generated clients unable to model valid failure behavior.

### Add cross-context SQL reads to migration 0008

Rejected. ADR 0006 freezes the complete exception registry. The M1 control-plane gate needs no shared transaction, while later admission owns the stronger request-time checks.

## Consequences

- M1-E5 has one implementable create/list/get/update/revoke/rotate contract for self and Admin proxy paths.
- Current and previous pepper verification is deterministic and every stored digest carries its exact version.
- CIDR values have stable storage and response semantics without prematurely claiming Gateway enforcement.
- Rotation reuses encrypted idempotency replay and exposes no read-back route for plaintext.
- Generated C# and TypeScript contracts plus the release manifest carry the target OpenAPI digest.
- With this ADR and registry entry `Accepted`/`accepted`, the exact compatibility command consumes only the pinned thirty-four diagnostics; any base, target or diagnostic drift fails closed.

## Migration, window boundary, and rollback impact

The compatibility window applies only from base commit `afa8519573e6965ff5611b479b1e09b00971dde3` and its OpenAPI digest to the exact target digest above. Any missing or additional diagnostic fails closed. Approval changes only the ADR/registry status and permanent evidence markers. After the protected target merges, it is the ordinary v1 baseline and this window is inert.

Database implementation must use a new immutable forward migration `0008_identity_api_keys_m1_e5.sql`; migrations 0001..0007 are unchanged. Before migration 0008 is executed, rollback is code/contract withdrawal and restoring the previous manifest. After execution, rollback is forward-only: disable the new application path and publish a corrective migration rather than editing history. No remote migration, deployment or release is authorized by this ADR.

## Security impact

- Only HMAC digest, display prefix, pepper version and non-secret metadata persist in `api_keys`.
- The application role loses direct API Key writes and receives only exact function execution needed by the state machine.
- Secret create/rotate replay uses versioned AEAD with request/resource-bound AAD; ordinary JSON, audit, events and telemetry remain secret-free.
- Strict CIDR normalization prevents ambiguous host bits, duplicate forms, scoped IPv6 identifiers and mapped-address policy bypasses.
- Fixed lock ordering, post-wait database time and strong old-Key If-Match prevent lost updates and stale rotation.

## Contract files updated and later implementation evidence

This accepted contract decision updates:

- `.gitleaks.toml` with an exact-path, exact-digest public-hash allowlist
- `docs/README.md` and `docs/architecture/adr/README.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/error-catalog.md`
- `docs/contracts/compatibility-windows-v1.json`
- `docs/系统重构方案-v1.0.md`
- `docs/开发执行规格-v1.0.md`
- `docs/database/README.md`
- `docs/traceability/release-1-traceability.json`
- `tools/contracts/lib/openapi.mjs`, compatibility comparison, and their self-tests
- deterministic generated C# and TypeScript contract artifacts
- `docs/release-manifest-v1.json`

The subsequent M1-E5 implementation must add forward migration
`0008_identity_api_keys_m1_e5.sql` plus API Key credential, CIDR, lifecycle,
permission, PostgreSQL-clock, idempotency-secret replay, RBAC, audit and
AC-006/007 evidence. Those runtime, migration and test artifacts are not present
or approved merely because this ADR is accepted.
