import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const planPath = resolve(
  repoRoot,
  "docs/release-evidence/r1.1-certification-plan.json",
);
const executionSpecificationPath = resolve(
  repoRoot,
  "docs/开发执行规格-v1.0.md",
);
const certificationIndexPath = resolve(
  repoRoot,
  "docs/release-evidence/r1.1-certification-index.json",
);
const traceabilityPath = resolve(
  repoRoot,
  "docs/traceability/release-1-traceability.json",
);
const qualityGatePath = resolve(repoRoot, "eng/test/quality-gate.sh");

const plan = await readJson(planPath, "release certification plan");
const executionSpecification = await readFile(executionSpecificationPath, "utf8");
const certificationIndex = await readJson(
  certificationIndexPath,
  "release certification index",
);
const traceability = await readJson(traceabilityPath, "traceability manifest");
const qualityGate = await readFile(qualityGatePath, "utf8");

validatePlan(plan, executionSpecification);
validateDevelopmentStartChecklist(executionSpecification);
validateSensitiveMaterialIsAbsent(plan);
validateCertificationIndex(certificationIndex, plan.archive);
validateSensitiveMaterialIsAbsent(certificationIndex, "certification_index");
validateM0ExitState(traceability, plan);
validateQualityGateInvocation(qualityGate);
runNegativeProbes(plan, executionSpecification, traceability);

process.stdout.write("Release certification plan validation passed.\n");

async function readJson(path, label) {
  let text;
  try {
    text = await readFile(path, "utf8");
  } catch (error) {
    throw new Error(`${label} cannot be read: ${error.message}`);
  }

  try {
    assertNoDuplicateJsonObjectKeys(text, label);
    return JSON.parse(text);
  } catch (error) {
    if (error?.code === "DUPLICATE_JSON_KEY") {
      throw error;
    }
    throw new Error(`${label} is not valid JSON: ${error.message}`);
  }
}

function assertNoDuplicateJsonObjectKeys(text, label) {
  let cursor = 0;

  scanValue("$");
  skipWhitespace();
  if (cursor !== text.length) {
    throw new Error(`unexpected content at offset ${cursor}`);
  }

  function scanValue(path) {
    skipWhitespace();
    const character = text[cursor];
    if (character === "{") {
      scanObject(path);
      return;
    }
    if (character === "[") {
      scanArray(path);
      return;
    }
    if (character === '"') {
      scanString();
      return;
    }
    scanPrimitive();
  }

  function scanObject(path) {
    cursor += 1;
    skipWhitespace();
    if (text[cursor] === "}") {
      cursor += 1;
      return;
    }

    const keys = new Set();
    while (cursor < text.length) {
      skipWhitespace();
      if (text[cursor] !== '"') {
        throw new Error(`object key expected at offset ${cursor}`);
      }
      const key = scanString();
      if (keys.has(key)) {
        const error = new Error(`${label} contains duplicate JSON key ${JSON.stringify(key)} at ${path}.`);
        error.code = "DUPLICATE_JSON_KEY";
        throw error;
      }
      keys.add(key);

      skipWhitespace();
      if (text[cursor] !== ":") {
        throw new Error(`colon expected at offset ${cursor}`);
      }
      cursor += 1;
      scanValue(`${path}.${key}`);
      skipWhitespace();
      if (text[cursor] === "}") {
        cursor += 1;
        return;
      }
      if (text[cursor] !== ",") {
        throw new Error(`comma or object end expected at offset ${cursor}`);
      }
      cursor += 1;
    }
    throw new Error("unterminated object");
  }

  function scanArray(path) {
    cursor += 1;
    skipWhitespace();
    if (text[cursor] === "]") {
      cursor += 1;
      return;
    }

    let index = 0;
    while (cursor < text.length) {
      scanValue(`${path}[${index}]`);
      index += 1;
      skipWhitespace();
      if (text[cursor] === "]") {
        cursor += 1;
        return;
      }
      if (text[cursor] !== ",") {
        throw new Error(`comma or array end expected at offset ${cursor}`);
      }
      cursor += 1;
    }
    throw new Error("unterminated array");
  }

  function scanString() {
    const start = cursor;
    cursor += 1;
    while (cursor < text.length) {
      const character = text[cursor];
      if (character === '"') {
        cursor += 1;
        return JSON.parse(text.slice(start, cursor));
      }
      if (character === "\\") {
        cursor += 1;
        if (cursor >= text.length) {
          throw new Error("unterminated string escape");
        }
        if (text[cursor] === "u") {
          cursor += 1;
          if (!/^[0-9a-fA-F]{4}$/u.test(text.slice(cursor, cursor + 4))) {
            throw new Error(`invalid unicode escape at offset ${cursor - 2}`);
          }
          cursor += 4;
          continue;
        }
        cursor += 1;
        continue;
      }
      cursor += 1;
    }
    throw new Error("unterminated string");
  }

  function scanPrimitive() {
    const start = cursor;
    while (cursor < text.length && !/[\s,\]}]/u.test(text[cursor])) {
      cursor += 1;
    }
    if (cursor === start) {
      throw new Error(`JSON value expected at offset ${cursor}`);
    }
  }

  function skipWhitespace() {
    while (cursor < text.length && /\s/u.test(text[cursor])) {
      cursor += 1;
    }
  }
}

