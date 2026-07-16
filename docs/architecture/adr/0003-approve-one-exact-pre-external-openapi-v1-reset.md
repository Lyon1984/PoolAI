# ADR 0003: Approve one exact pre-external OpenAPI v1 compatibility reset

- Status: **Accepted**
- Date: 2026-07-17
- Deciders: PoolAI public-contract baseline owner (`@Lyon1984`)
- Relates to: M1-E1 Issue #10 and sign-off control Issue #44
- Reset ID: `m1-e1-pre-release-mailbox-and-list-query`
- Base Git commit: `b1b329fac954ff22c94e6c317a81f56ea5e9c449`
- Base OpenAPI SHA-256: `19f01348d83d03c81fa7146586cfdcf9e40edcb3cc12f2bc8a2c1bf9fb9dc3b2`
- Target OpenAPI SHA-256: `e2b0d295eff895cbe2608aee4c8e702df17afb791138b6d2c1338c7af35d441c`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)

## Context

The M0 OpenAPI v1 baseline was signed before any M1 endpoint or runtime implementation was accepted. During the first M1-E1 implementation, executable validation exposed two mismatches between that paper baseline and the required Identity behavior:

1. `adminListUsers` must reject an invalid `limit` or malformed pagination cursor with the existing 400 `BadRequest` response, but the signed operation omitted that status.
2. Login, forgot-password, and Admin user-create must share one SMTP-compatible canonical mailbox input. The M0 inline `type: string`, `format: email`, `maxLength: 320` schemas are broader and less precise than the Identity normalization and delivery boundary.

The repository contains no release-candidate, GA, production, or external-consumer acceptance evidence for M1 or these endpoints. Silently moving the Git comparison base or disabling the compatibility checker would erase the signed transition. Publishing `/v2` before the first implementation would retain that evidence but create two public versions before either had external release evidence. The transition therefore needs a narrowly auditable pre-external-release decision that cannot become a reusable ignore list.

Two documentation-only `request_id` `allOf` wrappers initially appeared in the compatibility diagnostics. They are unnecessary and are reverted. The `X-Request-Id` clarification remains documentation-only and is not covered by this reset.

## Decision

PoolAI accepts one and only one pre-external-release OpenAPI v1 compatibility reset for M1-E1. It applies only when all of the following are simultaneously true:

- the comparison base is Git commit `b1b329fac954ff22c94e6c317a81f56ea5e9c449`;
- the base OpenAPI SHA-256 is `19f01348d83d03c81fa7146586cfdcf9e40edcb3cc12f2bc8a2c1bf9fb9dc3b2`;
- the target OpenAPI SHA-256 is `e2b0d295eff895cbe2608aee4c8e702df17afb791138b6d2c1338c7af35d441c`; and
- the compatibility checker produces exactly the 13 sorted diagnostics in `docs/contracts/compatibility-resets-v1.json`.

The registry uses strict exact keys, full digests, one exact base commit, and complete diagnostic strings. Wildcards, prefix or substring matching, duplicate entries, partial matches, missing registered failures, additional failures, stale digests, another base, or another target fail closed. The registry is an immutable record of this exact transition, not a general compatibility exception.

The approved semantic scope is limited to:

1. `GET /api/v1/admin/users` (`adminListUsers`) adds the existing `BadRequest` response at status 400 for an invalid `limit` or malformed pagination cursor.
2. `LoginRequest.email`, `ForgotPasswordRequest.email`, and `UserCreateRequest.email` replace the previous inline `{ type: string, format: email, maxLength: 320 }` input with `MailboxInput`.
3. `MailboxInput` accepts only a canonical mailbox of at most 254 characters, with an ASCII dot-atom local part of at most 64 characters and an IDNA/STD3-normalized lowercase ASCII DNS domain. Display-name forms, quoted local parts, comments, domain literals, control characters, and non-ASCII local parts are rejected.

No response schema, other response status, operation, stable error code, SSE fixture, or other email surface is covered. Forgot-password continues to use the same non-enumerating public behavior. The reset does not authorize a runtime behavior that disagrees with the target OpenAPI.

