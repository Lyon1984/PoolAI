# ADR 0009: Introduce a shared BCL-only secrets-envelope runtime

- Status: **Accepted**
- Date: 2026-07-29
- Decider: PoolAI architecture and security owner (`@Lyon1984`)
- Relates to: DEC-042, AC-044, [M2-E1 Issue #15](https://github.com/Lyon1984/PoolAI/issues/15), and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Base Git commit: `e09b7ecbf856bdec46888e8a1e985ea910f9b8ba`
- Approved candidate head: `f6caccc00d5c2708f4378dcc15beb9f79543f8c1`
- Approved quality-gate run: [30446932289](https://github.com/Lyon1984/PoolAI/actions/runs/30446932289)
- Approved security-evidence run: [30446932286](https://github.com/Lyon1984/PoolAI/actions/runs/30446932286)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue #44 approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5117402125)

## Context

DEC-042 freezes one Envelope v1 contract for every reversible secret: strict
versioned AEAD, a random content DEK, KEK wrapping, exact `kid` resolution,
purpose/entity/field AAD binding, historical-key reads, fail-closed parsing and
authentication, CAS rewrapping, and backup/restore evidence before an old key
can be removed.

Identity currently has an internal implementation for its M1
`email-delivery-secret`, `totp-secret`, and `idempotency-response` use cases.
That implementation is deliberately inaccessible to Supply. M2-E1 must add
`account-credential`, but Supply cannot reference the Identity implementation
without violating the module DAG, and a second independent implementation
would allow the frozen wire, parser, key-resolution, and rewrap semantics to
drift.

`PoolAI.BuildingBlocks` is not the right extraction target. It is intentionally
limited to vendor-neutral primitives used by Domain, Application, and
Abstractions. Placing key material and cryptographic mechanics there would make
them transitively visible to layers that must not handle secrets.

The missing boundary is a small technical runtime shared only by the
Infrastructure adapters that own reversible-secret use cases. It is not a
bounded context, configuration provider, secret store, or operational workflow.

## Decision

The permanent Issue #44 approval accepts the following exact architectural
boundary. It does not authorize the separately governed database migration,
remote execution, merge, release, or key operations.

### Shared technical project

1. Add `PoolAI.Infrastructure.Secrets` as a non-bounded-context, **BCL-only**
   production project. It has no project reference, framework reference,
   compile/runtime NuGet asset, project-specific source generator, or runtime
   dependency beyond the .NET Base Class Library. The repository-wide
   `PrivateAssets=all` analyzer packages remain build-only and must contribute
   no compile/runtime/native asset to its effective graph.
2. The project owns only generic Envelope v1 mechanics:
   - strict bounded parsing and canonical serialization of `v`, `alg`, `kid`,
     `wrapped_dek`, `wrap_nonce`, `wrap_tag`, `ciphertext`, `nonce`, and `tag`;
   - unpadded base64url conversion and fixed algorithm/version checks;
   - CSPRNG generation of a fresh 256-bit DEK, content nonce, and wrap nonce for
     each encryption;
   - AEAD content encryption and KEK wrapping;
   - exact-`kid` decryption against an immutable current-plus-historical
     keyring with bounded valid-Unicode identifiers, without trying every key
     or accepting replacement-character normalization;
   - authenticated rewrap that preserves content ciphertext while generating a
     fresh wrap nonce/tag and returning a replacement envelope for the caller's
     CAS write.
3. The runtime accepts the generic purpose/entity-type/entity-id/field-name
   components needed to encode the frozen canonical AAD. It owns no business
   purpose catalog, Account/User/idempotency binding rule, authorization
   decision, or field-name constant. Identity and Supply remain responsible for
   deriving and validating those business components before calling the
   runtime.
4. Unknown or duplicate fields, unsupported `v`/`alg`, malformed or oversized
   values, an unknown `kid`, AAD mismatch, or either authentication-tag failure
   fails closed. The runtime neither retries with other keys nor attempts
   plaintext or legacy-format compatibility.
5. Temporary DEKs, plaintext, decoded key material, and other sensitive buffers
   are cleared on every success and failure path where the BCL permits. Public
   failures contain only stable non-secret classifications; they do not include
   key bytes, plaintext, ciphertext, tags, nonces, full AAD, or serialized
   envelopes.

### Deliberately excluded responsibilities

`PoolAI.Infrastructure.Secrets` contains no:

- `IConfiguration`, Options binding, environment/file/secret-provider loading,
  service registration extension, or mutable global key registry;
- logger, metric, trace, alert writer, health check, readiness policy, or
  configuration-summary projection;
- business AAD constants/builders, entity lookup, authorization, Repository,
  DbContext, SQL, Redis, filesystem/object-store access, or backup inventory;
- background worker, retry scheduler, row claim, database CAS, key-rotation
  orchestration, restore command, or runbook;
- ASP.NET Core transport type, module type, `PoolAI.Contracts`,
  `PoolAI.BuildingBlocks`, vendor SDK, or general-purpose encryption API.

The owning module's Infrastructure adapter receives already validated secret
configuration from its module registration boundary, constructs the immutable
runtime inputs, translates module-specific AAD, and maps safe failure
classifications to its Application port. Operations-facing logging and alerts
remain outside the core and must be redacted.

### Allowed consumers and Host boundary

1. Only the `Infrastructure` namespaces of
   `PoolAI.Modules.Identity` and `PoolAI.Modules.Supply`, plus tests, may
   reference `PoolAI.Infrastructure.Secrets`.
2. Domain, Application, Endpoints, every `*.Abstractions` project,
   `PoolAI.Contracts`, `PoolAI.BuildingBlocks`,
   `PoolAI.Application.Orchestration`, adapters, and all executable Hosts may
   not reference or use the project directly.
3. Api and Worker load the owning Identity/Supply module implementation and
   provide validated configuration through those module registration
   boundaries. Their Composition Roots do not register or resolve the secrets
   runtime directly.
4. `PoolAI.Migrator` may neither reference nor load the project. It remains the
   schema owner and never decrypts Account credentials, performs application
   rewrap, or becomes a key-management command.
5. A module implementation project may carry the physical project reference
   because its logical layers currently share an assembly, but Architecture
   Tests must prove that only its `Infrastructure` namespace uses runtime types.

### Rotation, CAS, backup, and restore ownership

The runtime provides cryptographic transformation only. The owning module
selects records, supplies exact business AAD, and persists a returned rewrapped
envelope using a record version or exact-old-envelope CAS. A zero-row CAS result
is a concurrency miss, not permission to overwrite a newer envelope.

Rotation remains an operational sequence: add the new KEK to every required
runtime keyring, switch `current`, rewrap through the owning module's bounded
worker, inventory live data and retained backups, restore into an isolated
environment, prove all required `kid` values decrypt with exact AAD, and only
then authorize historical-key removal. Neither this ADR nor the shared core
authorizes remote configuration mutation, a database write, or key deletion.

### Supply persistence and maintenance-CAS boundary

The following architecture boundary is accepted, while its database bytes
remain a separately governed candidate that requires independent database
approval:

1. Supply owns Account credential creation, replacement, selection, and
   maintenance rewrap. Its Application ports remain internal and vendor-neutral;
   only Supply Infrastructure contains Npgsql, SQL, Envelope runtime calls, and
   the concrete Repository.
2. A forward migration adds a positive, non-public
   `accounts.credential_revision`. Human credential creation starts both the
   public Account `version` and the internal credential revision at one. Human
   replacement advances both exactly once, updates the public timestamp and
   non-secret prefix/hint, and resets credential-dependent health to `unknown`.
3. Authenticated maintenance rewrap compares only the internal credential
   revision and advances it exactly once. It changes only
   `credential_envelope`; it does not change the public Account `version`,
   `updated_at`, prefix/hint, lifecycle, cooldown, or health. This prevents a
   technical KEK change from manufacturing an ETag conflict or racing unrelated
   health updates.
4. The existing `poolai_runtime_owner NOLOGIN` may own the fixed
   `SECURITY DEFINER` create/replace/rewrap entry points and receive only their
   exact INSERT/UPDATE columns. It remains unable to `SELECT`
   `accounts.credential_envelope`. `poolai_worker` may read encrypted Account
   credentials and execute the bounded selector/CAS entry points, but receives
   no direct envelope or credential-revision UPDATE. `poolai_api` loses direct
   Account INSERT and credential UPDATE and may execute only the bounded human
   write entry points. The existing read-only Configuration/binding trigger
   guards move to fixed-search-path `SECURITY DEFINER` functions owned by the
   same NOLOGIN role, so they can retain their Account `FOR SHARE` validation
   without granting API a direct `UPDATE(id)` capability over stable Account
   identity; PUBLIC/API/Worker cannot execute those trigger functions directly.
5. A database guard requires an envelope change to advance
   `credential_revision` exactly once. In maintenance-rewrap mode it also
   requires `ciphertext`, content `nonce`, and content `tag` to remain unchanged;
   a Worker cannot use the maintenance entry point as a credential-replacement
   path.
6. The selector keyset-pages every retained Account by primary key, including
   retired rows. It does not filter on the JSON `kid`: every envelope is parsed
   and authenticated by the strict runtime, so malformed, unknown-key, copied,
   or tampered current-key rows cannot disappear behind a database expression
   filter.
7. `PoolAI.Worker` explicitly opts into a default-disabled, one-shot Supply
   rewrap service. Supply uses the existing Operations session advisory-lock
   port only for single-owner liveness. It performs selection, cryptography,
   lock verification, alert delivery, and retry delay outside transactions;
   each final CAS has one short PostgreSQL Unit of Work. A miss is reread and
   may be recomputed only within a fixed bound.

The public Account create/update use cases, authorization, idempotency, audit,
combined non-secret mutation, and HTTP evidence remain M2-E2 work. In
particular, the replacement seam in this candidate must not be composed with a
second generic Account UPDATE that would advance the public version twice.

## Alternatives considered

### Keep separate Identity and Supply implementations

Rejected. Two parsers, serializers, key resolvers, and rewrap implementations
would make one frozen envelope contract depend on cross-module code review
discipline and could diverge at exactly the fail-closed boundary.

### Move the implementation into `PoolAI.BuildingBlocks`

Rejected. BuildingBlocks is visible to Domain, Application, and Abstractions
and is reserved for small vendor-neutral primitives. Keyring and AEAD mechanics
would widen secret access and turn a foundational project into infrastructure.

### Let Supply reference Identity

Rejected. Identity does not own Account credentials, and a Supply-to-Identity
implementation reference violates the module dependency rule and creates a
false business ownership relationship.

### Use ASP.NET Core Data Protection as the stored wire format

Rejected. It does not preserve the frozen explicit Envelope v1 fields,
caller-owned business AAD contract, exact `kid` inventory, wrapped-DEK
rewriting, or cross-Host backup/restore evidence required by DEC-042.

### Put configuration, persistence, and rotation orchestration in the shared project

Rejected. That would create a secret-management service with broad Host and
database authority. The shared project is intentionally a pure cryptographic
mechanism; modules and Operations retain policy and workflow ownership.

## Consequences

- Identity and Supply can use one strict wire implementation without referencing
  each other's implementation.
- The Solution gains one production project and a narrow, testable exception at
  the module Infrastructure layer.
- Business AAD construction, persistence, CAS ownership, alerting, and runbooks
  remain explicit in the owning module or Operations rather than leaking into a
  generic core.
- Api/Worker continue to compose modules without gaining direct secret-runtime
  access, and Migrator remains unable to decrypt application secrets.
- The existing Identity implementation must be replaced through behavior- and
  fixture-equivalent tests; merely moving source files does not complete
  M2-E1's rewrap and backup/restore acceptance.
- BCL-only avoids a new cryptographic package supply chain, but it requires
  Architecture Tests to keep the dependency surface at zero.

## Migration and rollback impact

This candidate adds a forward-only Account credential revision and bounded
database entry points, plus a default-disabled Worker registration. It changes
no OpenAPI document or Envelope v1 bytes and does not authorize a remote
migration, configuration rollout, rewrap, restore, retirement, deployment, or
key operation. The migration SQL, checksum, manifest window, owner/ACL surface,
and PostgreSQL evidence require their own permanent database approval; approval
of this ADR cannot substitute for it.

Before release, extraction from Identity is rollbackable by removing the new
project references and restoring the internal implementation in one atomic
code change. After Supply persists Account credential envelopes, rollback may
replace the runtime only with a byte- and behavior-compatible implementation;
it must not rewrite envelopes to plaintext, drop historical keys, bypass CAS,
or remove a key still referenced by live data or backups.

## Security impact

- The direct consumer allowlist reduces the number of assemblies that can
  handle reversible secrets and key material.
- Exact `kid` lookup, authenticated AAD, strict parsing, and no legacy fallback
  preserve the fail-closed boundary for copied or tampered envelopes.
- Keeping configuration, logging, persistence, and business identifiers outside
  the core prevents it from becoming a broad secret exfiltration or database
  authority.
- Rewrap authenticates the existing envelope and uses a fresh wrap nonce/tag;
  module-owned CAS prevents a stale worker from overwriting concurrent changes.
- Old-key removal remains gated by live-data inventory and an isolated backup
  restore exercise, not by an in-memory keyring observation alone.
- Tests and diagnostics must never snapshot plaintext, KEKs, DEKs, serialized
  production envelopes, or private host configuration.

The candidate's threat analysis is explicit about what is proved locally and
what remains a release gate:

| Threat | Required control | Current evidence or remaining gate |
|---|---|---|
| KEK disclosure or inconsistent rollout | immutable exact-`kid` keyrings; deploy new key to every reader before switching `current`; retain rollback keys | configuration/keyring tests and the Operations runbook are local evidence; production KMS and rollout evidence remain M6 |
| Envelope copied across purpose/entity/field | rebuild canonical AAD from trusted business context and authenticate both AEAD layers | unit/integration tests cover wrong purpose, Account, and field |
| parser/resource abuse or downgrade | exact field set, bounded canonical base64url, fixed lengths and size, fixed `v/alg`, no legacy fallback | strict negative unit tests and Architecture Tests cover the shared runtime |
| tag/ciphertext/DEK tampering | authenticate the wrapped DEK and content before decrypt, inspect, or rewrap; expose only stable redacted failure classes | unit/integration tamper tests and Supply alert-payload tests |
| stale rewrap overwrites credential replacement | owning-module CAS on the non-public credential revision; human replacement and maintenance rewrap both advance it once; zero-row update rereads instead of overwriting | the forward migration, production Supply selector/CAS, bounded reread and crash/retry integration are candidate evidence; database approval and an authorized execution remain required |
| backup cannot decrypt after rotation | inventory every retained backup, restore in isolation with the required historical ring, and validate exact trusted AAD | local serialization/restore behavior is covered; physical PostgreSQL/PITR and RPO/RTO evidence remain M6-E4 |
| historical key removed too early | require zero live references, expired or proven retained backups, DR agreement, observation window, and separate retirement approval | the reviewed runbook defines the gate; an executed retirement record is not claimed |
| plaintext/key material leaks through failures | zero temporary byte buffers where supported; exclude secret values, `kid`, AAD, and envelopes from failure payloads/logs/traces | Supply event tests cover redaction; production observability verification remains required |

## Contract and test evidence

The architecture approval reviewed the candidate assets below. Independently
governed database approval and physical restore evidence are not implied by the
ADR's `Accepted` status and remain gates for M2-E1 or release completion.

- `docs/architecture/adr/README.md`
- `docs/architecture/design-pattern-baseline.md`
- `docs/开发执行规格-v1.0.md`
- the threat analysis in this ADR for key compromise, envelope swapping, parser
  abuse, stale rewrap, backup restore, and historical-key removal
- Architecture Tests for the exact consumer/namespace allowlist, BCL-only
  dependency surface, no direct Host/Migrator reference, and the excluded
  configuration/logging/business-AAD/storage responsibilities
- unit/contract fixtures proving byte-compatible Identity behavior, strict
  parsing, exact AAD, exact `kid`, fresh randomness, buffer cleanup, and safe
  failures
- real PostgreSQL integration tests for Supply persistence, concurrent CAS
  rewrap, crash/retry behavior, and no plaintext in database/log/trace output
- a separately governed forward migration candidate proving the internal
  credential revision, exact function owner/search path/ACL, rewrap
  content-preservation guard, and denial of direct API/Worker envelope writes;
  its independent database approval remains required before M2-E1 completion
- the operator-reviewed
  [`ops/runbooks/secret-envelope-key-rotation-and-restore.md`](../../../ops/runbooks/secret-envelope-key-rotation-and-restore.md)
  for key rotation, inventory, backup restore, rollback, and AC-044 evidence

The permanent `@Lyon1984` Issue #44 approval accepts this architecture only.
It does not approve migration 0010, remote database execution, PR readiness or
merge, deployment, rotation, rewrap, restore, historical-key removal, M2-E1
completion, M2 Exit, or any release or production acceptance.
