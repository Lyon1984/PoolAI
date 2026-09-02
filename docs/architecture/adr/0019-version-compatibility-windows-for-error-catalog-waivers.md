# ADR 0019: Version compatibility windows for exact error-catalog waivers

- Status: **Proposed**
- Date: 2026-09-02
- Decider: PoolAI architecture, public-contract, and contract-tooling owner (`@Lyon1984`); this proposal does not take effect without the exact approval described below
- Relates to: [M4-E2 Issue #25](https://github.com/Lyon1984/PoolAI/issues/25), [M4-E3 Issue #26](https://github.com/Lyon1984/PoolAI/issues/26), ADR 0003, ADR 0005, ADR 0017, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: **Pending an exact permanent approval by `@Lyon1984`**
- First intended consumer: compatibility window `m4-e2-e3-model-discriminator-overload` (**Pending its own exact public-contract approval**)

## Context

The contract compatibility checker already compares two independent public
sources. It freezes existing OpenAPI operations, schemas, response statuses, and
SSE fixtures, and it also parses the stable section of
`docs/contracts/error-catalog.md` and rejects removal, renaming, or a change to a
stable code's HTTP/SSE, retryable, `Retry-After`, or meaning semantics.

The normal compatibility-window registry cannot presently govern the second
kind of failure safely. `compatibility-windows-v1.json` declares
`schemaVersion: 1`, requires an exact closed set of fields for every record,
binds only the base and target OpenAPI SHA-256 digests, and accepts only an
`allowedFailures` entry beginning with a local OpenAPI JSON Pointer (`#/`). The
checker can produce an exact diagnostic such as
`error-catalog:gateway_overloaded: existing status, stream, retry, or meaning semantics changed`,
but the v1 registry correctly rejects that diagnostic and has no hashes that
would pin the two error-catalog sources.

ADR 0017 requires the future
`m4-e2-e3-model-discriminator-overload` window to change only the existing
Responses and Chat 429 descriptions and the existing `gateway_overloaded`
meaning/causes. Silently ignoring the error-catalog failure would weaken the
stable-code guard. Allowing the failure while binding only OpenAPI would permit
the error catalog to drift without invalidating the approval. Reinterpreting the
closed v1 schema in place would also make one version number describe two
different machine formats.

The registry therefore needs one minimal, versioned, backward-readable evolution
that preserves all accepted history while allowing a future window to bind and
waive an exact error-catalog semantic diagnostic.

## Decision

This proposal becomes effective only after `@Lyon1984` approves the exact
candidate in a permanent Issue #44 comment and this ADR is backwritten to
`Accepted` with that evidence. It approves only the registry/tooling format. It
does not approve ADR 0017, the
`m4-e2-e3-model-discriminator-overload` record or its contract changes, any
implementation, or M4 acceptance.

### The registry advances to schema version 2

A schema-version bump is required. Version 1 is deliberately closed: its record
key set and diagnostic namespace are exact validation rules, not an open object
with ignorable extension fields. Adding error-catalog digests or a second
diagnostic namespace while continuing to emit `schemaVersion: 1` would silently
redefine already signed machine data. That is not a backward-compatible use of a
closed schema.

The minimal compatible design is therefore `schemaVersion: 2`, not a second
registry and not a rewrite of the v1 records. The top-level keys remain exactly
`schemaVersion` and `windows`. The v2 reader understands both source versions so
it can compare a schema-1 Git base with a schema-2 head, but the current registry
declares version 2 after the governed transition. This is backward-compatible
for historical data, not forward-compatible with an old v1-only executable: an
old validator must reject the v2 document rather than guess at its meaning.

Schema version 2 recognizes two exact record shapes:

1. An **OpenAPI-only record** has exactly the existing v1 fields and may contain
   only `#/` diagnostics. Every existing v1 record remains valid in this shape.
2. A **digest-bound record** has those same fields plus exactly
   `baseErrorCatalogSha256` and `headErrorCatalogSha256`.

The two new values are lowercase 64-character SHA-256 hex digests of the exact
UTF-8 bytes of `docs/contracts/error-catalog.md` read from the bound Git base and
the candidate working tree, respectively. They are an all-or-nothing pair. A
record with only one digest, an unknown additional key, uppercase hex, a
non-64-character value, or a value calculated from normalized/parsed Markdown is
invalid.

A new OpenAPI-only window may use either shape; including the pair binds the
error-catalog inputs even when no error diagnostic needs an allowance. A new
window containing any `error-catalog:` diagnostic must use the digest-bound
shape. Existing accepted OpenAPI-only records remain legal with their exact v1
shape and must not be backfilled with the new fields. This keeps old approval
scope unchanged while requiring both authoritative error-catalog sources for
every error-semantic waiver.

The one allowed version transition is `1 -> 2`. A schema-2 base requires a
schema-2 head. A downgrade, an unsupported version, a skipped version, or a
schema change without the history checks below fails closed.

### Exact digest-bound record and diagnostic grammar

A digest-bound record retains the v1 rules for `id`, `status`, `scope`,
`baseRef`, both OpenAPI digests, ADR path, Issue #44 approval control/evidence,
and the non-empty `allowedFailures` array. It adds no default, wildcard, prefix,
substring, or regular-expression allowance.

Each `allowedFailures` member must be one exact, non-empty, sorted, unique,
single-line string in exactly one of these forms:

```text
#/<exact-local-JSON-Pointer>: <exact compatibility diagnostic>
error-catalog:<stable_code>: <exact compatibility diagnostic>
```

For the second form, `<stable_code>` must match
`^[a-z][a-z0-9_]{0,127}$` and must identify the exact stable code emitted by the
base-to-head error-catalog comparison. The diagnostic following `: ` is compared
as an exact complete string. Neither selector admits `*` or `?` wildcard
semantics. Punctuation that appears inside an exact generated diagnostic remains
literal, just as regex punctuation in an OpenAPI diagnostic is literal today.
An `sse-fixture:`, arbitrary file path, bare code, truncated message, alternate
prefix, or multi-line value is invalid.

An `error-catalog:` member is legal only on a digest-bound record with both error
catalog hashes. When at least one such member exists, the two error-catalog
digests must differ. The normal checker must still produce exactly the complete
sorted `allowedFailures` set across OpenAPI and the stable error catalog. Any
unregistered failure, registered-but-absent failure, changed punctuation, code
rename, partial match, or extra failure rejects the candidate. New stable codes
remain additive under the ordinary rule and require no waiver.

This evolution does not create a general error-catalog reset. It retains the
normal compatibility-window scope, exact base commit, different base/target
OpenAPI digests, one candidate ADR, and one approval transition. A purely
error-catalog-only breaking transition with identical OpenAPI bytes is outside
this decision and must open separate governance rather than weakening the
existing OpenAPI-window identity.

### Resolution and source binding

For a digest-bound record, window resolution must receive all four exact source
texts: base and head OpenAPI plus base and head error catalog. Before any
allowance is supplied to `validateContractCompatibility`, it must verify:

1. the exact lowercase 40-character `baseRef`;
2. base and target OpenAPI SHA-256 against their existing fields; and
3. base and target error-catalog SHA-256 against the new fields.

The base error catalog is read from the same exact Git commit as the base
OpenAPI. The head error catalog is the same safely loaded source already parsed
by the contract validator. No caller-provided alternate path, normalized copy,
generated catalog, network source, or digest-only bypass is permitted. The
existing repository-local, canonical, no-follow, regular-file protections for
ADRs remain unchanged.

A digestless OpenAPI-only record continues to resolve exactly as schema version
1 did and can contain only OpenAPI-pointer diagnostics. Its absence of
error-catalog digests is not interpreted as approval of any error-catalog
difference. If such a difference appears, it remains unexpected and fails
closed.

Proposed and accepted governance is unchanged. A `proposed` digest-bound record
must have `approvalEvidence: null`; after its hashes and complete diagnostics
match, the compatibility command still fails with `pending approval`. Only the
atomic transition to `accepted`, a non-placeholder permanent Issue #44 comment
URL, and a matching Accepted ADR may activate the allowance.

### Immutable history and approval transition

The history guard must parse each side according to its declared schema version
and enforce all of the following:

- every window present in the base remains present in the head, in the same
  relative order and with the same ID;
- every accepted base record remains field-for-field semantically identical;
  in particular, an existing v1-shaped record cannot be supplemented with
  error-catalog digests, a changed ADR, reordered/changed diagnostics, or
  replacement approval evidence;
- every referenced ADR for accepted history remains byte-for-byte identical;
- an existing proposed record may change only its registry `status` and
  `approvalEvidence` and the matching ADR status/evidence lines; its base,
  digests, diagnostics, ADR path, control, and all other bytes remain bound;
- every newly appended record satisfies exactly one schema-2 shape, and any
  record containing an `error-catalog:` diagnostic is digest-bound; and
- the initial root change from schema version 1 to 2 cannot be used to delete,
  reorder, drift, or retroactively supplement any existing record.

Whitespace outside signed ADR bytes is not an exemption mechanism: parsed
registry semantics and ordering are authoritative. Once schema version 2 is in
the comparison base, the root version and every accepted digest-bound record are
immutable history under the same rules.

Each digest-bound record's ADR must contain exactly one machine-checkable line
for its status, window ID, base Git commit, base/target OpenAPI SHA-256,
base/target error-catalog SHA-256, approval control, approval evidence, and every
allowed diagnostic. During approval, only the status and evidence lines may
change. This ADR 0019 governs that format but is not itself a compatibility
window record.

### Boundary for the first intended window

After this ADR is accepted and the v2 tooling is present, ADR 0017 may propose
the separate `m4-e2-e3-model-discriminator-overload` record. That record must be
digest-bound and must enumerate its exact OpenAPI diagnostics plus this exact
error-catalog diagnostic if the candidate produces it:

```text
error-catalog:gateway_overloaded: existing status, stream, retry, or meaning semantics changed
```

The record must bind the exact base commit and all four source digests. It starts
as `proposed` with null evidence. Its permanent public-contract approval is
independent of approval for this schema/tooling ADR and independent of ADR 0017's
architecture approval. None of those approvals may be inferred from another.

That concrete window may authorize only the exact 429 description and
`gateway_overloaded` meaning/causes enumerated by its signed candidate. It cannot
authorize a new status, code, response schema, header, Retry-After value, stream
semantic, unrelated catalog edit, or a different diagnostic. Such a change must
stop and obtain its own governance.

## Rejected alternatives

### Keep `schemaVersion: 1` and make the two fields optional

Rejected. Version 1 has exact closed record keys and accepts only OpenAPI-pointer
diagnostics. Reusing the number would silently assign new meaning to signed
machine data and make it impossible for reviewers to distinguish the old and new
grammar from the version marker.

### Backfill error-catalog hashes into every accepted record

Rejected. The old approvals did not bind those hashes. Retrofitting them changes
accepted records and could create the false impression that historical approvers
reviewed an additional source. Legacy records instead remain exact history.

### Bind error-catalog failures only to the OpenAPI hashes

Rejected. An error-catalog edit could drift while both OpenAPI digests continue
to match. The allowance would then no longer identify the approved target.

### Create a second error-catalog compatibility registry

Rejected. A second record and approval lifecycle for one atomic public-contract
candidate could disagree with the OpenAPI window, duplicate base selection, or
allow only half of the failure set. One digest-bound record keeps the four
sources and complete diagnostic set indivisible.

### Treat meaning changes as additive or ignore them

Rejected. Client retry, operator response, and disclosure behavior can depend on
a stable code's meaning even when its spelling and HTTP status do not change.
The existing semantic comparison remains fail-closed; only an exact signed
window may consume its diagnostic.

### Permit prefixes, wildcards, or diagnostic regular expressions

Rejected. A broad match could silently consume later unrelated breakage. Every
selector and generated diagnostic remains an exact complete string.

## Consequences

- The registry advertises its new closed grammar honestly as schema version 2.
- All existing accepted OpenAPI-only windows retain their exact fields, ADRs,
  evidence, and original approval scope.
- Every error-catalog waiver binds both OpenAPI and error-catalog inputs, while
  only an explicitly enumerated exact failure can be waived.
- An old v1-only tool fails closed on the v2 registry; CI must upgrade tooling and
  registry atomically.
- The first M4-E2/E3 window remains pending until its own exact hashes,
  diagnostics, ADR markers, and permanent evidence exist.
- The reset registry remains separate and unchanged. This decision does not add
  another pre-release reset or alter ADR 0003 history.

## Migration and rollback impact

This is a repository contract-tooling migration only. It adds no PostgreSQL
migration, table, column, function, permission, Redis key/script/version, runtime
state, data repair, or deployment authorization.

Before protected merge, rollback is to withdraw the schema-2 candidate and keep
the unchanged schema-1 registry/tool. The concrete M4 window cannot then waive an
error-catalog semantic change and must also be withdrawn or redesigned.

After an accepted schema-2 candidate merges, rollback must use a v2-aware
contract-compatible tooling repair. It must not set the registry back to version
1, remove a record, strip error-catalog hashes, rewrite an accepted ADR, or delete
approval evidence. A later format change requires another explicit schema
version and ADR. Historical v1 records continue to be validated as legacy
records inside v2; there is no data conversion to reverse.

## Security impact

- Binding raw base/head error-catalog digests prevents a signed diagnostic from
  authorizing unreviewed meaning, retry, status, or disclosure drift.
- Exact selector grammar and full-set equality prevent wildcard, prefix,
  substring, partial-set, and unused-allowance bypasses.
- The v1-to-v2 history guard prevents deletion, reordering, record mutation,
  digest backfill, an error allowance without both digests, or approval-marker
  laundering.
- Existing canonical/no-follow ADR reads and exact Git-base reads remain in use;
  no network content or alternate file path enters the trust boundary.
- Registry and ADR content is limited to public commit IDs, SHA-256 digests,
  diagnostics, and public Issue URLs. It contains no credentials, prompts,
  private hosts, request data, or secret material.

## Coupled contract and test files

Acceptance and implementation of this format require one atomic protected change
covering:

- `tools/contracts/lib/compatibility-windows.mjs` for schema-1/schema-2 parsing,
  exact OpenAPI-only/digest-bound record shapes, diagnostic grammar, approval
  markers, and immutable history;
- `tools/contracts/lib/compatibility.mjs` for four-source resolution and digest
  verification before any allowance is consumed;
- `tools/contracts/lib/compatibility-window-self-tests.mjs` for the positive and
  negative matrix below;
- `tools/contracts/README.md` for the schema-2 format, legacy-history boundary,
  digest rules, and failure behavior; and
- `docs/contracts/compatibility-windows-v1.json` for the one governed root
  `schemaVersion: 2` transition while preserving every existing record and ADR.

The filename `compatibility-windows-v1.json` remains unchanged because `v1`
identifies the public `/v1` compatibility process, while the internal
`schemaVersion` identifies the registry representation. Renaming or duplicating
the registry would create two possible authorities.

The concrete `m4-e2-e3-model-discriminator-overload` change must separately and
atomically update `docs/contracts/openapi-v1.yaml`,
`docs/contracts/error-catalog.md`, its digest-bound registry record, the ADR that
contains the exact binding lines, and any coupled contract fixtures or generated
artifacts required by the target. It remains subject to an independent permanent
Issue #44 approval.

At minimum, self-tests and contract tests must prove:

1. the current schema-1 registry and all existing ADRs remain valid;
2. the exact `1 -> 2` transition preserves v1 records and accepts one newly
   appended digest-bound proposed record;
3. schema 1 rejects extended fields and `error-catalog:` diagnostics, while
   schema 2 accepts an exact digestless OpenAPI-only record;
4. missing/extra/uppercase/stale error-catalog hashes, a one-sided digest pair,
   an error diagnostic with equal base/head catalog digests, malformed stable
   codes, unsorted/duplicate entries, newlines, selectors with wildcard syntax,
   and unknown keys fail closed;
5. a mixed exact OpenAPI/error-catalog failure set is consumed only when every
   base/head digest and every diagnostic matches, and any unused or unexpected
   failure is rejected;
6. proposed status still ends in the exact pending-approval failure and accepted
   status requires the matching permanent evidence in both record and ADR;
7. history rejects downgrade, deletion, reordering, drift, digest backfill into
   an existing accepted v1 record, or any non-marker change during
   proposed-to-accepted approval; and
8. ADR history remains byte-immutable except for the one permitted status/evidence
   transition.

The narrow contract validator/self-tests run first, followed by the repository
contract test project and normal quality gate. No compatibility success may be
claimed from documentation review alone.
