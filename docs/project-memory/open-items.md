# Open items

Only unresolved decisions, risks, or blockers belong here. The full M0-M7 delivery backlog remains authoritative in `docs/开发执行规格-v1.0.md`.

## M0 exit blockers

1. Repository-owned PR/release gates are implemented and locally verified, but the authorized target repository still needs its first workflow runs: changed-line coverage against a real PR base, CodeQL, full-history Gitleaks, the browser matrix, SBOM/image scans, dynamic Compose, required checks, and branch protection.
2. The repository now validates all DEC/AC mappings and a complete 38-Epic task-system-neutral import preview. Importing that preview into the real task system, reading back real IDs/owners, completing DEC/database/OpenAPI sign-off, and recording the M0 exit review still require external project-management/review evidence.

## Repository publication

- The authorized private GitHub repository `Lyon1984/PoolAI` and its bootstrap `main` branch exist. Target workflow conclusions, the first pull request, required checks, and branch protection still need verified readback/configuration; subsequent changes must use pull requests rather than direct pushes to `main`.

## Later implementation risk

- Before M4, [`NormalizedUpstreamResult`](../../src/Modules/PoolAI.Modules.Gateway.Abstractions/NormalizedUpstreamResult.cs) Token fields must move beyond `long` to the frozen lossless Token representation and enforce the OpenAI safe-integer output boundary; the current abstraction cannot represent abnormal 78-digit upstream evidence.

## Environment inputs still required later

- Secret Provider/KMS implementation and key identifiers
- SMTP sender and delivery policy
- Production ingress, TLS, CORS allowlist, trusted proxy, and observability destinations

Do not put values for these inputs in this file.