After the protected change is merged, the target document becomes the ordinary compatibility baseline. The registry entry remains historical evidence. A later comparison base or target cannot match its bindings, so all subsequent changes return to the normal `/v1` compatibility rule.

This ADR does not amend or re-sign DEC-001..042, ADR 0001 or ADR 0002, the M0 database approval for migrations 0001..0003, the M0 OpenAPI approval for SHA-256 `19f01348d83d03c81fa7146586cfdcf9e40edcb3cc12f2bc8a2c1bf9fb9dc3b2`, or the M0 Exit approval. Those records remain immutable evidence of their original scopes. The target M1-E1 contract requires separate incremental OpenAPI evidence and does not claim M1 runtime acceptance, release-candidate promotion, GA, or production acceptance. Migration `0004_identity_m1_e1.sql` and its incremental database sign-off remain independently governed.

## Alternatives considered

### Publish `/v2` immediately

Rejected for this exact pre-external transition. It would preserve compatibility mechanically but create and maintain a second public version before the first Identity implementation has release or external-consumer evidence. Any later breaking change after this transition still requires the normal compatibility-window or `/v2` process.

### Keep the broad M0 email schemas or hide stricter runtime behavior

Rejected. The public contract would accept inputs that the Identity and SMTP boundary rejects, defeating contract-first implementation and making client behavior unpredictable.

### Remove the Admin list 400 response

Rejected. Invalid pagination input needs the existing explicit control-plane error response; returning an undocumented status or coercing malformed input would weaken the contract.

### Disable, loosen, or silently rebase the compatibility checker

Rejected. A broad allowlist, wildcard, arbitrary base override, or moved base could authorize unrelated changes and would destroy the signed audit trail established by the M0 baseline.

## Consequences

- The target contract accurately describes the implemented Identity mailbox boundary and Admin user-list validation.
- CI may consume exactly 13 registered compatibility diagnostics only for the pinned base/target transition; stale, extra, or missing diagnostics stop the build.
- The first pull-request comparison and its corresponding first `main` transition may evaluate the same exact record. Future bases cannot use it.
- Generated C# and TypeScript DTOs and the release manifest must carry the target OpenAPI digest.
- The M0 approval records remain unchanged; a separate post-merge incremental OpenAPI sign-off must cite the protected verification evidence.
- Reviewers must treat any edit to the ADR or registry as a public-contract governance change, not routine test maintenance.

## Migration and rollback impact

This decision authorizes no data rewrite and does not change ownership of PostgreSQL migration `0004_identity_m1_e1.sql`. Before merge, rollback consists of withdrawing this candidate and restoring the signed M0 OpenAPI shapes or choosing `/v2`/an explicit compatibility window.

After merge, this ADR and registry entry must not be deleted or rewritten to hide history. The target becomes the normal baseline, and any subsequent incompatible change follows the standard `/v1` compatibility-window or `/v2` process. If external usage evidence is discovered before merge, this reset must be abandoned and the versioned migration path used instead.

## Security impact

- The mailbox boundary rejects CR/LF and other control characters, display-name/comment forms, domain literals, quoted local parts, and Unicode local-part ambiguity before persistence or SMTP composition.
- IDNA/STD3 normalization and lowercase ASCII DNS comparison reduce visually ambiguous or non-canonical domain identities; the normalized 254-character limit aligns persisted identity with deliverability constraints.
- Forgot-password response semantics remain non-enumerating, so the input correction does not expose whether an account exists.
- The new Admin 400 response uses the existing closed problem schema and stable error catalog rather than leaking parser or cursor internals.
- No credentials, tokens, connection material, private hosts, or production evidence are recorded in the reset registry or ADR.

## Contract and test files updated

This decision is complete only when these assets agree in one protected change:

- `docs/contracts/openapi-v1.yaml` and deterministic generated C#/TypeScript DTOs;
- `docs/contracts/compatibility-resets-v1.json`;
- `docs/release-manifest-v1.json`;
- `tools/contracts/lib/compatibility.mjs`, its self-tests, CLI, and tooling documentation;
- contract, architecture, integration, and end-to-end tests covering mailbox normalization, Admin pagination failures, non-enumeration, and exact generated shapes; and
- project memory and architecture indexes linking this decision without replacing its text.