function validatePlan(value, specification) {
  expectRecord(value, "plan");
  expectExactKeys(
    value,
    [
      "schema_version",
      "release_id",
      "governing_contract",
      "reference_environment",
      "reference_hardware",
      "network",
      "upstream",
      "minimum_data_scale",
      "execution_policy",
      "scenarios",
      "reporting",
      "archive",
    ],
    "plan",
  );
  expectEqual(value.schema_version, 1, "plan.schema_version");
  expectEqual(value.release_id, "r1.1", "plan.release_id");

  expectRecord(value.governing_contract, "plan.governing_contract");
  expectExactKeys(
    value.governing_contract,
    ["path", "section"],
    "plan.governing_contract",
  );
  expectEqual(
    value.governing_contract.path,
    "docs/开发执行规格-v1.0.md",
    "plan.governing_contract.path",
  );
  expectEqual(
    value.governing_contract.section,
    "8.2",
    "plan.governing_contract.section",
  );

  const section = extractSection82(specification);
  validateReferenceEnvironment(value.reference_environment);
  validateReferenceHardware(value.reference_hardware, section);
  validateNetworkAndUpstream(value.network, value.upstream, section);
  validateMinimumDataScale(value.minimum_data_scale, section);
  validateExecutionPolicy(value.execution_policy, section);
  validateScenarios(value.scenarios, section);
  validateReporting(value.reporting, section);
  validateArchive(value.archive);
}

function validateReferenceEnvironment(environment) {
  expectRecord(environment, "plan.reference_environment");
  expectExactKeys(
    environment,
    [
      "logical_environment_id",
      "environment_class",
      "topology",
      "provisioning_status",
      "host_inventory",
    ],
    "plan.reference_environment",
  );
  expectEqual(
    environment.logical_environment_id,
    "r1.1-reference",
    "plan.reference_environment.logical_environment_id",
  );
  expectEqual(
    environment.environment_class,
    "dedicated-pre-production",
    "plan.reference_environment.environment_class",
  );
  expectEqual(
    environment.topology,
    "production-equivalent",
    "plan.reference_environment.topology",
  );
  expectEqual(
    environment.provisioning_status,
    "not-provisioned",
    "plan.reference_environment.provisioning_status",
  );
  expectArray(environment.host_inventory, "plan.reference_environment.host_inventory");
  if (environment.host_inventory.length !== 0) {
    throw new Error(
      "plan.reference_environment.host_inventory must stay empty while provisioning_status is not-provisioned.",
    );
  }
}

