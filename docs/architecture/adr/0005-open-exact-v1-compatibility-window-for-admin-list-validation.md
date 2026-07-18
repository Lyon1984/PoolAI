# ADR 0005: Open an exact v1 compatibility window for Admin list validation

- Status: **Accepted**
- Date: 2026-07-17
- Decider: PoolAI public-contract owner (`@Lyon1984`); the decision takes effect only with the approval evidence below
- Relates to: M1-E4 Issue #13 and sign-off control Issue #44
- Compatibility window ID: `m1-e4-admin-list-validation-responses`
- Base Git commit: `5ef07fcc762ed0d11d79eeee3012967d1eac6121`
- Base OpenAPI SHA-256: `d8887b322bebc575005a2b170be6a17cf002de94154c94cbfa8a729bf091f076`
- Target OpenAPI SHA-256: `43b81083d47d5c228f49acd2bdbbc608df57a96df083e22cf64e2a59f65f0ada`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5010008464)
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1groups/get/responses/400: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1subscription-templates/get/responses/400: new response status was added to an existing operation`
- Allowed diagnostic: `#/paths/~1api~1v1~1admin~1subscriptions/get/responses/400: new response status was added to an existing operation`

## Context

M1-E4 implements the three frozen Admin pagination queries `adminListGroups`,
`adminListSubscriptionTemplates`, and `adminListSubscriptions`. Their runtime input
boundary rejects an invalid `limit`, a malformed cursor, and (for Subscription list)
an invalid UUID filter with the existing control-plane `BadRequest` response. The
frozen OpenAPI operations declare those query inputs but omit status 400. Returning
an undocumented 400 or silently correcting invalid input would contradict the
contract-first rule and ADR 0003; removing strict runtime validation would make
pagination and filtering ambiguous.

The exact comparison base is Git commit
`5ef07fcc762ed0d11d79eeee3012967d1eac6121`. Repository evidence at this base and
the M1-E4 working candidate contains no release-candidate, GA, production, external
consumer acceptance, or deployed-environment evidence for these three operations.
This is therefore a candidate for the ordinary `/v1` compatibility-window process,
not a reuse or extension of ADR 0003's one-time pre-external reset. The immutable
`compatibility-resets-v1.json` record remains byte-for-byte unchanged.

## Decision

PoolAI proposes one exact OpenAPI v1 compatibility window for this transition. It
may become effective only after the public-contract owner explicitly approves it
and a permanent GitHub Issue comment is recorded. The machine gate requires all of
the following simultaneously:

- comparison base `5ef07fcc762ed0d11d79eeee3012967d1eac6121`;
- base OpenAPI SHA-256
  `d8887b322bebc575005a2b170be6a17cf002de94154c94cbfa8a729bf091f076`;
- target OpenAPI SHA-256
  `43b81083d47d5c228f49acd2bdbbc608df57a96df083e22cf64e2a59f65f0ada`;
- exactly the three sorted diagnostics registered in
  `docs/contracts/compatibility-windows-v1.json`;
- registry status `accepted`, this ADR status `Accepted`, and a non-placeholder
  permanent `https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-...`
  approval-evidence URL that is identical in the registry and ADR.

While status is `proposed`, the compatibility command must verify that the exact
candidate is registered and then fail explicitly as pending approval. It must not
waive any diagnostic. Approval is one atomic governance transition: update the
registry status and evidence together with this ADR's status and evidence only
after the permanent approval comment actually exists.

The semantic scope is limited to adding the existing `BadRequest` response at
status 400 to:

1. `GET /api/v1/admin/groups` (`adminListGroups`) for invalid `limit` or cursor;
2. `GET /api/v1/admin/subscription-templates`
   (`adminListSubscriptionTemplates`) for invalid `limit` or cursor; and
3. `GET /api/v1/admin/subscriptions` (`adminListSubscriptions`) for invalid
   `limit`, cursor, `user_id`, or `group_id`.

No request schema, response body schema, successful response, stable error code,
fixture, other operation, or runtime coercion is covered. Exact hashes and the
complete diagnostic set prohibit partial, wildcard, extra, or reused exemptions.

## Alternatives considered

### Return 400 without changing OpenAPI

Rejected. It creates an undocumented public response and leaves generated clients
and contract tests unable to model the runtime boundary.

### Coerce or ignore malformed query input

Rejected. Silent correction makes cursor and filter semantics unstable, hides
client defects, and conflicts with the explicit rejection behavior already used by
the implemented Admin query boundaries.

### Publish `/v2`

Not selected for this narrowly bounded candidate because there is no external
release or consumer evidence for these operations and the only change is an
explicit existing error response. If external usage evidence appears before
approval, this candidate must be withdrawn and `/v2` (or a broader deliberately
designed compatibility migration) must be used.

### Add another ADR 0003 reset entry

Rejected. ADR 0003 and `compatibility-resets-v1.json` authorize exactly one
historical transition and explicitly prohibit a second record or reuse.

## Consequences

- Generated C# and TypeScript contract artifacts and the release manifest carry
  the target OpenAPI digest.
- The three Admin list operations explicitly model strict invalid-query behavior.
- Proposed status remains a hard CI failure, so this candidate cannot merge as an
  accidental exception.
- Accepted status can consume only the exact three registered diagnostics for the
  pinned base and target; any drift fails closed.
- ADR 0003 and its reset registry remain unchanged and independently validated.

## Migration, window boundary, and rollback impact

This decision changes no data and authorizes no database or Redis operation. The
window opens only for the exact base-to-target transition above. It closes when the
accepted target is merged into the protected branch; that target then becomes the
ordinary `/v1` comparison baseline, and the record is inert for every other base or
target. It cannot authorize a later response-status change.

Before approval or merge, rollback is to withdraw the candidate registry/ADR and
restore the three OpenAPI operations plus generated artifacts and manifest to the
base digest. After accepted merge, the record and accepted ADR are immutable audit
history. A later reversal or incompatible change requires normal `/v1`
compatibility governance or `/v2`; deleting history is not rollback.

## Security impact

- Strict UUID, cursor, and limit rejection avoids ambiguous filters and accidental
  broad Admin result sets.
- The existing closed `BadRequest` problem response avoids leaking parser or
  cursor internals.
- The registry contains only public contract hashes, diagnostics, and public
  approval URLs; it contains no credentials, tokens, private hosts, or consumer
  data.
- Requiring a permanent approval-evidence URL prevents a local status edit from
  silently authorizing the window.

## Contract and test files updated by this decision

- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/compatibility-windows-v1.json`
- deterministic generated C# and TypeScript contract artifacts
- `docs/release-manifest-v1.json`
- `tools/contracts/lib/compatibility-windows.mjs` and independent self-tests
- OpenAPI validator tests for the three exact `BadRequest` bindings
- contract-tooling and ADR indexes
