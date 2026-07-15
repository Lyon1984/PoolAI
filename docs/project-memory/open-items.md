# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## M0 exit blockers

1. The authorized target repository's full-history Gitleaks, browser matrix, SBOM/image scans, and dynamic Compose jobs passed. PR #1 proved the CodeQL `actions: read` correction, but upload is blocked because Code Scanning is not enabled for the private repository. Its quality logs also exposed false-pass risks when missing `rg` was suppressed by both coverage-integrity and forbidden-scope; the branch now uses fail-closed Node scanners. A target rerun of those scanners, entitled Code Security enablement, non-zero changed-line coverage on a production-source PR, required checks, and branch protection remain pending.
2. The repository now validates all DEC/AC mappings and a complete 38-Epic task-system-neutral import preview. Importing that preview into the real task system, reading back real IDs/owners, completing DEC/database/OpenAPI sign-off, and recording the M0 exit review still require external project-management/review evidence.

## Repository publication

- The authorized private GitHub repository `Lyon1984/PoolAI` and its bootstrap `main` branch exist. PR #1 validated the CodeQL token correction, but private Code Scanning entitlement/enablement remains blocked; its changed-line check correctly had no production source to evaluate. Required checks and branch protection still need verified configuration. Subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- SMTP sender and delivery policy
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