function validateReferenceHardware(hardware, section) {
  expectRecord(hardware, "plan.reference_hardware");
  expectExactKeys(
    hardware,
    ["api", "worker", "postgresql", "redis"],
    "plan.reference_hardware",
  );
  validateComputeUnit(
    hardware.api,
    "per-instance",
    4,
    8,
    "plan.reference_hardware.api",
  );
  validateComputeUnit(
    hardware.worker,
    "per-instance",
    2,
    4,
    "plan.reference_hardware.worker",
  );

  expectRecord(hardware.postgresql, "plan.reference_hardware.postgresql");
  expectExactKeys(
    hardware.postgresql,
    ["sizing_basis", "vcpu", "memory_gib", "storage", "connection_limit"],
    "plan.reference_hardware.postgresql",
  );
  expectEqual(
    hardware.postgresql.sizing_basis,
    "single-instance",
    "plan.reference_hardware.postgresql.sizing_basis",
  );
  expectEqual(hardware.postgresql.vcpu, 4, "plan.reference_hardware.postgresql.vcpu");
  expectEqual(
    hardware.postgresql.memory_gib,
    16,
    "plan.reference_hardware.postgresql.memory_gib",
  );
  expectEqual(
    hardware.postgresql.storage,
    "local-ssd",
    "plan.reference_hardware.postgresql.storage",
  );
  expectEqual(
    hardware.postgresql.connection_limit,
    "deployment-configured",
    "plan.reference_hardware.postgresql.connection_limit",
  );

  validateComputeUnit(
    hardware.redis,
    "single-instance",
    2,
    4,
    "plan.reference_hardware.redis",
  );

  expectContractText(
    section,
    "参考环境：单个 API 实例 4 vCPU/8 GiB；单个 Worker 2 vCPU/4 GiB；PostgreSQL 4 vCPU/16 GiB、本地 SSD、连接上限按配置；Redis 2 vCPU/4 GiB；服务间 RTT p95 ≤ 1 ms；上游使用可控 mock，固定响应/流量模型。",
    "reference hardware",
  );
}

function validateComputeUnit(value, sizingBasis, vcpu, memoryGib, label) {
  expectRecord(value, label);
  expectExactKeys(value, ["sizing_basis", "vcpu", "memory_gib"], label);
  expectEqual(value.sizing_basis, sizingBasis, `${label}.sizing_basis`);
  expectEqual(value.vcpu, vcpu, `${label}.vcpu`);
  expectEqual(value.memory_gib, memoryGib, `${label}.memory_gib`);
}

function validateNetworkAndUpstream(network, upstream, section) {
  expectRecord(network, "plan.network");
  expectExactKeys(network, ["service_to_service_rtt_p95_ms_max"], "plan.network");
  expectEqual(
    network.service_to_service_rtt_p95_ms_max,
    1,
    "plan.network.service_to_service_rtt_p95_ms_max",
  );

  expectRecord(upstream, "plan.upstream");
  expectExactKeys(
    upstream,
    ["kind", "response_model", "traffic_model"],
    "plan.upstream",
  );
  expectEqual(upstream.kind, "controlled-mock", "plan.upstream.kind");
  expectEqual(upstream.response_model, "fixed", "plan.upstream.response_model");
  expectEqual(upstream.traffic_model, "fixed", "plan.upstream.traffic_model");

  expectContractText(section, "服务间 RTT p95 ≤ 1 ms", "network RTT");
  expectContractText(section, "上游使用可控 mock，固定响应/流量模型", "controlled mock upstream");
}

function validateMinimumDataScale(scale, section) {
  expectRecord(scale, "plan.minimum_data_scale");
  expectExactKeys(
    scale,
    ["groups", "users", "api_keys", "accounts", "attempt_aggregate_records"],
    "plan.minimum_data_scale",
  );
  for (const [key, expected] of Object.entries({
    groups: 100,
    users: 1000,
    api_keys: 5000,
    accounts: 100,
    attempt_aggregate_records: 10000000,
  })) {
    expectEqual(scale[key], expected, `plan.minimum_data_scale.${key}`);
  }
  expectContractText(
    section,
    "测试数据至少 100 个 Group、1,000 个用户、5,000 个 Key、100 个 Account、1,000 万 attempt 聚合记录。",
    "minimum certification data scale",
  );
}

function validateExecutionPolicy(policy, section) {
  expectRecord(policy, "plan.execution_policy");
  expectExactKeys(
    policy,
    [
      "independent_runs_per_scenario",
      "same_build_required",
      "build_identity_fields",
      "all_runs_must_pass",
      "averages_only_are_sufficient",
    ],
    "plan.execution_policy",
  );
  expectEqual(
    policy.independent_runs_per_scenario,
    3,
    "plan.execution_policy.independent_runs_per_scenario",
  );
  expectEqual(
    policy.same_build_required,
    true,
    "plan.execution_policy.same_build_required",
  );
  expectStringArray(
    policy.build_identity_fields,
    [
      "commit_sha",
      "api_image_digest",
      "worker_image_digest",
      "migrator_image_digest",
    ],
    "plan.execution_policy.build_identity_fields",
  );
  expectEqual(
    policy.all_runs_must_pass,
    true,
    "plan.execution_policy.all_runs_must_pass",
  );
  expectEqual(
    policy.averages_only_are_sufficient,
    false,
    "plan.execution_policy.averages_only_are_sufficient",
  );
  expectContractText(
    section,
    "每个场景在相同构建上独立运行 3 次且全部通过。",
    "three-run same-build policy",
  );
  expectContractText(section, "只报告平均值不算通过。", "averages-only rejection");
}

