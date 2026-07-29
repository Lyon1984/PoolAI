# ADR 0010: Freeze the upstream Account Base URL safety contract

- Status: **Proposed**
- Date: 2026-07-30
- Decider: PoolAI public-contract, Supply, security, and database owner (`@Lyon1984`); this candidate does not take effect without the approval evidence below
- Relates to: M2-E2 Issue #16, ADR 0001, ADR 0009, and sign-off control Issue #44
- Compatibility window ID: `m2-e2-upstream-account-base-url-safety`
- Base Git commit: `a2127f2d4a2ed8b0ccdd2b7fd6c62c27fd05eab0`
- Base OpenAPI SHA-256: `1c9dee2fe48cd3e2f0fa5a00805e07e21d303b5a4fa070faeab66f3be6132141`
- Target OpenAPI SHA-256: `6f3bde282140f66e7c73ef811b17e11c51bbacb58bce6516a3ca4f3f98e977bb`
- Approval control: [Issue #44](https://github.com/Lyon1984/PoolAI/issues/44)
- Approval evidence: **Pending explicit approval**
- Allowed diagnostic: `#/components/schemas/Account/properties/base_url/maxLength: maxLength tightened from <none> to 2048`
- Allowed diagnostic: `#/components/schemas/Account/properties/base_url/pattern: pattern changed from <none> to ^(?:https://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:/[^\s?#]*)?$`
- Allowed diagnostic: `#/components/schemas/AccountCreateRequest/properties/base_url/maxLength: maxLength tightened from <none> to 2048`
- Allowed diagnostic: `#/components/schemas/AccountCreateRequest/properties/base_url/pattern: pattern changed from <none> to ^(?:https://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:/[^\s?#]*)?$`
- Allowed diagnostic: `#/components/schemas/AccountUpdateRequest/properties/base_url/maxLength: maxLength tightened from <none> to 2048`
- Allowed diagnostic: `#/components/schemas/AccountUpdateRequest/properties/base_url/pattern: pattern changed from <none> to ^(?:https://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:/[^\s?#]*)?$`

## Context

The frozen Account request and response schemas previously required only
`format: uri`, while signed migrations 0001 and 0010 accepted HTTPS URLs and
HTTP only for three loopback spellings. That left important public behavior
undefined: URI userinfo could carry a secret, query or fragment state could be
silently combined with upstream paths, an unbounded value could consume
excessive memory and log space, and application/database validators could
legitimately disagree.

M2-E2 must expose Account CRUD and prove URL safety. Implementing the signed
database behavior behind the public API without first freezing the accepted
input would silently tighten an existing v1 schema. The six diagnostics above
therefore require one exact compatibility window.

## Decision

### Exact public Base URL grammar

`Account.base_url`, `AccountCreateRequest.base_url`, and
`AccountUpdateRequest.base_url` use the same contract:

- the decoded string is an absolute URI and contains at most 2,048 Unicode
  scalar values;
- the scheme spelling is lowercase `https`, except that lowercase `http` is
  allowed only with the exact host `localhost`, `127.0.0.1`, or `[::1]`;
- a non-loopback HTTPS host is ASCII DNS/IPv4 syntax or a bracketed IPv6
  literal; an internationalized DNS name must be supplied as ASCII punycode;
- an explicit port, when present, is the canonical decimal range `1..65535`
  without leading zeroes;
- an optional absolute path is allowed so OpenAI-compatible deployments may
  use a prefix such as `/v1`;
- userinfo, query, fragment, whitespace, relative references, and every other
  scheme are rejected;
- the accepted original string is persisted without trimming, credential
  extraction, or query/path rewriting.

The OpenAPI `format: uri`, exact pattern, and maximum length are all required.
The .NET Supply boundary validates `Uri.OriginalString`, and the forward
database validator applies the same bounded grammar. Public response DTOs never
contain the Account credential.

### SSRF boundary

This decision closes the M2-E2 stored-URL and control-plane injection surface.
It intentionally does not claim that syntactic validation defeats DNS
rebinding, a DNS name resolving to a private address, redirects, or a
time-of-check/time-of-use change.

Any later health probe or Gateway transport that opens a network connection
must re-read the current Supply configuration, resolve and classify every
address at connection time, apply the deployment egress policy, disable
automatic cross-policy redirects, and repeat the check for an allowed redirect.
That connection-time policy belongs to the milestone that introduces the
outbound call; it cannot be inferred from this stored-URL approval.

### Provider and ownership invariants

The URL is Account-owned. This ADR does not change the immutable
`provider=openai/openai_compatible` selector, the public `platform=openai`
protocol-family constant, Channel ownership, Group Supply Configuration
ownership, or any cross-context dependency. `platform` remains unsuitable as a
Provider selector.

### Governance state

The compatibility registry binds exactly the six diagnostics above. While this
ADR/window remains `Proposed`/`proposed` with null approval evidence, the
compatibility command must verify the exact base, target, and diagnostic set and
then fail with `pending approval`. This candidate is not a waiver, database
signature, remote migration, deployment, merge, or release authorization.

## Alternatives considered

### Keep `format: uri` only

Rejected. It permits syntactically valid but unsafe userinfo/query/fragment
shapes and does not align the public contract with the signed database boundary.

### Permit HTTP for any OpenAI-compatible deployment

Rejected. Cleartext credential-bearing upstream traffic is not an acceptable
production default. Exact loopback HTTP remains available for local development
and deterministic tests.

### Reject all private or loopback addresses at create time

Rejected. OpenAI-compatible deployments may intentionally use private HTTPS,
and create-time DNS classification cannot prevent rebinding. The actual
connection requires its own current-address and egress-policy check.

### Normalize or silently trim the submitted value

Rejected. It can change idempotency hashes, audit evidence, routing paths, or
the authority selected by the client. Validation is not an authorization to
rewrite.

## Consequences

- Six existing v1 input/response schema locations are tightened through one
  exact, non-reusable compatibility window.
- Account CRUD has one deterministic URL rule across contract, Domain,
  Application, and PostgreSQL.
- Local mock upstreams remain possible through the three exact loopback HTTP
  forms.
- Existing rows outside the new grammar make the forward migration fail
  atomically. Operators must use a separately authorized read-only preflight
  and maintenance plan; the migration never repairs or rewrites a URL.
- Outbound-call milestones retain an explicit DNS, redirect, and egress-policy
  obligation and cannot cite this ADR as connection-time SSRF evidence.

## Migration, window boundary, and rollback impact

The window applies only from base commit
`a2127f2d4a2ed8b0ccdd2b7fd6c62c27fd05eab0` and its exact OpenAPI digest to
target digest
`6f3bde282140f66e7c73ef811b17e11c51bbacb58bce6516a3ca4f3f98e977bb`.
Any missing or additional diagnostic fails closed.

The database change is forward-only after immutable migration 0010 and requires
its own candidate checksum/manifest approval. Before that migration is applied,
rollback is candidate withdrawal and restoration of the previous application
and manifest. After application, rollback requires another forward migration.
This ADR performs and authorizes no remote migration or data repair.

## Security impact

- URI userinfo cannot become a second credential channel or leak through
  ordinary Account serialization.
- Query and fragment values cannot smuggle tenant tokens or alter path
  composition.
- Cleartext non-loopback upstream transport is rejected.
- Length and port bounds prevent ambiguous or unbounded parsing inputs.
- The explicit connection-time obligation prevents syntactic validation from
  being misrepresented as complete SSRF protection.

## Contract and test updates

- `docs/contracts/openapi-v1.yaml`
- `docs/contracts/compatibility-windows-v1.json`
- `docs/architecture/adr/README.md`
- `docs/README.md`
- generated TypeScript and C# OpenAPI outputs
- `tools/contracts/lib/openapi.mjs` and its self-tests
- M2-E2 Supply Domain/Application validation tests
- forward migration 0011 and PostgreSQL integration tests, under an independent
  database approval
