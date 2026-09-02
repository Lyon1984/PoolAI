# ADR 0016: Add the Gateway credential-revision column read

- Status: **Accepted**
- Date: 2026-09-02
- Decider: PoolAI architecture, Gateway, Supply, database, and security owner (`@Lyon1984`)
- Relates to: [M4-E1 Issue #24](https://github.com/Lyon1984/PoolAI/issues/24), ADR 0009, ADR 0010, ADR 0011, ADR 0015, migration 0010, migration 0019, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Amends: only ADR 0015's statement that the revision-fenced handoff requires no database-permission change
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue #44 permanent ADR approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5508959500), binding candidate `bf1ef153cf0ade650fb47815d76ad4f88e07a525`, tree `f7b3bccc36d127077fcbeaa6c93e2310c242843c`, and pre-evidence-backwrite ADR SHA-256 `b06ee15291e7282596b4083cc281004306e40908f3199b6440c0ee3c2e3ba978`
- Independent database approval evidence: [Issue #44 permanent migration 0020 approval](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5508962022), binding candidate `bf1ef153cf0ade650fb47815d76ad4f88e07a525`, tree `f7b3bccc36d127077fcbeaa6c93e2310c242843c`, SQL SHA-256 `cb2249c7c3a62e8f43ce9dd1a6cfd56461cce7f452a71167e8cf9d9f873c06b0`, release manifest SHA-256 `6643f0bcbc2682a3011ba8c82639e9232ec74b4bc3ea5a070488623bd42db9e5`, PostgreSQL major `18`, and compatibility `20..20`

## Context

ADR 0015 correctly requires every M4-E1 route to bind the selected Account
`credential_revision`, and requires credential acquisition to recheck that same
revision before decrypting. It also states that this handoff changes no database
permission. The implementation and a real PostgreSQL 18 role test disproved that
last assumption.

Migration 0003 granted the co-hosted `poolai_api` role an explicit column-level
Account read set. Migration 0010 later added `accounts.credential_revision` and
granted that column to `poolai_runtime_owner` and `poolai_worker`, but not to
`poolai_api`. PostgreSQL does not extend an existing column-level grant when a
later migration adds a column. Consequently, both the non-secret route snapshot
read and the revision-fenced credential-lease read fail under the real API role
with SQLSTATE `42501` on schema 19.

The signed migrations 0010 and 0019 are immutable. Bypassing the revision check,
reading under the Migrator or NOLOGIN owner, moving the query to Worker, or
granting table-level Account SELECT would either violate ADR 0015 or widen the
database security boundary. The correction therefore needs one new forward
migration and an independently reviewed architecture decision.

## Decision

Create forward migration
`0020_gateway_credential_revision_permission_m4_e1.sql`. Its only positive
runtime grant is:

```sql
GRANT SELECT (credential_revision)
    ON public.accounts TO poolai_api;
```

The permission is narrowly justified by the already accepted, revision-fenced
M4-E1 route and credential handoff. `credential_revision` is a positive internal
CAS value; it is not a credential, envelope, key, authorization value, or public
response field. The existing Account credential-envelope read remains the
separately accepted co-hosted Gateway exception. This ADR neither enlarges that
exception nor makes the revision public.

Migration 0020 must fail closed unless all of the following remain true:

- `poolai_api` receives `SELECT` on exactly the complete schema-20 Account column
  set through column ACLs, with no table-level Account privilege and no grant
  option;
- `poolai_api` receives no Account `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`,
  `REFERENCES`, or `TRIGGER` privilege, including column-level write/reference
  privileges;
- the Account relation has exactly the columns established by migrations
  0001–0010 and no additional column;
- the `credential_revision` column ACL remains limited to the previously
  authorized runtime-owner/Worker capabilities plus the one new API SELECT;
- the three runtime roles retain their non-elevated attributes, the owner remains
  NOLOGIN, API/Worker have no role membership, and none receives `public` schema
  CREATE; and
- migration 0020 creates or changes no table, column, constraint, index,
  function, function ACL, role, membership, or write permission.

The release manifest advances atomically to PostgreSQL compatibility `20..20`
and embeds the exact migration checksum. The schema-20 Api/Worker build is not
ready against schema 19, and the schema-19 build rejects schema 20 as an unknown
future version. Any rollout therefore uses the existing drained, fenced,
forward-only cutover discipline.

This decision does not alter the route facts, credential lease lifetime,
credential decryption rules, authority/SSRF fence, Adapter boundary, Group quota,
Group RPM, Account lease, OpenAPI, error catalog, Redis key/script ABI, or any
public response.

The exact ADR and migration/checksum/manifest candidates have independent
permanent approvals linked above. Those approvals make the accepted decision and
SQL bytes immutable, but do not authorize applying a database migration, using a
real credential, merging a release, deploying, or closing M4-E1.

## Alternatives considered

### Edit migration 0010 or 0019

Rejected. Both migrations are independently signed and checksum-bound. Their
bytes remain immutable; corrections must be forward-only.

### Omit the revision from the Gateway reads

Rejected. That removes the accepted credential replacement/rewrap fence and can
bind a route snapshot to a different credential generation.

### Grant table-level SELECT on `accounts`

Rejected. Future Account columns would become readable automatically, defeating
the explicit least-privilege boundary that exposed this defect.

### Add a new SECURITY DEFINER getter

Rejected. A function would add a new executable capability and owner/search-path
surface when one non-secret column grant is sufficient. It would not reduce the
existing co-hosted Gateway credential access.

### Run the Gateway query as Worker or Migrator

Rejected. Worker does not own the request path, and the Migrator must never be a
runtime dependency or privilege bridge.

## Consequences

- The M4-E1 route snapshot and credential acquisition can both compare the
  current Account credential revision under the real `poolai_api` role.
- Schema 20 has a single auditable privilege delta and no storage, function,
  role, membership, OpenAPI, or Redis delta.
- Existing schema-19 deployments require the normal forward migration before a
  schema-20 build can become ready; there is no mixed schema-19/schema-20 binary
  compatibility promise.
- The strong audit intentionally treats unexpected Account columns or ACL drift
  as a migration failure requiring an independently reviewed later correction.

## Migration and rollback impact

Migration 0020 is additive and forward-only. Before execution, rollback is to
withhold the accepted migration. After execution, its bytes/checksum are
immutable and rollback uses a later governed forward migration; migration 0010
or 0019 is never edited. A post-migration schema-19 binary cannot be restored to
service because its manifest rejects the future version.

This ADR and migration perform no remote database operation, data repair,
credential read/decrypt/rotation/rewrap, Redis mutation, upstream request,
deployment, RC, GA, or production acceptance.

## Security impact

The change restores the accepted TOCTOU/revision fence without exposing secret
material or establishing an owner-privilege bridge. Column-level SELECT prevents
future Account fields from being inherited silently. The in-migration audit and
catalog-delta tests reject table-wide access, write/reference/grant-option access,
unexpected grantees, elevated roles, memberships, schema CREATE, new functions,
and any privilege change other than the one exact SELECT.

The value may be used only inside the immutable route/credential comparison. It
must not appear in public DTOs, logs, traces, problem details, or audit metadata
unless a separately governed non-secret diagnostic contract explicitly allows
it.

## Coupled contract and test files

- `docs/database/0020_gateway_credential_revision_permission_m4_e1.sql` and
  `docs/database/README.md` define the forward permission correction and its
  PostgreSQL 18 acceptance evidence.
- `docs/release-manifest-v1.json` and
  `src/PoolAI.Database.Migrations/PoolAI.Database.Migrations.csproj` bind schema
  compatibility `20..20`, checksum, and the embedded authoritative SQL bytes.
- `tests/PoolAI.IntegrationTests/PostgresMigrationTests.M4E1CredentialRevisionPermission.cs`
  must reproduce both schema-19 SQLSTATE `42501` failures, prove both schema-20
  reads, and prove the exact catalog privilege delta.
- `tests/PoolAI.ArchitectureTests/DatabasePermissionCorrectionBoundaryTests.cs`,
  `tests/PoolAI.ArchitectureTests/CrossContextSqlBoundaryTests.cs`,
  `tests/PoolAI.ArchitectureTests/DependencyBoundaryTests.cs`, and
  `tests/PoolAI.IntegrationTests/MigrationCatalogTests.cs` must freeze the
  one-grant/no-DDL/no-function/no-role shape and embedded manifest/catalog bytes.
- OpenAPI, fixtures, error catalog, Redis contract/scripts, and their manifest
  entries remain byte-for-byte unchanged.