function validateScenarios(scenarios, section) {
  expectArray(scenarios, "plan.scenarios");
  const expectedScenarios = [
    { id: "same-group-hot-reservation", name: "同 Group 热点预留" },
    { id: "mixed-non-streaming", name: "混合非流式" },
    { id: "sse-concurrency", name: "SSE 并发" },
    { id: "bulkhead-isolation", name: "舱壁隔离" },
    { id: "breaker-herd", name: "breaker 惊群" },
    { id: "long-non-streaming", name: "长非流" },
    { id: "usage-query", name: "`/v1/usage`" },
    { id: "worker-backlog-recovery", name: "Worker 积压恢复" },
    { id: "worker-single-owner", name: "Worker 单 owner" },
    { id: "failover", name: "故障切换" },
    { id: "process-crash", name: "进程崩溃" },
  ];
  const contractScenarios = parseScenarioTable(section);
  if (contractScenarios.length !== expectedScenarios.length) {
    throw new Error(
      `docs/开发执行规格-v1.0.md section 8.2 must contain exactly ${expectedScenarios.length} certification scenarios.`,
    );
  }
  if (scenarios.length !== expectedScenarios.length) {
    throw new Error(
      `plan.scenarios must contain exactly ${expectedScenarios.length} entries.`,
    );
  }

  const contractByName = new Map(
    contractScenarios.map((scenario) => [scenario.name, scenario]),
  );
  if (contractByName.size !== contractScenarios.length) {
    throw new Error("Section 8.2 certification scenario names must be unique.");
  }

  for (const [index, scenario] of scenarios.entries()) {
    const label = `plan.scenarios[${index}]`;
    const expectedScenario = expectedScenarios[index];
    const contractScenario = contractByName.get(expectedScenario.name);
    if (!contractScenario) {
      throw new Error(
        `Section 8.2 is missing the stable certification scenario ${expectedScenario.name}.`,
      );
    }
    expectRecord(scenario, label);
    expectExactKeys(scenario, ["id", "name", "workload", "pass_threshold"], label);
    expectEqual(scenario.id, expectedScenario.id, `${label}.id`);
    expectEqual(scenario.name, expectedScenario.name, `${label}.name`);
    expectEqual(scenario.workload, contractScenario.workload, `${label}.workload`);
    expectEqual(
      scenario.pass_threshold,
      contractScenario.passThreshold,
      `${label}.pass_threshold`,
    );
  }

  if (new Set(scenarios.map((scenario) => scenario.id)).size !== scenarios.length) {
    throw new Error("plan.scenarios ids must be unique.");
  }
}

function validateReporting(reporting, section) {
  expectRecord(reporting, "plan.reporting");
  expectExactKeys(
    reporting,
    ["execution_status", "reports", "required_report_fields"],
    "plan.reporting",
  );
  expectEqual(reporting.execution_status, "not-run", "plan.reporting.execution_status");
  expectArray(reporting.reports, "plan.reporting.reports");
  if (reporting.reports.length !== 0) {
    throw new Error("plan.reporting.reports must be empty while execution_status is not-run.");
  }
  expectStringArray(
    reporting.required_report_fields,
    [
      "commit_sha",
      "api_image_digest",
      "worker_image_digest",
      "migrator_image_digest",
      "configuration",
      "hardware",
      "data_scale",
      "scripts",
      "raw_metrics",
      "conclusion",
    ],
    "plan.reporting.required_report_fields",
  );
  expectContractText(
    section,
    "报告必须保存 commit/image digest、配置、硬件、数据规模、脚本、原始指标和结论；",
    "required report fields",
  );
}

