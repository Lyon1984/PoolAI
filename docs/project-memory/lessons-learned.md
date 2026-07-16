# Lessons learned

## Integrity scanners must distinguish no matches from execution failure

- Evidence: successive target quality logs reported `rg: command not found` in the coverage-integrity and forbidden-scope gates, but command substitutions ending in `|| true` converted the failures into empty output and both negative scanners reported success.
- Durable lesson: a negative scanner may tolerate its tool's documented no-match status only after distinguishing it from startup/read/parse failures; missing tools and unreadable roots must fail closed. Prefer an already locked runtime when that removes an undeclared runner dependency.
- Scope and verification: coverage-suppression and forbidden-product-scope policies; replaced with Node standard-library scanners and verified with clean, denied-input, and explicit-guard probes plus passing PR and default-branch target logs.

## Private-repository CodeQL upload needs Actions read access

- Evidence: the initial target CodeQL run completed extraction, builds, queries, and SARIF export, then failed while reading its workflow run with `Resource not accessible by integration`; the job token had `contents: read` and `security-events: write` but not `actions: read`.
- Durable lesson: a private-repository CodeQL/SARIF job must explicitly grant `actions: read` in addition to `contents: read` and `security-events: write`; this token permission is separate from GitHub Code Security entitlement and must be verified before diagnosing an entitlement failure.
- Scope and verification: GitHub Actions security evidence; enforced by `eng/policies/validate-version-locks.mjs`. PR #1 verified the permission correction through SARIF upload initiation; after the authorized visibility change to public, the incremental rerun uploaded successfully and produced zero PR results, while the later full default-branch analysis correctly remained able to surface pre-existing findings.

## Incremental CodeQL needs default-branch readback

- Evidence: PR #1 analyzed 166 rules with zero results, but its first post-merge default-branch analysis surfaced four pre-existing JavaScript findings. PR #2 also produced zero PR results; only its post-merge default-branch analysis established that all four findings became `fixed`, no dismissal was used, and no new finding appeared.
- Durable lesson: a successful pull-request CodeQL analysis proves the proposed merge introduces no reported result in that PR context; it does not replace full default-branch analysis or the open-alert API readback needed to claim repository-wide closure.
- Scope and verification: protected security delivery; verify the PR analysis, merge through required checks, then retain the matching default-branch analysis and alert-state evidence.

## Central package changes require Solution-wide lock refresh

- Evidence: adding a centrally managed direct package with `CentralPackageTransitivePinningEnabled` made otherwise untouched project lock files fail `dotnet restore --locked-mode` with NU1004.
- Durable lesson: after adding or moving a central package version, run a Solution-level `dotnet restore PoolAI.sln --force-evaluate`, then rerun locked restore; refreshing only the directly edited project is insufficient.
- Scope and verification: NuGet/package-lock maintenance; reproduced by the repository quality gate and verified by the subsequent fully passing locked restore.

## Minimal-host tests must inject startup-time settings early

- Evidence: `WebApplicationFactory.ConfigureAppConfiguration` supplied the test PostgreSQL setting too late for top-level `Program.cs` service registration, which reads the connection string immediately after `WebApplication.CreateBuilder`.
- Durable lesson: settings consumed during minimal-host service registration must be injected with `IWebHostBuilder.UseSetting` or an equivalently early provider; later test configuration remains suitable for settings first read during host build or service resolution.
- Scope and verification: Api End-to-End host fixtures; reproduced by seven startup failures and verified by all seven End-to-End tests plus the full quality gate.
