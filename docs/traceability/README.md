# Release traceability

[`release-1-traceability.json`](release-1-traceability.json) links every frozen Release 1 decision and acceptance ID to its primary workstream, direct implementation/verification Epics, governing contracts, and either existing local evidence or a named planned test. `decisionVerification` is keyed by every `DEC-001..042`; each `AC-001..045` carries its verification inline.

[`delivery-epics.json`](delivery-epics.json) is the task-import registry for all 38 M0–M7 Epics. It records the primary/supporting WS ownership frozen in the system plan plus the unique GitHub Issue URL and read-back owner `@Lyon1984` for each imported Epic. The deterministic exporter merges this registry with the authoritative backlog and Release 1 traceability without turning generated output into another contract source.

This index does not redefine DEC/AC text and does not replace the authoritative registries in [`../开发执行规格-v1.0.md`](../开发执行规格-v1.0.md). `epics` records direct work, not the umbrella M6-E5 release check repeated on every AC; AC-045 includes M6-E5 because it owns an explicit release-acceptance slice. `implemented-local` records only a verified repository slice; `partial` always names both current evidence and the remaining planned test.

Local evidence must declare either `kind: test`, which resolves to an actual xUnit `[Fact]`/`[Theory]` method, or `kind: quality-gate-command`, which resolves to an exact command executed by the repository quality gate. The validator parses the authoritative WS applicability matrix and locks the reviewed ID/WS/Epic map against silent remapping. External task IDs and owners reflect the GitHub readback indexed by Issue #44; DEC-001..042 are signed off by the permanent approval comment recorded in every decision entry, while database, OpenAPI, and M0 exit review stay empty until their separate human approval comments produce auditable permanent links. Format checks cannot replace external readback or human approval. The target-CI gate is verified by PR #4, the task-system gate by Issues #6–#44, and the decision-signoff gate by the DEC approval comment in Issue #44; database, OpenAPI, and exit-review gates remain pending.

Validate it with:

```bash
tools/local-dev/run-with-toolchain.sh node eng/policies/validate-traceability.mjs --structure-only
tools/local-dev/run-with-toolchain.sh node eng/release/prepare-task-import.mjs --check
tools/local-dev/run-with-toolchain.sh node eng/release/prepare-task-import.mjs --output-dir artifacts/task-system
```

The structure-only mode is the fast pre-build check and does not certify local test execution. The repository quality gate reruns the validator with `--compiled-tests` after the .NET build; every test evidence reference must then appear under the expected suite and class in compiled xUnit discovery. The same gate executes all six test projects with `failSkips: true` and runs a dynamic-skip negative probe, so a discovered but skipped evidence test cannot produce a passing gate.

Task-import output uses the `.preview` suffix while `externalEvidence.taskSystem` is pending and the `.verified` suffix after auditable readback. The current verified output contains the 38 unique GitHub Issue URLs and owner `@Lyon1984`. Six Epics intentionally have no direct R1.1 DEC/AC association: M3-E5, M5-E6, and the four post-GA M7 Epics; the exporter locks that set so future accidental mapping loss fails validation.
