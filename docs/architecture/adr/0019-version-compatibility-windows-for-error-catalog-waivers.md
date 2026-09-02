# ADR 0019: Version compatibility windows for exact error-catalog waivers

- Status: **Accepted**
- Date: 2026-09-02
- Decider: PoolAI architecture, public-contract, and contract-tooling owner (`@Lyon1984`); this proposal does not take effect without the exact approval described below
- Relates to: [M4-E2 Issue #25](https://github.com/Lyon1984/PoolAI/issues/25), [M4-E3 Issue #26](https://github.com/Lyon1984/PoolAI/issues/26), ADR 0003, ADR 0005, ADR 0017, and [sign-off control Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: [Issue approval comment](https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5510724488)
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
raw bytes of `docs/contracts/error-catalog.md` read from the bound Git base and
the candidate working tree, respectively. The digest is calculated over the
`Buffer` before any decoding or parsing. They are an all-or-nothing pair. A
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

"Single-line" is a source-data property, not merely a result of splitting on
CR/LF. Registry strings used by any ADR machine line, including every diagnostic,
must contain no Unicode `Cc`, `Zl`, or `Zp` code point. This rejects CR, LF, tab,
NEL, U+2028, U+2029, and other control/line/paragraph separators before a line is
constructed. The diagnostic after the exact `: ` delimiter must contain at least
one Unicode scalar value.

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

### Exact digest-bound ADR machine preamble

For a digest-bound record, the ADR machine preamble begins on the line after the
single H1 title and ends immediately before the first line beginning with exact
`## `. A missing H1, a second H1 before that boundary, a missing `## ` boundary,
an indented pseudo-heading, or a machine marker outside this preamble is invalid.
Non-reserved human metadata such as `Date`, `Decider`, and `Relates to` may remain
in the preamble, but it cannot use a reserved marker namespace.

The complete registry-derived machine-line set is:

```text
- Status: **<Proposed-or-Accepted>**
- Compatibility window ID: `<id>`
- Base Git commit: `<baseRef>`
- Base OpenAPI SHA-256: `<baseOpenApiSha256>`
- Target OpenAPI SHA-256: `<headOpenApiSha256>`
- Base error-catalog SHA-256: `<baseErrorCatalogSha256>`
- Target error-catalog SHA-256: `<headErrorCatalogSha256>`
- Approval control: [Issue #44](<approvalControl>)
- Approval evidence: **Pending explicit approval**
- Approval evidence: [Issue approval comment](<approvalEvidence>)
- Allowed diagnostic: `<one exact allowedFailures member>`
```

The status line is derived from registry `status`. Exactly one of the two
approval-evidence forms is derived from that same status: Pending for `proposed`,
or the exact permanent URL for `accepted`. Every other placeholder is replaced
by its exact registry value. Each singleton namespace must have exactly one
derived line. `Allowed diagnostic` must have exactly
`allowedFailures.length` lines, one occurrence of each sorted member and no
other diagnostic. The two error-catalog digest lines therefore have these exact
literal labels and map respectively to `baseErrorCatalogSha256` and
`headErrorCatalogSha256`; `Head error-catalog`, `Base error catalog`, `SHA256`, or
any other alias is not accepted.

For collision detection, a candidate bullet is any ADR line with optional
leading ASCII whitespace, one CommonMark bullet byte (`-`, `+`, or `*`), and one
or more ASCII spaces. Its marker label is the text after that prefix and before
its first `:`. The validator creates a collision key by ASCII-lowercasing and
removing ASCII whitespace, `-`, and `_`. It applies this scan to the entire ADR;
a reserved candidate outside the preamble is invalid. A label belongs to a
reserved namespace if its key contains any of these canonical keys:

```text
status
compatibilitywindowid
basegitcommit
baseopenapisha256
targetopenapisha256
baseerrorcatalogsha256
targeterrorcatalogsha256
approvalcontrol
approvalevidence
alloweddiagnostic
```

The source/digest namespaces additionally reserve any collision key containing
`openapi` or `errorcatalog`, one of `base`, `target`, or `head`, and one of
`digest`, `hash`, `checksum`, or an ASCII `sha` followed by one or more digits.
This catches reordered or approximate labels rather than allowing them as prose.
Every line in a reserved namespace must byte-for-byte equal one member of the
registry-derived machine-line set; then the exact-count checks above run.
Consequently a correct line cannot hide an extra or conflicting Status, OpenAPI
digest, error-catalog digest, approval, or unregistered Allowed diagnostic
marker. Differently cased, whitespace-varied, suffixed, duplicate, and otherwise
near marker lines fail closed.

All machine lines must satisfy the single-line/control-code rule above. A marker
value cannot terminate its bullet and inject a second metadata line. Legacy
OpenAPI-only ADRs remain governed by their existing marker checks and are not
retroactively required to contain the two error-catalog digest lines or the new
reserved-namespace scan.

### Resolution, byte loading, and source binding

For a digest-bound record, window resolution must receive the base and head
OpenAPI sources plus the base and head error-catalog `Buffer` values. Before any
allowance is supplied to `validateContractCompatibility`, it must verify:

1. the exact lowercase 40-character `baseRef`;
2. base and target OpenAPI SHA-256 against their existing fields; and
3. base and target error-catalog SHA-256 against the new fields.

The base error catalog is read as a raw Git-blob `Buffer` from the same exact Git
commit as the base OpenAPI; a UTF-8 string-producing `git show` helper is not
sufficient. The head error catalog is opened through the repository's canonical,
inside-root, `O_NOFOLLOW`, `O_NONBLOCK`, stable-descriptor, regular-file reader
and is read from that verified descriptor as a `Buffer`. A direct path-based
`readFile(..., 'utf8')`, a second reopen after verification, or a symlink/FIFO/
device fallback is forbidden.

Each raw `Buffer` is hashed first. It is then decoded exactly once with a fatal
UTF-8 decoder, and that one decoded value is passed to stable-error parsing and
compatibility comparison. Invalid UTF-8 fails closed; replacement decoding is
forbidden, so distinct invalid byte sequences cannot converge through U+FFFD and
share an approved semantic comparison. The implementation must not hash a
decoded/re-encoded string or separately decode one byte source for hashing,
validation, and comparison.

No caller-provided alternate path, normalized copy, generated catalog, network
source, digest-only bypass, or already-decoded untrusted string is permitted at
the digest-bound resolution boundary. OpenAPI loading remains under its existing
contract in this decision; this ADR does not falsely claim that the current
generic contract-source loader already provides the new error-catalog byte
guarantee. The implementation must add that guarantee explicitly before a
digest-bound record can validate.

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

- the complete base `windows.map(id)` sequence is an exact prefix of the head ID
  sequence; new records may be appended only after the last base record, so a
  prepend, middle insertion, reorder, replacement, or duplicate cannot preserve
  history accidentally;
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

If `CONTRACT_DIFF_BASE` has no
`docs/contracts/compatibility-windows-v1.json`, a schema-2 head fails closed. It
must not be treated as an empty-history initialization and cannot activate the
v1-to-v2 transition. The existing missing-base bootstrap behavior remains only
for the historical introduction of a schema-1 registry and cannot contain v2
fields or `error-catalog:` diagnostics. In the current repository, advancing to
schema 2 therefore requires a readable schema-1 base registry whose complete
history passes the prefix and immutability checks.

Whitespace outside signed ADR bytes is not an exemption mechanism: parsed
registry semantics and ordering are authoritative. Once schema version 2 is in
the comparison base, the root version and every accepted digest-bound record are
immutable history under the same rules.

Each digest-bound record's ADR must contain exactly one machine-checkable line
for its status, window ID, base Git commit, base/target OpenAPI SHA-256, the exact
Base/Target error-catalog SHA-256 lines frozen above, approval control, approval
evidence, and every allowed diagnostic. Reserved marker namespaces must contain
no additional or approximate line. During approval, only the status and evidence
lines may change. This ADR 0019 governs that format but is not itself a
compatibility-window record.

### Boundary for the first intended window

The governance sequence is deliberately two-stage. First, this ADR must receive
its exact architecture/tooling approval, be backwritten to `Accepted`, and the
schema-2 validator, self-tests, documentation, and registry transition must land
through the protected branch. Only a later candidate based on that landed v2
tooling may request the public-contract approval required by ADR 0017 and the
separate `m4-e2-e3-model-discriminator-overload` record. ADR 0017 may remain a
proposal during the first stage, but neither it nor its required public-contract
change is effective.

The later record must be digest-bound and must enumerate its exact OpenAPI
diagnostics plus this exact error-catalog diagnostic if the candidate produces
it:

```text
error-catalog:gateway_overloaded: existing status, stream, retry, or meaning semantics changed
```

The record must bind the exact base commit and all four source digests. It starts
as `proposed` with null evidence. Its permanent public-contract approval is
independent of approval for this schema/tooling ADR and independent of ADR 0017's
architecture approval. None of those approvals may be inferred from another.
Including ADR 0019 as Proposed, v2 code, ADR 0017, or the concrete window in one
unaccepted working tree cannot bypass this order or make the error-catalog
allowance effective. In particular, the concrete window must not be approved in
the candidate that first lands the v2 tooling.

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
- The first M4-E2/E3 window remains pending until this ADR and the v2 tooling have
  landed first, then its own later exact hashes, diagnostics, ADR markers, and
  permanent evidence exist.
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
- Buffer-first hashing plus one fatal UTF-8 decode prevents invalid source bytes
  from being replaced, normalized, or conflated before the approval binding is
  checked.
- Exact selector grammar and full-set equality prevent wildcard, prefix,
  substring, partial-set, and unused-allowance bypasses.
- Exact-prefix history and missing-base rejection prevent prepend/middle
  insertion, deletion, reordering, record mutation, digest backfill, an error
  allowance without both digests, or approval-marker laundering.
- Canonical/no-follow regular-file reads protect the head source, while exact raw
  Git-blob reads protect the base source; no network content or alternate file
  path enters the trust boundary.
- Registry and ADR content is limited to public commit IDs, SHA-256 digests,
  diagnostics, and public Issue URLs. It contains no credentials, prompts,
  private hosts, request data, or secret material.

## Coupled contract and test files

Acceptance and implementation of this format require one atomic protected change
covering:

- `tools/contracts/lib/compatibility-windows.mjs` for schema-1/schema-2 parsing,
  exact OpenAPI-only/digest-bound record shapes, diagnostic grammar, approval
  markers, exact-prefix/missing-base history, and immutable history;
- `tools/contracts/lib/compatibility.mjs` for four-source resolution and digest
  verification, raw Git-blob Buffer loading, and fatal single-decode flow before
  any allowance is consumed;
- `tools/contracts/lib/context.mjs` for canonical no-follow regular-file Buffer
  loading of the head error catalog and for returning the exact bytes together
  with their one fatal-decoded source;
- `eng/policies/repository-file.mjs` and its focused tests if the existing
  descriptor helper cannot be reused unchanged; any loader change is part of
  this same atomic candidate rather than a follow-up hardening patch;
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
5. raw base/head bytes are hashed before a single fatal UTF-8 decode; invalid
   UTF-8, U+FFFD replacement convergence, a symlink, FIFO/non-regular file,
   canonical escape, path-swap attempt, decoded-string hashing, or a second
   reopen/decode fails closed;
6. a mixed exact OpenAPI/error-catalog failure set is consumed only when every
   base/head digest and every diagnostic matches, and any unused or unexpected
   failure is rejected;
7. proposed status still ends in the exact pending-approval failure and accepted
   status requires the matching permanent evidence in both record and ADR;
8. history requires the base ID sequence to be the exact head prefix and rejects
   prepend, middle insertion, reorder, replacement, deletion, drift, digest
   backfill into an existing accepted v1 record, or any non-marker change during
   proposed-to-accepted approval;
9. a missing base registry with a schema-2 head fails closed, while the narrowly
   retained schema-1 historical bootstrap cannot carry v2 fields or error
   diagnostics;
10. every digest-bound ADR has one valid H1-to-first-`## ` preamble and exactly
    the registry-derived singleton/diagnostic lines; extra or conflicting
    Status, Base/Target OpenAPI digest, Base/Target error-catalog digest,
    approval, or unregistered Allowed diagnostic lines fail closed, as do
    duplicates, control/line separators, case/whitespace variants,
    Head/digest/SHA aliases, and other reserved near markers; and
11. ADR history remains byte-immutable except for the one permitted
    status/evidence transition.

The narrow contract validator/self-tests run first, followed by the repository
contract test project and normal quality gate. No compatibility success may be
claimed from documentation review alone.