function validateArchive(archive) {
  expectRecord(archive, "plan.archive");
  expectExactKeys(
    archive,
    [
      "authoritative_store",
      "repository",
      "releases_url",
      "retention",
      "release_tag_pattern",
      "asset_name_pattern",
      "index_path",
      "digest_algorithm",
      "github_actions_artifacts",
    ],
    "plan.archive",
  );
  expectEqual(
    archive.authoritative_store,
    "github-release-assets",
    "plan.archive.authoritative_store",
  );
  expectEqual(archive.repository, "Lyon1984/PoolAI", "plan.archive.repository");
  expectEqual(
    archive.releases_url,
    "https://github.com/Lyon1984/PoolAI/releases",
    "plan.archive.releases_url",
  );
  expectEqual(archive.retention, "long-term", "plan.archive.retention");
  expectEqual(
    archive.release_tag_pattern,
    "r1.1-rc.*",
    "plan.archive.release_tag_pattern",
  );
  expectEqual(
    archive.asset_name_pattern,
    "poolai-r1.1-certification-{commit_sha}.tar.zst",
    "plan.archive.asset_name_pattern",
  );
  if (
    archive.asset_name_pattern.includes("/") ||
    archive.asset_name_pattern.includes("\\")
  ) {
    throw new Error("plan.archive.asset_name_pattern must be a GitHub asset name, not a path.");
  }
  expectEqual(
    archive.index_path,
    "docs/release-evidence/r1.1-certification-index.json",
    "plan.archive.index_path",
  );
  validateRelativePath(archive.index_path, "plan.archive.index_path");
  expectEqual(archive.digest_algorithm, "sha256", "plan.archive.digest_algorithm");

  expectRecord(
    archive.github_actions_artifacts,
    "plan.archive.github_actions_artifacts",
  );
  expectExactKeys(
    archive.github_actions_artifacts,
    ["role", "authoritative"],
    "plan.archive.github_actions_artifacts",
  );
  expectEqual(
    archive.github_actions_artifacts.role,
    "transport-only",
    "plan.archive.github_actions_artifacts.role",
  );
  expectEqual(
    archive.github_actions_artifacts.authoritative,
    false,
    "plan.archive.github_actions_artifacts.authoritative",
  );
}

function validateCertificationIndex(index, archive) {
  expectRecord(index, "certification_index");
  expectExactKeys(
    index,
    ["schema_version", "release_id", "archive_url", "entries"],
    "certification_index",
  );
  expectEqual(index.schema_version, 1, "certification_index.schema_version");
  expectEqual(index.release_id, "r1.1", "certification_index.release_id");
  expectEqual(
    index.archive_url,
    archive.releases_url,
    "certification_index.archive_url",
  );
  expectArray(index.entries, "certification_index.entries");
  if (index.entries.length !== 0) {
    throw new Error("certification_index.entries must stay empty while M6 certification is not run.");
  }
}

function validateDevelopmentStartChecklist(specification) {
  const marker = "## 13. 开发启动清单";
  const start = specification.indexOf(marker);
  if (start === -1) {
    throw new Error("docs/开发执行规格-v1.0.md is missing the development-start checklist.");
  }
  const section = specification.slice(start);
  const checked = section
    .split(/\r?\n/u)
    .filter((line) => line.startsWith("- [x] "));
  const unchecked = section
    .split(/\r?\n/u)
    .filter((line) => line.startsWith("- [ ] "));
  if (checked.length !== 10 || unchecked.length !== 0) {
    throw new Error(
      "Development-start checklist must contain exactly 10 checked items and no unchecked items.",
    );
  }

  for (const requiredBoundary of [
    "全部勾选仍不授权进入 M1",
    "externalEvidence.m0ExitReview",
    "独立永久评论标记为 `verified` 后，团队才进入 M1 功能编码",
    "若缺少任一清单项或 M0 Exit 仍为 `pending`，只能继续 M0",
  ]) {
    if (!section.includes(requiredBoundary)) {
      throw new Error(
        `Development-start checklist must preserve the M0 Exit boundary: ${requiredBoundary}`,
      );
    }
  }

  const planLink =
    "[`r1.1-certification-plan.json`](release-evidence/r1.1-certification-plan.json)";
  const planItem = checked.find((line) => line.includes("R1.1 发布认证逻辑环境"));
  if (!planItem || !planItem.includes(planLink)) {
    throw new Error(
      "Development-start checklist must link its R1.1 certification item to the repository-relative plan.",
    );
  }
}

function validateM0ExitState(traceability, validatedPlan) {
  const evidence = traceability?.externalEvidence;
  expectRecord(evidence, "traceability.externalEvidence");
  const exitReview = evidence.m0ExitReview;
  expectRecord(exitReview, "traceability.externalEvidence.m0ExitReview");
  if (!new Set(["pending", "verified"]).has(exitReview.status)) {
    throw new Error(
      "traceability.externalEvidence.m0ExitReview.status must be pending or verified.",
    );
  }
  if (exitReview.status === "pending" && exitReview.evidence !== null) {
    throw new Error(
      "traceability.externalEvidence.m0ExitReview.evidence must be null while pending.",
    );
  }
  if (exitReview.status !== "verified") {
    return;
  }

  if (
    typeof exitReview.evidence !== "string" ||
    !/^https:\/\/github\.com\/Lyon1984\/PoolAI\/issues\/44#issuecomment-[1-9][0-9]*$/.test(
      exitReview.evidence,
    )
  ) {
    throw new Error(
      "A verified M0 exit review must use a permanent Lyon1984/PoolAI Issue #44 comment URL.",
    );
  }
  for (const prerequisite of [
    "taskSystem",
    "decisionSignoff",
    "databaseReview",
    "openApiReview",
    "targetCi",
  ]) {
    if (evidence[prerequisite]?.status !== "verified") {
      throw new Error(
        `M0 exit review cannot be verified while ${prerequisite} remains pending.`,
      );
    }
  }
  const prerequisiteEvidence = [
    "taskSystem",
    "decisionSignoff",
    "databaseReview",
    "openApiReview",
    "targetCi",
  ].map((key) => evidence[key]?.evidence);
  if (prerequisiteEvidence.includes(exitReview.evidence)) {
    throw new Error(
      "M0 exit review must use its own permanent comment and cannot reuse prerequisite evidence.",
    );
  }

  // validatePlan has already fail-closed on the full certification-plan shape.
  expectEqual(
    validatedPlan.reference_environment.logical_environment_id,
    "r1.1-reference",
    "verified M0 exit certification environment",
  );
  expectEqual(
    validatedPlan.archive.authoritative_store,
    "github-release-assets",
    "verified M0 exit certification archive",
  );
}

function validateQualityGateInvocation(script) {
  const command = "node eng/release/validate-release-certification-plan.mjs";
  const directInvocations = script
    .split(/\r?\n/u)
    .filter((line) => line.trim() === command);
  if (directInvocations.length !== 1) {
    throw new Error(
      `eng/test/quality-gate.sh must invoke '${command}' directly exactly once.`,
    );
  }
}

function runNegativeProbes(validPlan, specification, validTraceability) {
  assertNoDuplicateJsonObjectKeys(
    '{"text":"escaped \\\" quote and { [ ] }","nested":[{"value":"\\u007b"}]}',
    "string-escape probe",
  );

  const unknownField = structuredClone(validPlan);
  unknownField.unreviewed = true;
  expectProbeRejected("unknown plan field", () => validatePlan(unknownField, specification));

  expectProbeRejected("duplicate nested JSON key", () =>
    assertNoDuplicateJsonObjectKeys(
      '{"outer":[{"safe":1,"\\u0073afe":2}]}',
      "duplicate-key probe",
    ),
  );

  expectProbeRejected("private host", () =>
    validateSensitiveMaterialIsAbsent({ host: "codex-203-0-113-10" }, "probe"),
  );

  const provisioned = structuredClone(validPlan);
  provisioned.reference_environment.provisioning_status = "provisioned";
  expectProbeRejected("forged provisioning status", () =>
    validatePlan(provisioned, specification),
  );

  const forgedStatus = structuredClone(validPlan);
  forgedStatus.reporting.execution_status = "passed";
  expectProbeRejected("forged execution status", () =>
    validatePlan(forgedStatus, specification),
  );

  const forgedReport = structuredClone(validPlan);
  forgedReport.reporting.reports.push({ conclusion: "passed" });
  expectProbeRejected("forged load report", () => validatePlan(forgedReport, specification));

  const reusedSignoff = structuredClone(validTraceability);
  reusedSignoff.externalEvidence.m0ExitReview = {
    status: "verified",
    evidence: reusedSignoff.externalEvidence.decisionSignoff.evidence,
  };
  expectProbeRejected("reused prerequisite sign-off", () =>
    validateM0ExitState(reusedSignoff, validPlan),
  );
}

function expectProbeRejected(label, action) {
  try {
    action();
  } catch {
    return;
  }
  throw new Error(`Validator negative probe did not reject ${label}.`);
}

function validateSensitiveMaterialIsAbsent(value, path = "plan") {
  if (Array.isArray(value)) {
    for (const [index, item] of value.entries()) {
      validateSensitiveMaterialIsAbsent(item, `${path}[${index}]`);
    }
    return;
  }
  if (isRecord(value)) {
    for (const [key, item] of Object.entries(value)) {
      if (
        /(?:password|passwd|client_secret|private_key|access_token|refresh_token|api_token|connection_string)/iu.test(
          key,
        )
      ) {
        throw new Error(`${path}.${key} is a forbidden secret-bearing field.`);
      }
      validateSensitiveMaterialIsAbsent(item, `${path}.${key}`);
    }
    return;
  }
  if (typeof value !== "string") {
    return;
  }

  const forbiddenValuePatterns = [
    /-----BEGIN [A-Z ]*PRIVATE KEY-----/u,
    /(?:postgres(?:ql)?|redis):\/\//iu,
    /authorization\s*:\s*bearer\s+/iu,
    /(?:password|passwd|secret|access[_-]?token|refresh[_-]?token)\s*[=:]\s*\S+/iu,
    /(?:^|[^0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?:$|[^0-9])/u,
    /(?:^|[\s[/])(?:::1|f[cd][0-9a-f]{2}:|fe80:)/iu,
    /(?:^|[\s/:.])(?:localhost|host\.docker\.internal)(?:$|[\s/:.])/iu,
    /(?:\.local|\.lan|\.internal)(?:$|[\s/:])/iu,
    /(?:^|[\s/:])codex-[0-9-]+(?:$|[\s/:])/iu,
  ];
  if (forbiddenValuePatterns.some((pattern) => pattern.test(value))) {
    throw new Error(`${path} contains a private host, IP literal, or secret-like value.`);
  }

  if (/^[a-z][a-z0-9+.-]*:\/\//iu.test(value)) {
    let url;
    try {
      url = new URL(value);
    } catch {
      throw new Error(`${path} contains an invalid URL.`);
    }
    if (
      url.protocol !== "https:" ||
      url.hostname !== "github.com" ||
      value !== "https://github.com/Lyon1984/PoolAI/releases"
    ) {
      throw new Error(`${path} may only contain the canonical GitHub Releases URL.`);
    }
  }
}

function extractSection82(specification) {
  const startMarker = "### 8.2 参考硬件与认证门槛";
  const start = specification.indexOf(startMarker);
  if (start === -1) {
    throw new Error("docs/开发执行规格-v1.0.md is missing section 8.2.");
  }
  const end = specification.indexOf("\n## 9.", start + startMarker.length);
  if (end === -1) {
    throw new Error("docs/开发执行规格-v1.0.md section 8.2 has no section 9 boundary.");
  }
  return specification.slice(start, end);
}

function parseScenarioTable(section) {
  const rows = [];
  for (const line of section.split(/\r?\n/u)) {
    if (!line.startsWith("|")) {
      continue;
    }
    const cells = line
      .slice(1, line.endsWith("|") ? -1 : undefined)
      .split("|")
      .map((cell) => cell.trim());
    if (
      cells.length !== 3 ||
      cells[0] === "场景" ||
      cells.every((cell) => /^-+$/u.test(cell))
    ) {
      continue;
    }
    rows.push({
      name: cells[0],
      workload: cells[1],
      passThreshold: cells[2],
    });
  }
  return rows;
}

function expectContractText(section, expected, label) {
  if (!section.includes(expected)) {
    throw new Error(`Section 8.2 ${label} no longer matches the certification plan.`);
  }
}

function validateRelativePath(value, label) {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.startsWith("/") ||
    value.includes("\\") ||
    value.split("/").some((segment) => segment === "" || segment === "." || segment === "..")
  ) {
    throw new Error(`${label} must be a safe repository-relative POSIX path.`);
  }
}

function expectRecord(value, label) {
  if (!isRecord(value)) {
    throw new Error(`${label} must be an object.`);
  }
}

function expectArray(value, label) {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }
}

function expectExactKeys(value, expected, label) {
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(wanted)) {
    throw new Error(`${label} must define exactly: ${expected.join(", ")}.`);
  }
}

function expectEqual(actual, expected, label) {
  if (!Object.is(actual, expected)) {
    throw new Error(`${label} must equal ${JSON.stringify(expected)}.`);
  }
}

function expectStringArray(actual, expected, label) {
  expectArray(actual, label);
  if (
    !actual.every((value) => typeof value === "string") ||
    JSON.stringify(actual) !== JSON.stringify(expected)
  ) {
    throw new Error(`${label} must equal ${JSON.stringify(expected)}.`);
  }
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
