import { createHash } from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { basename, resolve } from 'node:path'
import {
  auditableReferencePolicyCases,
  canonicalAuditableTaskId,
  isAuditableHttpsReference,
  isAuditableTaskId,
  isNamedOwner,
  namedOwnerPolicyCases,
  taskIdPolicyCases,
} from './auditable-reference.mjs'
import {
  repositoryFileErrorCodes,
  resolveCanonicalRepositoryPath,
  withReadOnlyRepositoryFile,
} from './repository-file.mjs'

const root = resolve(import.meta.dirname, '..', '..')
const manifestPath = resolve(root, 'docs/traceability/release-1-traceability.json')
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
const executionSpec = readFileSync(resolve(root, 'docs/开发执行规格-v1.0.md'), 'utf8')
const systemPlan = readFileSync(resolve(root, 'docs/系统重构方案-v1.0.md'), 'utf8')
const adr0006Path = 'docs/architecture/adr/0006-register-group-subscription-lifecycle-fence.md'
const systemPlanPath = 'docs/系统重构方案-v1.0.md'
const currentStatePath = 'docs/project-memory/current-state.md'
const adr0006ProtectedSigningFiles = [adr0006Path, systemPlanPath, currentStatePath]
const adr0006 = readFileSync(
  resolve(root, adr0006Path),
  'utf8',
)
const qualityGate = readFileSync(resolve(root, 'eng/test/quality-gate.sh'), 'utf8')
const qualityGateWorkflow = readFileSync(resolve(root, '.github/workflows/quality-gate.yml'), 'utf8')
const currentState = readFileSync(resolve(root, currentStatePath), 'utf8')
const frontendPackage = JSON.parse(readFileSync(resolve(root, 'frontend/package.json'), 'utf8'))
const xunitRunnerConfig = JSON.parse(readFileSync(resolve(root, 'tests/xunit.runner.json'), 'utf8'))
const failures = []
const validationModes = process.argv.slice(2)
const structureOnly = validationModes.length === 1 && validationModes[0] === '--structure-only'
const compiledTests = validationModes.length === 1 && validationModes[0] === '--compiled-tests'
const githubEvidence = validationModes.length === 1 && validationModes[0] === '--github-evidence'
const expectedDecisionIds = Array.from({ length: 42 }, (_, index) => `DEC-${String(index + 1).padStart(3, '0')}`)
const expectedAcceptanceIds = Array.from({ length: 45 }, (_, index) => `AC-${String(index + 1).padStart(3, '0')}`)
const allowedSuites = new Set([
  'PoolAI.UnitTests',
  'PoolAI.ArchitectureTests',
  'PoolAI.ContractTests',
  'PoolAI.IntegrationTests',
  'PoolAI.EndToEndTests',
  'PoolAI.LoadTests',
])
const externalKeys = [
  'taskSystem',
  'decisionSignoff',
  'databaseReview',
  'openApiReview',
  'targetCi',
  'm0ExitReview',
]
// This review lock covers only ID -> primary workstream/direct Epic mappings.
// Update it only together with the authoritative plan and traceability review.
const expectedMappingDigest = '810e7ac50d7e46a2eef32de82213048f955b31aa449415f50c27b71a4cf411f2'

const fail = (message) => failures.push(message)
const sorted = (values) => [...values].sort()
const sameValues = (left, right) => JSON.stringify(sorted(left)) === JSON.stringify(sorted(right))
const duplicates = (values) => [...new Set(values.filter((value, index) => values.indexOf(value) !== index))]
const semanticMarkdownText = (source) => source
  .replaceAll(/\]\([^\r\n)]*\)/gu, ']')
  .replaceAll(/&#(?:x([0-9a-f]+)|([0-9]+));/giu, (_match, hex, decimal) => {
    const value = Number.parseInt(hex ?? decimal, hex ? 16 : 10)
    return Number.isSafeInteger(value) && value >= 0 && value <= 0x10ffff
      ? String.fromCodePoint(value)
      : ''
  })
  .replaceAll(/&[A-Za-z][A-Za-z0-9]+;/gu, '')
  .replaceAll(/\\([\\`*_[\]{}()#+.!~>-])/gu, '$1')
  .replaceAll(/[\*_`~\[\]]/gu, '')
  .normalize('NFKC')
  .replaceAll(/[\p{Cf}\uFE00-\uFE0F\u{E0100}-\u{E01EF}]/gu, '')
const forbiddenAdr0006GovernanceHtmlPattern = /<(?:\/?[A-Za-z]|[!?])/u
const adr0006ReferenceSource =
  String.raw`(?<![\p{L}\p{N}])ADR[^\p{L}\p{N}\r\n]{0,16}0006(?![\p{L}\p{N}])`
const adr0006ReferencePattern = new RegExp(adr0006ReferenceSource, 'iu')
const adr0006ReferenceScanPattern = new RegExp(adr0006ReferenceSource, 'giu')
const hasAdr0006Reference = (source) => adr0006ReferencePattern.test(semanticMarkdownText(source))
const countAdr0006References = (source) => [
  ...semanticMarkdownText(source)
    .replaceAll(/\r?\n/gu, ' ')
    .matchAll(adr0006ReferenceScanPattern),
].length

const adr0006ApprovalUrlPattern =
  /^https:\/\/github\.com\/Lyon1984\/PoolAI\/issues\/44#issuecomment-[1-9][0-9]*$/u
const adr0006CiUrlPattern =
  /^https:\/\/github\.com\/Lyon1984\/PoolAI\/actions\/runs\/[1-9][0-9]*$/u
const adr0006ProposedDecider =
  'PoolAI architecture owner (`@Lyon1984`) — pending explicit approval'
const adr0006AcceptedDecider = 'PoolAI architecture owner (`@Lyon1984`)'
const adr0006ApprovalLinkLabel = 'Issue #44 approval comment'
const adr0006PlanApprovalLinkLabel = 'Issue #44 永久批准评论'
const adr0006PlanSectionHeading = '### 6.2 模型请求 Process Manager'
const adr0006PlanStatusPrefix = '- ADR 0006 治理状态：'
const adr0006PlanScopeBullet = [
  '- 按 [`ADR 0006`](architecture/adr/0006-register-group-subscription-lifecycle-fence.md)，跨 Context 数据库 `SELECT`/行锁候选 registry 只含三个 family：',
  '**Family A** 是 `poolai_quota_reserve` 的 canonical admission 与既有结算路径的 route/provider identity 验证；',
  '**Family B** 是 `poolai_validate_group_activation` 的点时 activation guard；',
  '**Family C** 是 Group–Subscription lifecycle fence。',
  '精确函数、表、字段、锁序和等待后数据库时钟规则只在架构与数据库契约维护；',
  '三类均不授权跨 Context 写入、共享 UoW 或通用 SQL executor。',
  '旧 activation evidence 或 Redis snapshot 也不能代替 Family A 的强读。',
].join('')
const adr0006ProposedPlanStatus =
  '- ADR 0006 治理状态：**Proposed**（待 `@Lyon1984` 明确批准；不是 M1-E4 architecture sign-off 或 release-ready 证据）。'
const adr0006AcceptedPlanStatus = (approvalUrl) =>
  `- ADR 0006 治理状态：**Accepted**（[${adr0006PlanApprovalLinkLabel}](${approvalUrl})）。`
const adr0006MemoryStatusPrefix = adr0006PlanStatusPrefix
const adr0006ProposedMemoryStatus = adr0006ProposedPlanStatus
const adr0006AcceptedMemoryStatus = adr0006AcceptedPlanStatus
const adr0006AllowedCurrentStateReferenceDigests = new Set([
  'a7659e62b990cd15bc48cd908ba0b2a985a2c5dc06e41a5ebfe9ccb08e2a7531',
  'f94337f80152dce6c203be2951ce6e2ffd666c1bbaaadfa32d3c6cfd6e9c0b7f',
  '1c8e5033cb15964ea2891aa0ee88810d57333bb8f784649a1c07588d9da7009a',
  'ec9fded9de936a98a42e8778d6139e6fcac6fa6c92492bc69d302aa5e424dff0',
])
// Accepted bases may predate a reviewed current-state refresh. Historical
// digests remain valid only for the exact Git blob that originally carried
// them; they never widen validation of the event head or checkout.
const adr0006HistoricalBaseReferenceDigestsByMemoryBlob = new Map([
  [
    'fda527e20894738aaba7f5e8b7f96adae156e9b7',
    new Set([
      'a7659e62b990cd15bc48cd908ba0b2a985a2c5dc06e41a5ebfe9ccb08e2a7531',
      'f94337f80152dce6c203be2951ce6e2ffd666c1bbaaadfa32d3c6cfd6e9c0b7f',
      '446bfca9e6bd847341176d6cb530f507bbbefd20d07c03a6f6c5facefc417d15',
      'ce0fa59682a2b4b72933e75ca06cbc56ece3a9ce51db03ac4cf9b5fe0ef3f5d7',
    ]),
  ],
])
const selectAdr0006CurrentStateReferenceDigests = (
  memoryBlob,
  allowHistoricalBase,
) => allowHistoricalBase
  ? adr0006HistoricalBaseReferenceDigestsByMemoryBlob.get(memoryBlob)
    ?? adr0006AllowedCurrentStateReferenceDigests
  : adr0006AllowedCurrentStateReferenceDigests

if (adr0006HistoricalBaseReferenceDigestsByMemoryBlob.size !== 1) {
  fail('ADR 0006 historical current-state policy must register exactly one reviewed base blob.')
}
for (const [memoryBlob, digests] of adr0006HistoricalBaseReferenceDigestsByMemoryBlob) {
  if (!/^[0-9a-f]{40}$/u.test(memoryBlob)
      || digests.size !== 4
      || [...digests].some((digest) => !/^[0-9a-f]{64}$/u.test(digest))
      || sameValues(digests, adr0006AllowedCurrentStateReferenceDigests)) {
    fail('ADR 0006 historical current-state policy contains an invalid blob or digest set.')
  }
  if (selectAdr0006CurrentStateReferenceDigests(memoryBlob, true) !== digests
      || selectAdr0006CurrentStateReferenceDigests(memoryBlob, false)
        !== adr0006AllowedCurrentStateReferenceDigests) {
    fail('ADR 0006 historical current-state digests escaped their exact base-only blob binding.')
  }
}
if (selectAdr0006CurrentStateReferenceDigests('0'.repeat(40), true)
    !== adr0006AllowedCurrentStateReferenceDigests) {
  fail('ADR 0006 historical current-state policy accepted an unknown base blob.')
}
const adr0006ApprovalTemplate = [
  'APPROVED: ADR 0006 — Freeze the cross-context database read/lock allowlist',
  '',
  'I, @Lyon1984, approve exactly these three exception families:',
  '- Family A: GroupQuota canonical admission and route-identity validation, including only the registered Account/Channel id + provider locks on settle, mark-dispatched, and adjust-usage.',
  '- Family B: the registered Group activation guard.',
  '- Family C: the registered bidirectional Group–Subscription lifecycle fence.',
  '',
  'I approve only the stated cross-context SELECT/row locks, the global Quota → Group → Template/Subscription lock order, post-wait PostgreSQL clock checks, and short transactions.',
  '',
  'Excluded: every additional function, table, field, consumer, or lock direction; cross-context DML or trigger-side mutation; dynamic or generic SQL; shared DbContext, Repository, or Unit of Work; and HTTP, SMTP, Redis, SSE, backoff, or other external waits. This comment does not approve database migration 0007, its manifest/checksum or remote execution, PR merge, Issue closure, M1-E4 completion, RC/GA, deployment, or production release.',
  '',
  'Approved candidate head: `<APPROVED_CANDIDATE_SHA>`',
  'Approved quality-gate run: <APPROVED_QUALITY_RUN_URL>',
  'Approved security-evidence run: <APPROVED_SECURITY_RUN_URL>',
].join('\n')
const adr0006ApprovalTemplateBegin = '<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_BEGIN -->'
const adr0006ApprovalTemplateEnd = '<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_END -->'
const adr0006ApprovalTemplateBlock = [
  adr0006ApprovalTemplateBegin,
  '```text',
  adr0006ApprovalTemplate,
  '```',
  adr0006ApprovalTemplateEnd,
].join('\n')
const adr0006SigningLifecycleApprovalUrl =
  'https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5046436932'
const adr0006SigningLifecycleApprovalBody = [
  '## ADR 0006 签署门禁生命周期解释：APPROVED',
  '',
  'ADR 0006 SIGNING-GATE LIFECYCLE CLARIFICATION: APPROVED',
  'Signer: @Lyon1984',
  'Approval control: Issue #44',
  'Related ADR approval: https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5011030600',
  'Reviewed Draft PR: #57',
  'Approved candidate: `2ef8368f3a8df8286a64d7e4bb90286e3859d71a`',
  'Historical signing transition: `bbfa45b6a1b3c75c4f2d988215a1511f8261838b`',
  '',
  'APPROVED：确认 ADR 0006 中“candidate-to-signing-head 只修改三份治理文件”的规则约束唯一的 Proposed → Accepted 签署 transition，不将 PR #57 永久冻结在该 transition 提交。',
  '',
  '1. 当前 PR 的 candidate 至事件 head 历史必须包含且仅包含一个非 merge 的 Proposed → Accepted transition；该 transition 必须继续满足 ADR 0006 原有三文件精确差异、metadata、normalization、评论及候选 CI 证据要求。',
  '2. transition 之后允许提交非治理开发文件，但 ADR 0006、系统重构方案和 current-state 三份签署文件必须在每个后继提交、PR event head 及 GitHub synthetic checkout 中保持相同的 `100644 blob` 身份。',
  '3. 未来以 Accepted ADR 为基线的 PR，ADR 文件必须在 base、event head 和 checkout 之间保持字节及 mode 完全一致；系统方案和 current-state 必须继续通过 canonical Accepted 状态与永久评论引用校验，但其他合法内容可以演进。',
  '4. Accepted → Proposed 回滚、零个或多个签署 transition、merge transition、缺失或畸形状态、签署后治理文件漂移均必须 fail closed。',
  '5. `push` 到 main 仍可按 ADR 0006 原规则跳过首次签署 ancestry 检查以支持 squash merge，但不得跳过 GitHub 评论、作者、正文、候选、CI、workflow、run head、URL 或仓库身份验证。',
  '',
  '本解释不修改或扩大 ADR 0006 的三类跨 Context allowlist，不批准 migration 0007、manifest/checksum、远程数据库执行、PR ready/merge、Issue 关闭、M1-E4/M1 完成、部署、RC、GA 或生产发布。',
  '',
  '执行说明：本评论由 Codex 在取得 @Lyon1984 对上述完整文本的明确确认后，通过其已认证的 GitHub 账号代为发布。',
].join('\n') + '\n'

const adrMetadataValues = (source, label) => {
  const escapedLabel = label.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')
  return [...source.matchAll(new RegExp(`^- ${escapedLabel}: ([^\\r\\n]*)$`, 'gmu'))]
    .map((match) => match[1])
}

const adrMetadataLikeCount = (source, label) => {
  const escapedLabel = label.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')
  return [...source.matchAll(new RegExp(`^-\\s*${escapedLabel}\\s*:`, 'gimu'))].length
}

const parseExactMarkdownLink = (source, expectedLabel) => {
  const match = source.match(/^\[([^\]\r\n]+)\]\(([^()\s]+)\)$/u)
  if (!match || match[1] !== expectedLabel) {
    return null
  }
  return match[2]
}

const extractAdr0006ApprovalTemplate = (source) => {
  const markerCounts = [adr0006ApprovalTemplateBegin, adr0006ApprovalTemplateEnd]
    .map((marker) => source.split(marker).length - 1)
  const matches = [...source.matchAll(
    /<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_BEGIN -->\r?\n```text\r?\n([\s\S]*?)\r?\n```\r?\n<!-- ADR0006_APPROVAL_COMMENT_TEMPLATE_END -->/gu,
  )]
  if (markerCounts.some((count) => count !== 1) || matches.length !== 1) {
    return null
  }
  return matches[0][1].replaceAll('\r\n', '\n')
}

const extractSystemPlanSection62 = (source) => {
  const sectionFailures = []
  const headings = [...source.matchAll(/^### 6\.2(?:[ \t]+[^\r\n]+)?$/gmu)]
  if (headings.length !== 1 || headings[0][0] !== adr0006PlanSectionHeading) {
    sectionFailures.push(`The system plan must contain exactly one ${adr0006PlanSectionHeading} heading.`)
    return { section: null, failures: sectionFailures }
  }
  const section = source.match(
    /^### 6\.2(?:[ \t]+[^\r\n]+)?\r?\n[\s\S]*?(?=^### 6\.3(?:[ \t]+[^\r\n]+)?$)/mu,
  )?.[0] ?? null
  if (!section) {
    sectionFailures.push('The system-plan section 6.2 must end at one following section 6.3 heading.')
  }
  return { section, failures: sectionFailures }
}

const validateAdr0006Governance = (adrSource, planSource) => {
  const governanceFailures = []
  const reject = (message) => governanceFailures.push(message)
  if (forbiddenAdr0006GovernanceHtmlPattern.test(planSource)) {
    reject('The system plan must not contain HTML comments or tags because they can hide ADR status text.')
  }
  const statusValues = adrMetadataValues(adrSource, 'Status')
  const deciderValues = adrMetadataValues(adrSource, 'Decider')
  const approvalValues = adrMetadataValues(adrSource, 'Approval evidence')
  const candidateHeadValues = adrMetadataValues(adrSource, 'Approved candidate head')
  const approvedCiValues = adrMetadataValues(adrSource, 'Approved CI')
  const candidateHeadLikeCount = adrMetadataLikeCount(adrSource, 'Approved candidate head')
  const approvedCiLikeCount = adrMetadataLikeCount(adrSource, 'Approved CI')
  const planSectionResult = extractSystemPlanSection62(planSource)
  for (const sectionFailure of planSectionResult.failures) {
    reject(sectionFailure)
  }
  const planSection = planSectionResult.section ?? ''

  if (statusValues.length !== 1 || adrMetadataLikeCount(adrSource, 'Status') !== 1) {
    reject('ADR 0006 must declare exactly one Status metadata field.')
  }
  if (deciderValues.length !== 1 || adrMetadataLikeCount(adrSource, 'Decider') !== 1) {
    reject('ADR 0006 must declare exactly one Decider metadata field.')
  }
  if (approvalValues.length !== 1 || adrMetadataLikeCount(adrSource, 'Approval evidence') !== 1) {
    reject('ADR 0006 must declare exactly one Approval evidence metadata field.')
  }
  if (extractAdr0006ApprovalTemplate(adrSource) !== adr0006ApprovalTemplate) {
    reject('ADR 0006 must declare exactly one unchanged canonical APPROVED comment template.')
  }

  const status = statusValues[0]?.match(/^\*\*(Proposed|Accepted)\*\*$/u)?.[1]
  if (!status) {
    reject('ADR 0006 Status must be exactly **Proposed** or **Accepted**.')
    return governanceFailures
  }

  const decider = deciderValues[0] ?? ''
  const approval = approvalValues[0] ?? ''
  const planLines = planSection.split(/\r?\n/u)
  const fullPlanLines = planSource.split(/\r?\n/u)
  const planStatusLines = planLines
    .filter((line) => line.startsWith(adr0006PlanStatusPrefix))
  const planAdr0006Lines = fullPlanLines
    .filter((line) => hasAdr0006Reference(line))
  const planStatusTokens = [...planSection.matchAll(/\b(?:Proposed|Accepted)\b/giu)]
  const planIssueCommentOccurrences = [...planSection.matchAll(
    /https:\/\/github\.com\/Lyon1984\/PoolAI\/issues\/44#issuecomment-[^\s)\]。；,]*/gu,
  )].map((match) => match[0])

  if (planStatusLines.length !== 1
      || planStatusTokens.length !== 1
      || countAdr0006References(planSource) !== 2
      || planAdr0006Lines.length !== 2
      || !planAdr0006Lines.includes(adr0006PlanScopeBullet)
      || !planAdr0006Lines.includes(planStatusLines[0])) {
    reject('The complete system plan may contain ADR 0006 only in one canonical section 6.2 scope bullet and one normalized standalone status bullet.')
  }

  if (status === 'Proposed') {
    if (decider !== adr0006ProposedDecider) {
      reject(`Proposed ADR 0006 Decider must be exactly: ${adr0006ProposedDecider}`)
    }
    if (approval !== '**Pending explicit approval**') {
      reject('Proposed ADR 0006 Approval evidence must remain exactly **Pending explicit approval**.')
    }
    if (candidateHeadValues.length > 0
        || approvedCiValues.length > 0
        || candidateHeadLikeCount > 0
        || approvedCiLikeCount > 0) {
      reject('Proposed ADR 0006 must not prefill Approved candidate head or Approved CI metadata.')
    }
    if (planStatusLines[0] !== adr0006ProposedPlanStatus) {
      reject('The system-plan ADR 0006 Proposed status bullet must match the canonical line exactly.')
    }
    if (planIssueCommentOccurrences.length > 0) {
      reject('Proposed ADR 0006 must not bind an Issue #44 approval comment in system-plan section 6.2.')
    }
  } else {
    if (candidateHeadValues.length !== 1 || candidateHeadLikeCount !== 1) {
      reject('Accepted ADR 0006 must declare exactly one Approved candidate head metadata field.')
    }
    if (approvedCiValues.length !== 1 || approvedCiLikeCount !== 1) {
      reject('Accepted ADR 0006 must declare exactly one Approved CI metadata field.')
    }
    if (!/^`(?!0{40}`$)[0-9a-f]{40}`$/u.test(candidateHeadValues[0] ?? '')) {
      reject('Accepted ADR 0006 Approved candidate head must be one non-zero 40-character lowercase Git SHA.')
    }
    const approvedCiMatch = approvedCiValues[0]?.match(
      /^\[quality\]\((https:\/\/github\.com\/Lyon1984\/PoolAI\/actions\/runs\/[1-9][0-9]*)\), \[security\]\((https:\/\/github\.com\/Lyon1984\/PoolAI\/actions\/runs\/[1-9][0-9]*)\)$/u,
    )
    const qualityCiUrl = approvedCiMatch?.[1]
    const securityCiUrl = approvedCiMatch?.[2]
    if (!qualityCiUrl
        || !securityCiUrl
        || !adr0006CiUrlPattern.test(qualityCiUrl)
        || !adr0006CiUrlPattern.test(securityCiUrl)
        || !isAuditableHttpsReference(qualityCiUrl)
        || !isAuditableHttpsReference(securityCiUrl)) {
      reject('Accepted ADR 0006 Approved CI must contain exact quality and security Lyon1984/PoolAI Actions run URLs.')
    } else if (qualityCiUrl === securityCiUrl) {
      reject('Accepted ADR 0006 quality and security evidence must use two distinct Actions runs.')
    }
    if (decider !== adr0006AcceptedDecider) {
      reject(`Accepted ADR 0006 Decider must be exactly: ${adr0006AcceptedDecider}`)
    }
    const approvalUrl = parseExactMarkdownLink(approval, adr0006ApprovalLinkLabel)
    if (!approvalUrl
        || !adr0006ApprovalUrlPattern.test(approvalUrl)
        || !isAuditableHttpsReference(approvalUrl)) {
      reject(`Accepted ADR 0006 Approval evidence must be one exact [${adr0006ApprovalLinkLabel}](URL) link.`)
    }
    const expectedPlanStatus = approvalUrl ? adr0006AcceptedPlanStatus(approvalUrl) : null
    if (!expectedPlanStatus || planStatusLines[0] !== expectedPlanStatus) {
      reject('The system-plan ADR 0006 Accepted status bullet must be exact and use the canonical Markdown link label.')
    }
    if (planIssueCommentOccurrences.length !== 1) {
      reject('Accepted ADR 0006 must bind exactly one Issue #44 approval-comment URL in system-plan section 6.2.')
    } else if (approvalUrl && planIssueCommentOccurrences[0] !== approvalUrl) {
      reject('The system-plan ADR 0006 permanent approval evidence must bind the same Issue #44 comment URL as the ADR.')
    }
  }

  return governanceFailures
}

const validateAdr0006CurrentState = (
  source,
  status,
  approvalUrl = null,
  allowedReferenceDigests = adr0006AllowedCurrentStateReferenceDigests,
) => {
  const memoryFailures = []
  const lines = source.split(/\r?\n/u)
  const statusLines = lines.filter((line) => line.startsWith(adr0006MemoryStatusPrefix))
  const referenceLines = lines.filter((line) => hasAdr0006Reference(line))
  const nonStatusReferenceDigests = referenceLines
    .filter((line) => !line.startsWith(adr0006MemoryStatusPrefix))
    .map((line) => createHash('sha256').update(line).digest('hex'))
  const expectedStatusLine = status === 'Proposed'
    ? adr0006ProposedMemoryStatus
    : status === 'Accepted' && approvalUrl
      ? adr0006AcceptedMemoryStatus(approvalUrl)
      : null
  if (forbiddenAdr0006GovernanceHtmlPattern.test(source)
      || statusLines.length !== 1
      || !expectedStatusLine
      || statusLines[0] !== expectedStatusLine
      || countAdr0006References(source) !== 5
      || referenceLines.length !== 5
      || !sameValues(nonStatusReferenceDigests, allowedReferenceDigests)) {
    memoryFailures.push('Project memory must contain one canonical ADR 0006 status bullet plus only the four reviewed non-status reference lines, with no HTML markup or hidden cross-line reference.')
  }
  return memoryFailures
}

const withAdr0006Template = (metadata) => `${metadata}\n\n${adr0006ApprovalTemplateBlock}`
const withSystemPlanFixture = (sectionBody) => [
  '# System-plan fixture',
  adr0006PlanSectionHeading,
  sectionBody,
  '### 6.3 Fixture',
  'Fixture tail.',
].join('\n')
const withAdr0006MemoryFixtureStatus = (source, statusLine) => {
  const sourceLines = source.split('\n')
  const statusLineIndexes = sourceLines
    .map((line, index) => [line.endsWith('\r') ? line.slice(0, -1) : line, index])
    .filter(([line]) => line.startsWith(adr0006MemoryStatusPrefix))
    .map(([, index]) => index)
  if (statusLineIndexes.length !== 1) {
    return null
  }
  const statusLineIndex = statusLineIndexes[0]
  const carriageReturn = sourceLines[statusLineIndex].endsWith('\r') ? '\r' : ''
  sourceLines[statusLineIndex] = `${statusLine}${carriageReturn}`
  return sourceLines.join('\n')
}
const adr0006GovernanceFixtures = {
  acceptedUrl: 'https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-5012345678',
  candidateHead: '0123456789abcdef0123456789abcdef01234567',
  qualityCiUrl: 'https://github.com/Lyon1984/PoolAI/actions/runs/5012345678',
  securityCiUrl: 'https://github.com/Lyon1984/PoolAI/actions/runs/5012345679',
}
adr0006GovernanceFixtures.proposedAdr = withAdr0006Template([
  '- Status: **Proposed**',
  `- Decider: ${adr0006ProposedDecider}`,
  '- Approval evidence: **Pending explicit approval**',
].join('\n'))
adr0006GovernanceFixtures.proposedPlan = withSystemPlanFixture([
  adr0006PlanScopeBullet,
  adr0006ProposedPlanStatus,
].join('\n'))
adr0006GovernanceFixtures.acceptedAdr = withAdr0006Template([
  '- Status: **Accepted**',
  `- Decider: ${adr0006AcceptedDecider}`,
  `- Approval evidence: [${adr0006ApprovalLinkLabel}](${adr0006GovernanceFixtures.acceptedUrl})`,
  `- Approved candidate head: \`${adr0006GovernanceFixtures.candidateHead}\``,
  `- Approved CI: [quality](${adr0006GovernanceFixtures.qualityCiUrl}), [security](${adr0006GovernanceFixtures.securityCiUrl})`,
].join('\n'))
adr0006GovernanceFixtures.acceptedPlan =
  withSystemPlanFixture([
    adr0006PlanScopeBullet,
    adr0006AcceptedPlanStatus(adr0006GovernanceFixtures.acceptedUrl),
  ].join('\n'))
adr0006GovernanceFixtures.proposedMemory = withAdr0006MemoryFixtureStatus(
  currentState,
  adr0006ProposedMemoryStatus,
) ?? ''
adr0006GovernanceFixtures.acceptedMemory = withAdr0006MemoryFixtureStatus(
  currentState,
  adr0006AcceptedMemoryStatus(adr0006GovernanceFixtures.acceptedUrl),
) ?? ''

for (const fixtureSeed of [
  adr0006GovernanceFixtures.proposedMemory,
  adr0006GovernanceFixtures.acceptedMemory,
]) {
  if (withAdr0006MemoryFixtureStatus(fixtureSeed, adr0006ProposedMemoryStatus)
        !== adr0006GovernanceFixtures.proposedMemory
      || withAdr0006MemoryFixtureStatus(
        fixtureSeed,
        adr0006AcceptedMemoryStatus(adr0006GovernanceFixtures.acceptedUrl),
      ) !== adr0006GovernanceFixtures.acceptedMemory) {
    fail('ADR 0006 current-state self-test fixtures must be independent of the repository governance state.')
  }
}
for (const [label, fixtureSeed] of [
  [
    'a missing status slot',
    adr0006GovernanceFixtures.proposedMemory.replace(adr0006ProposedMemoryStatus, ''),
  ],
  [
    'a duplicate status slot',
    `${adr0006GovernanceFixtures.proposedMemory}\n${adr0006MemoryStatusPrefix}forged`,
  ],
]) {
  if (withAdr0006MemoryFixtureStatus(fixtureSeed, adr0006ProposedMemoryStatus) !== null) {
    fail(`ADR 0006 current-state fixture self-test accepted ${label}.`)
  }
}

const adr0006ReviewedMemoryFixtureLines = adr0006GovernanceFixtures.proposedMemory.split('\n')
const adr0006ReviewedMemoryReferenceIndexes = adr0006ReviewedMemoryFixtureLines
  .map((line, index) => [line.endsWith('\r') ? line.slice(0, -1) : line, index])
  .filter(([line]) => hasAdr0006Reference(line) && !line.startsWith(adr0006MemoryStatusPrefix))
  .map(([, index]) => index)
if (adr0006ReviewedMemoryReferenceIndexes.length !== 4) {
  fail('ADR 0006 current-state digest-drift self-test requires exactly four reviewed non-status reference lines.')
}
const adr0006HistoricalMemoryReferenceDigests =
  adr0006HistoricalBaseReferenceDigestsByMemoryBlob.values().next().value ?? new Set()
const adr0006MixedGenerationReferenceDigests = new Set([
  ...[...adr0006AllowedCurrentStateReferenceDigests].slice(0, 2),
  ...[...adr0006HistoricalMemoryReferenceDigests].slice(0, 2),
])
for (const [label, digests] of [
  ['historical base digests on the current memory', adr0006HistoricalMemoryReferenceDigests],
  ['mixed current and historical digests', adr0006MixedGenerationReferenceDigests],
]) {
  if (validateAdr0006CurrentState(
    adr0006GovernanceFixtures.proposedMemory,
    'Proposed',
    null,
    digests,
  ).length === 0) {
    fail(`ADR 0006 current-state digest self-test accepted ${label}.`)
  }
}
for (const referenceIndex of adr0006ReviewedMemoryReferenceIndexes) {
  const driftedLines = [...adr0006ReviewedMemoryFixtureLines]
  const carriageReturn = driftedLines[referenceIndex].endsWith('\r') ? '\r' : ''
  const reviewedLine = carriageReturn ? driftedLines[referenceIndex].slice(0, -1) : driftedLines[referenceIndex]
  driftedLines[referenceIndex] = `${reviewedLine} [digest-drift]${carriageReturn}`
  if (validateAdr0006CurrentState(driftedLines.join('\n'), 'Proposed').length === 0) {
    fail(`ADR 0006 current-state digest-drift self-test accepted reviewed reference ${referenceIndex}.`)
  }
}

for (const [label, adrSource, planSection] of [
  ['Proposed', adr0006GovernanceFixtures.proposedAdr, adr0006GovernanceFixtures.proposedPlan],
  ['Accepted', adr0006GovernanceFixtures.acceptedAdr, adr0006GovernanceFixtures.acceptedPlan],
]) {
  if (validateAdr0006Governance(adrSource, planSection).length > 0) {
    fail(`ADR 0006 governance self-test rejected a valid ${label} state.`)
  }
}
for (const [label, source, status, approvalUrl] of [
  ['Proposed', adr0006GovernanceFixtures.proposedMemory, 'Proposed', null],
  ['Accepted', adr0006GovernanceFixtures.acceptedMemory, 'Accepted', adr0006GovernanceFixtures.acceptedUrl],
]) {
  if (validateAdr0006CurrentState(source, status, approvalUrl).length > 0) {
    fail(`ADR 0006 current-state self-test rejected a valid ${label} state.`)
  }
}
for (const [label, source, status, approvalUrl] of [
  [
    'a duplicate canonical status bullet',
    `${adr0006GovernanceFixtures.proposedMemory}\n${adr0006ProposedMemoryStatus}`,
    'Proposed',
    null,
  ],
  [
    'an out-of-band Accepted claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR 0006 is Accepted.`,
    'Proposed',
    null,
  ],
  [
    'a Markdown-emphasized out-of-band approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR **0006** 已批准并生效。`,
    'Proposed',
    null,
  ],
  [
    'a Markdown-link-label out-of-band approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\n[ADR **0006**](../architecture/adr/0006.md) 已批准。`,
    'Proposed',
    null,
  ],
  [
    'an HTML-emphasized out-of-band approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR <strong>0006</strong> 已批准。`,
    'Proposed',
    null,
  ],
  [
    'an incomplete HTML-tag approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR <strong 0006 已批准。`,
    'Proposed',
    null,
  ],
  [
    'a processing-instruction HTML approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR <?hidden 0006 已批准。`,
    'Proposed',
    null,
  ],
  [
    'a CDATA HTML approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR <![CDATA[0006 已批准。`,
    'Proposed',
    null,
  ],
  [
    'an unseparated out-of-band approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR0006 已批准并生效。`,
    'Proposed',
    null,
  ],
  [
    'a hyphenated out-of-band Accepted claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR-0006 is Accepted.`,
    'Proposed',
    null,
  ],
  [
    'a zero-width numeric-entity approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR 00&#x200b;06 已批准。`,
    'Proposed',
    null,
  ],
  [
    'a named-whitespace-entity approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR&Tab;0006 已批准。`,
    'Proposed',
    null,
  ],
  [
    'a cross-line HTML-comment approval claim',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR<!--\n-->0006 已批准。`,
    'Proposed',
    null,
  ],
  [
    'an unregistered Chinese approval synonym',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR 0006 已正式批准。`,
    'Proposed',
    null,
  ],
  [
    'an unregistered English approval synonym',
    `${adr0006GovernanceFixtures.proposedMemory}\nADR 0006 received approval.`,
    'Proposed',
    null,
  ],
  [
    'an Accepted bullet with a different comment URL',
    adr0006GovernanceFixtures.acceptedMemory.replace('5012345678', '5012345679'),
    'Accepted',
    adr0006GovernanceFixtures.acceptedUrl,
  ],
]) {
  if (validateAdr0006CurrentState(source, status, approvalUrl).length === 0) {
    fail(`ADR 0006 current-state self-test accepted ${label}.`)
  }
}

const invalidAdr0006GovernanceFixtures = [
  [
    'a duplicate Proposed Decider',
    adr0006GovernanceFixtures.proposedAdr.replace(
      '- Approval evidence:',
      `- Decider: ${adr0006ProposedDecider}\n- Approval evidence:`,
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'an approximate Proposed Decider',
    adr0006GovernanceFixtures.proposedAdr.replace(
      adr0006ProposedDecider,
      'PoolAI architecture owner (`@Lyon1984`) — approval pending',
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'an approximate Accepted Decider',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      adr0006AcceptedDecider,
      `${adr0006AcceptedDecider} — approved`,
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'a Proposed ADR with non-pending evidence',
    adr0006GovernanceFixtures.proposedAdr.replace(
      '**Pending explicit approval**',
      `[${adr0006ApprovalLinkLabel}](${adr0006GovernanceFixtures.acceptedUrl})`,
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'a Proposed ADR with prefilled candidate evidence',
    adr0006GovernanceFixtures.proposedAdr.replace(
      '- Approval evidence: **Pending explicit approval**',
      `- Approval evidence: **Pending explicit approval**\n- Approved candidate head: \`${adr0006GovernanceFixtures.candidateHead}\``,
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'a Proposed ADR with malformed prefilled CI evidence',
    adr0006GovernanceFixtures.proposedAdr.replace(
      '- Approval evidence: **Pending explicit approval**',
      '- Approval evidence: **Pending explicit approval**\n- Approved CI : forged',
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'a Proposed ADR with lowercase prefilled candidate evidence',
    adr0006GovernanceFixtures.proposedAdr.replace(
      '- Approval evidence: **Pending explicit approval**',
      `- Approval evidence: **Pending explicit approval**\n- approved candidate head: \`${adr0006GovernanceFixtures.candidateHead}\``,
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
  [
    'an Accepted ADR missing the candidate head',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      `- Approved candidate head: \`${adr0006GovernanceFixtures.candidateHead}\`\n`,
      '',
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with a spoofed approval-link label',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      `[${adr0006ApprovalLinkLabel}]`,
      '[APPROVED by Lyon1984]',
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with an approval URL suffix',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      adr0006GovernanceFixtures.acceptedUrl,
      `${adr0006GovernanceFixtures.acceptedUrl}/forged`,
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with a non-lowercase candidate head',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      adr0006GovernanceFixtures.candidateHead,
      adr0006GovernanceFixtures.candidateHead.toUpperCase(),
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with CI evidence from another repository',
    adr0006GovernanceFixtures.acceptedAdr.replaceAll(
      'https://github.com/Lyon1984/PoolAI/actions/runs/',
      'https://github.com/dotnet/runtime/actions/runs/',
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with a CI URL suffix',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      adr0006GovernanceFixtures.qualityCiUrl,
      `${adr0006GovernanceFixtures.qualityCiUrl}/attempts/1`,
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR with a lowercase duplicate CI field',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      '- Approved candidate head:',
      '- approved ci: forged\n- Approved candidate head:',
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted ADR that reuses one CI run',
    adr0006GovernanceFixtures.acceptedAdr.replace(
      adr0006GovernanceFixtures.securityCiUrl,
      adr0006GovernanceFixtures.qualityCiUrl,
    ),
    adr0006GovernanceFixtures.acceptedPlan,
  ],
  [
    'an Accepted plan with a spoofed approval-link label',
    adr0006GovernanceFixtures.acceptedAdr,
    adr0006GovernanceFixtures.acceptedPlan.replace(
      `[${adr0006PlanApprovalLinkLabel}]`,
      '[APPROVED]',
    ),
  ],
  [
    'an Accepted plan with a URL suffix after its Markdown link',
    adr0006GovernanceFixtures.acceptedAdr,
    adr0006GovernanceFixtures.acceptedPlan.replace(
      adr0006AcceptedPlanStatus(adr0006GovernanceFixtures.acceptedUrl),
      `${adr0006AcceptedPlanStatus(adr0006GovernanceFixtures.acceptedUrl)} forged`,
    ),
  ],
  [
    'an Accepted plan with a mismatched link target',
    adr0006GovernanceFixtures.acceptedAdr,
    adr0006GovernanceFixtures.acceptedPlan.replace('5012345678', '5012345679'),
  ],
  [
    'an Accepted plan with an extra status sentence',
    adr0006GovernanceFixtures.acceptedAdr,
    `${adr0006GovernanceFixtures.acceptedPlan}\nADR 0006 is also Accepted.`,
  ],
  [
    'an Accepted plan with a lowercase extra status sentence',
    adr0006GovernanceFixtures.acceptedAdr,
    `${adr0006GovernanceFixtures.acceptedPlan}\nADR 0006 remains accepted.`,
  ],
  [
    'an Accepted plan with a Chinese extra approval sentence',
    adr0006GovernanceFixtures.acceptedAdr,
    `${adr0006GovernanceFixtures.acceptedPlan}\nADR 0006 已经批准。`,
  ],
  [
    'a Proposed plan with a Chinese approved-and-effective bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR 0006 已批准并生效。`,
  ],
  [
    'a Proposed plan with a Markdown-emphasized approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR **0006** 已批准并生效。`,
  ],
  [
    'a Proposed plan with a Markdown-link-label approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\n[ADR **0006**](architecture/adr/0006.md) 已批准。`,
  ],
  [
    'a Proposed plan with an HTML-emphasized approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR <strong>0006</strong> 已批准。`,
  ],
  [
    'a Proposed plan with an incomplete HTML-tag approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR <strong 0006 已批准。`,
  ],
  [
    'a Proposed plan with an unseparated approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR0006 已批准并生效。`,
  ],
  [
    'a Proposed plan with a hyphenated Accepted bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR-0006 is Accepted.`,
  ],
  [
    'a Proposed plan with a zero-width numeric-entity approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR 00&#x200b;06 已批准。`,
  ],
  [
    'a Proposed plan with a named-whitespace-entity approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR&Tab;0006 已批准。`,
  ],
  [
    'a Proposed plan with a cross-line HTML-comment approval bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR<!--\n-->0006 已批准。`,
  ],
  [
    'a Proposed plan with a Chinese signed-and-permanent bypass sentence',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR 0006 已签署并永久生效。`,
  ],
  [
    'an Accepted plan with a duplicate status bullet',
    adr0006GovernanceFixtures.acceptedAdr,
    `${adr0006GovernanceFixtures.acceptedPlan}\n${adr0006GovernanceFixtures.acceptedPlan}`,
  ],
  [
    'a Proposed plan carrying an approval URL',
    adr0006GovernanceFixtures.proposedAdr,
    `${adr0006GovernanceFixtures.proposedPlan}\nADR 0006 approval: ${adr0006GovernanceFixtures.acceptedUrl}`,
  ],
  [
    'an ADR with a modified canonical APPROVED template',
    adr0006GovernanceFixtures.proposedAdr.replace(
      'APPROVED: ADR 0006',
      'APPROVED ADR 0006',
    ),
    adr0006GovernanceFixtures.proposedPlan,
  ],
]
for (const [label, adrSource, planSection] of invalidAdr0006GovernanceFixtures) {
  if (validateAdr0006Governance(adrSource, planSection).length === 0) {
    fail(`ADR 0006 governance self-test accepted ${label}.`)
  }
}

const validSectionFixture = adr0006GovernanceFixtures.proposedPlan
if (extractSystemPlanSection62(validSectionFixture).failures.length > 0) {
  fail('ADR 0006 governance self-test rejected one valid system-plan section 6.2 heading.')
}
if (extractSystemPlanSection62(`${validSectionFixture}\n### 6.2 Duplicate`).failures.length === 0) {
  fail('ADR 0006 governance self-test accepted duplicate system-plan section 6.2 headings.')
}

const systemPlanSection62 = extractSystemPlanSection62(systemPlan)
for (const sectionFailure of systemPlanSection62.failures) {
  fail(sectionFailure)
}
const dataPlaneSection = systemPlanSection62.section
const expectedCrossContextFamilies = ['A', 'B', 'C']
const crossContextFamilyMarkers = new Map([
  ['Family A canonical admission and route identity', [
    'Family A',
    'poolai_quota_reserve',
    'route/provider identity',
  ]],
  ['Family B activation guard', [
    'Family B',
    'poolai_validate_group_activation',
  ]],
  ['Family C Group–Subscription lifecycle fence', [
    'Family C',
    'Group–Subscription lifecycle fence',
  ]],
])

if (!dataPlaneSection) {
  fail('The system plan must retain section 6.2 for the model-request process manager.')
} else {
  const registeredFamilies = [...dataPlaneSection.matchAll(/\bFamily\s+([A-Z])\b/gu)]
    .map((match) => match[1])
  if (!sameValues(new Set(registeredFamilies), expectedCrossContextFamilies)) {
    fail('The system-plan data-plane rules must register exactly Family A, Family B, and Family C.')
  }
  if (!dataPlaneSection.includes('只含三个 family')) {
    fail('The system-plan data-plane rules must state that the ADR 0006 registry contains only three families.')
  }
  if (/是仅有的跨\s*Context\s*数据库窄例外/u.test(dataPlaneSection)) {
    fail('The system-plan data-plane rules retain the stale two-exception summary.')
  }
  if (!dataPlaneSection.includes('ADR 0006')) {
    fail('The system-plan data-plane rules must link the cross-context database registry to ADR 0006.')
  }
  for (const [family, markers] of crossContextFamilyMarkers) {
    const missingMarkers = markers.filter((marker) => !dataPlaneSection.includes(marker))
    if (missingMarkers.length > 0) {
      fail(`The system-plan data-plane rules must summarize ${family}; missing: ${missingMarkers.join(', ')}.`)
    }
  }

}

for (const governanceFailure of validateAdr0006Governance(adr0006, systemPlan)) {
  fail(governanceFailure)
}

const expectedQualityGateTrigger = [
  'on:',
  '  pull_request:',
  '  push:',
  '    branches:',
  '      - main',
  '',
  'permissions:',
].join('\n')
const expectedQualityGatePermissions = new Map([
  ['actions', 'read'],
  ['contents', 'read'],
  ['issues', 'read'],
])
const expectedQualityGateWorkflowSha256 =
  'b904848e7218befde8a08a517a76c05151b87d3f475bdd8df5c68de709604988'
const expectedNodeSetupStep = [
  '      - name: Set up Node.js',
  '        uses: actions/setup-node@48b55a011bda9f5d6aeb4c2d9c7362e8dae4041e # v6.4.0',
  '        with:',
  '          node-version-file: .node-version',
  '          package-manager-cache: false',
].join('\n')
const expectedGithubEvidenceStep = [
  '      - name: Verify ADR 0006 GitHub approval evidence',
  '        env:',
  '          GITHUB_TOKEN: ${{ github.token }}',
  '          ADR0006_EVENT_NAME: ${{ github.event_name }}',
  "          ADR0006_BASE_HEAD: ${{ github.event_name == 'pull_request' && github.event.pull_request.base.sha || github.event.before }}",
  '          ADR0006_CHECKOUT_HEAD: ${{ github.sha }}',
  "          ADR0006_SIGNING_HEAD: ${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}",
  '        run: node eng/policies/validate-traceability.mjs --github-evidence',
].join('\n')
const expectedGithubEvidenceStepLines = expectedGithubEvidenceStep.split('\n')

const validateQualityGateEvidenceWorkflow = (source) => {
  const workflowFailures = []
  const reject = (message) => workflowFailures.push(message)
  const normalizedSource = source.replaceAll('\r\n', '\n')
  const lines = normalizedSource.split('\n')
  const workflowDigest = createHash('sha256').update(normalizedSource).digest('hex')
  if (workflowDigest !== expectedQualityGateWorkflowSha256) {
    reject('quality-gate.yml must match the reviewed fail-closed workflow digest; any workflow change requires governance review and digest rotation.')
  }
  if (normalizedSource.split(expectedQualityGateTrigger).length - 1 !== 1
      || lines.filter((line) => line === 'on:').length !== 1) {
    reject('quality-gate.yml must run only for pull_request and push to main so only main pushes skip signing ancestry checks.')
  }

  const rootPermissionsIndex = lines.findIndex((line) => line === 'permissions:')
  const rootPermissions = new Map()
  let rootPermissionEntryCount = 0
  if (rootPermissionsIndex >= 0) {
    for (let index = rootPermissionsIndex + 1; index < lines.length; index += 1) {
      const match = lines[index].match(/^  ([a-z-]+): (read|write|none)$/u)
      if (!match) {
        break
      }
      rootPermissionEntryCount += 1
      rootPermissions.set(match[1], match[2])
    }
  }
  if (rootPermissionsIndex < 0
      || lines.filter((line) => line === 'permissions:').length !== 1
      || rootPermissionEntryCount !== expectedQualityGatePermissions.size
      || !sameValues(rootPermissions.keys(), expectedQualityGatePermissions.keys())
      || [...expectedQualityGatePermissions]
        .some(([name, value]) => rootPermissions.get(name) !== value)) {
    reject('quality-gate.yml must grant exactly top-level contents:read, actions:read, and issues:read.')
  }
  if (source.includes('pull_request_target')) {
    reject('quality-gate.yml must not use pull_request_target for ADR 0006 evidence verification.')
  }
  if (/\$\{\{\s*secrets\./u.test(source) || /\bPAT\b/iu.test(source)) {
    reject('quality-gate.yml must use github.token rather than a PAT or repository secret.')
  }
  if (lines.some((line) => line === 'defaults:' || line === '    defaults:')) {
    reject('quality-gate.yml must not define workflow-level or job-level defaults.')
  }

  const verifyStarts = lines
    .map((line, index) => [line, index])
    .filter(([line]) => line === '  verify:')
    .map(([, index]) => index)
  if (verifyStarts.length !== 1) {
    reject('quality-gate.yml must contain exactly one verify job.')
    return workflowFailures
  }
  const verifyStart = verifyStarts[0]
  const nextJobOffset = lines.slice(verifyStart + 1)
    .findIndex((line) => /^  [A-Za-z0-9_-]+:$/u.test(line))
  const verifyEnd = nextJobOffset < 0 ? lines.length : verifyStart + 1 + nextJobOffset
  const verifyLines = lines.slice(verifyStart, verifyEnd)
  if (verifyLines.some((line) => /^    (?:if|continue-on-error):/u.test(line))) {
    reject('quality-gate.yml verify job must not use job-level if or continue-on-error.')
  }

  const evidenceStarts = lines
    .map((line, index) => [line, index])
    .filter(([line]) => line === expectedGithubEvidenceStepLines[0])
    .map(([, index]) => index)
  if (evidenceStarts.length !== 1) {
    reject('quality-gate.yml must contain exactly one ADR 0006 GitHub-evidence step.')
    return workflowFailures
  }
  const evidenceStart = evidenceStarts[0]
  if (evidenceStart <= verifyStart || evidenceStart >= verifyEnd) {
    reject('The ADR 0006 GitHub-evidence step must belong to the verify job.')
  }
  for (const [offset, expectedLine] of expectedGithubEvidenceStepLines.entries()) {
    if (lines[evidenceStart + offset] !== expectedLine) {
      reject('The ADR 0006 GitHub-evidence step must match its complete canonical YAML block exactly.')
      break
    }
  }
  let nextNonEmpty = evidenceStart + expectedGithubEvidenceStepLines.length
  while (nextNonEmpty < lines.length && lines[nextNonEmpty] === '') {
    nextNonEmpty += 1
  }
  if (lines[nextNonEmpty] !== '      - name: Activate pnpm') {
    reject('Activate pnpm must be the next non-empty line after the complete ADR 0006 evidence step.')
  }
  const expectedEvidencePrefix = `${expectedNodeSetupStep}\n\n${expectedGithubEvidenceStep}`
  if (normalizedSource.split(expectedEvidencePrefix).length - 1 !== 1) {
    reject('The ADR 0006 evidence step must be the immediate next step after the complete pinned Node setup step.')
  }
  const firstRunIndex = lines.findIndex((line) => /^\s+run:/u.test(line))
  const evidenceRunIndex = evidenceStart + expectedGithubEvidenceStepLines.length - 1
  if (firstRunIndex !== evidenceRunIndex) {
    reject('The ADR 0006 evidence check must be the first workflow run step so no candidate command can mutate governance files before verification.')
  }
  return workflowFailures
}

for (const workflowFailure of validateQualityGateEvidenceWorkflow(qualityGateWorkflow)) {
  fail(workflowFailure)
}
const evidenceRunLine = '        run: node eng/policies/validate-traceability.mjs --github-evidence'
for (const [label, suffix] of [
  ['continue-on-error', '        continue-on-error: true'],
  ['an if condition', '        if: false'],
  ['a custom shell', '        shell: bash {0}'],
  ['a duplicate run key', '        run: true'],
]) {
  const mutatedWorkflow = qualityGateWorkflow.replace(
    evidenceRunLine,
    `${evidenceRunLine}\n${suffix}`,
  )
  if (validateQualityGateEvidenceWorkflow(mutatedWorkflow).length === 0) {
    fail(`ADR 0006 workflow self-test accepted ${label} on the evidence step.`)
  }
}
for (const [label, mutation] of [
  ['a verify-job if', '  verify:\n    if: false'],
  ['a spaced verify-job if', '  verify:\n    if : false'],
  ['a quoted verify-job if', '  verify:\n    "if": false'],
  ['verify-job continue-on-error', '  verify:\n    continue-on-error: true'],
  ['workflow defaults', 'defaults:\n  run:\n    shell: bash {0}\n\njobs:'],
  ['inline workflow defaults', 'defaults: {run: {shell: "bash {0} || true"}}\n\njobs:'],
]) {
  const mutatedWorkflow = label.includes('workflow defaults')
    ? qualityGateWorkflow.replace('jobs:', mutation)
    : qualityGateWorkflow.replace('  verify:', mutation)
  if (validateQualityGateEvidenceWorkflow(mutatedWorkflow).length === 0) {
    fail(`ADR 0006 workflow self-test accepted ${label}.`)
  }
}
const insertedPreEvidenceStep = qualityGateWorkflow.replace(
  expectedGithubEvidenceStep,
  [
    '      - name: Mutate governance state',
    '        run: node malicious.mjs',
    '',
    expectedGithubEvidenceStep,
  ].join('\n'),
)
if (validateQualityGateEvidenceWorkflow(insertedPreEvidenceStep).length === 0) {
  fail('ADR 0006 workflow self-test accepted a candidate run step before evidence verification.')
}

if (!structureOnly && !compiledTests && !githubEvidence) {
  fail('Choose exactly one traceability mode: --structure-only, --compiled-tests, or --github-evidence.')
}

const parseAdr0006Evidence = (source) => {
  const status = adrMetadataValues(source, 'Status')[0]?.match(/^\*\*(Proposed|Accepted)\*\*$/u)?.[1]
  const approvalUrl = parseExactMarkdownLink(
    adrMetadataValues(source, 'Approval evidence')[0] ?? '',
    adr0006ApprovalLinkLabel,
  )
  const candidateHead = adrMetadataValues(source, 'Approved candidate head')[0]
    ?.match(/^`([0-9a-f]{40})`$/u)?.[1] ?? null
  const approvedCi = adrMetadataValues(source, 'Approved CI')[0]?.match(
    /^\[quality\]\((https:\/\/github\.com\/Lyon1984\/PoolAI\/actions\/runs\/[1-9][0-9]*)\), \[security\]\((https:\/\/github\.com\/Lyon1984\/PoolAI\/actions\/runs\/[1-9][0-9]*)\)$/u,
  )
  return {
    status,
    approvalUrl,
    candidateHead,
    qualityCiUrl: approvedCi?.[1] ?? null,
    securityCiUrl: approvedCi?.[2] ?? null,
  }
}

const currentAdr0006Evidence = parseAdr0006Evidence(adr0006)
for (const memoryFailure of validateAdr0006CurrentState(
  currentState,
  currentAdr0006Evidence.status,
  currentAdr0006Evidence.approvalUrl,
)) {
  fail(memoryFailure)
}

const buildAdr0006ApprovalBody = (evidence) => adr0006ApprovalTemplate
  .replace('<APPROVED_CANDIDATE_SHA>', evidence.candidateHead)
  .replace('<APPROVED_QUALITY_RUN_URL>', evidence.qualityCiUrl)
  .replace('<APPROVED_SECURITY_RUN_URL>', evidence.securityCiUrl)

const validateAdr0006CommentPayload = (payload, evidence) => {
  const payloadFailures = []
  const commentId = evidence.approvalUrl?.match(/#issuecomment-([1-9][0-9]*)$/u)?.[1]
  const expectedIssueUrl = 'https://api.github.com/repos/Lyon1984/PoolAI/issues/44'
  const expectedCommentApiUrl = commentId
    ? `https://api.github.com/repos/Lyon1984/PoolAI/issues/comments/${commentId}`
    : null
  if (payload?.issue_url !== expectedIssueUrl) {
    payloadFailures.push('ADR 0006 approval comment issue_url must identify Lyon1984/PoolAI Issue #44 exactly.')
  }
  if (payload?.html_url !== evidence.approvalUrl) {
    payloadFailures.push('ADR 0006 approval comment html_url must equal the ADR metadata URL exactly.')
  }
  if (payload?.url !== expectedCommentApiUrl) {
    payloadFailures.push('ADR 0006 approval comment API url must identify the declared comment exactly.')
  }
  if (payload?.user?.login !== 'Lyon1984') {
    payloadFailures.push('ADR 0006 approval comment must be authored by GitHub user Lyon1984.')
  }
  if (payload?.body !== buildAdr0006ApprovalBody(evidence)) {
    payloadFailures.push('ADR 0006 approval comment body must equal the canonical APPROVED template exactly.')
  }
  return payloadFailures
}

// ADR 0006's original signing-head rule still governs the historical
// Proposed-to-Accepted transition. This independently approved comment records
// how later event/checkout heads preserve that transition without freezing
// unrelated development files forever.
const validateAdr0006SigningLifecycleCommentPayload = (payload) => {
  const payloadFailures = []
  const expectedIssueUrl = 'https://api.github.com/repos/Lyon1984/PoolAI/issues/44'
  const expectedCommentApiUrl =
    'https://api.github.com/repos/Lyon1984/PoolAI/issues/comments/5046436932'
  if (payload?.issue_url !== expectedIssueUrl) {
    payloadFailures.push('ADR 0006 signing-lifecycle comment must belong to Issue #44 exactly.')
  }
  if (payload?.html_url !== adr0006SigningLifecycleApprovalUrl
      || payload?.url !== expectedCommentApiUrl) {
    payloadFailures.push('ADR 0006 signing-lifecycle comment must match its permanent URL exactly.')
  }
  if (payload?.user?.login !== 'Lyon1984') {
    payloadFailures.push('ADR 0006 signing-lifecycle comment must be authored by GitHub user Lyon1984.')
  }
  if (payload?.body !== adr0006SigningLifecycleApprovalBody) {
    payloadFailures.push('ADR 0006 signing-lifecycle comment body must equal the approved text exactly.')
  }
  return payloadFailures
}

const validateAdr0006WorkflowPayload = (payload, expectedName, expectedPath) => {
  const payloadFailures = []
  const workflowId = payload?.id
  if (!Number.isSafeInteger(workflowId) || workflowId <= 0) {
    payloadFailures.push(`ADR 0006 ${expectedName} workflow must expose one positive integer id.`)
  }
  if (payload?.name !== expectedName || payload?.path !== expectedPath) {
    payloadFailures.push(`ADR 0006 ${expectedName} workflow must have its exact name and repository path.`)
  }
  if (payload?.state !== 'active') {
    payloadFailures.push(`ADR 0006 ${expectedName} workflow must be active.`)
  }
  if (payload?.url !== `https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/${workflowId}`
      || payload?.html_url !== `https://github.com/Lyon1984/PoolAI/blob/main/${expectedPath}`) {
    payloadFailures.push(`ADR 0006 ${expectedName} workflow API and HTML URLs must identify the exact workflow.`)
  }
  return payloadFailures
}

const validateAdr0006RunPayload = (
  payload,
  workflow,
  expectedName,
  expectedPath,
  expectedUrl,
  candidateHead,
) => {
  const payloadFailures = []
  const runId = expectedUrl?.match(/\/actions\/runs\/([1-9][0-9]*)$/u)?.[1]
  const expectedApiUrl = runId
    ? `https://api.github.com/repos/Lyon1984/PoolAI/actions/runs/${runId}`
    : null
  if (payload?.name !== expectedName) {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must have workflow run name ${expectedName}.`)
  }
  if (payload?.status !== 'completed' || payload?.conclusion !== 'success') {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must be completed successfully.`)
  }
  if (payload?.head_sha !== candidateHead) {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must run on the approved candidate head.`)
  }
  if (payload?.html_url !== expectedUrl || payload?.url !== expectedApiUrl) {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence URL must match its declared Actions run exactly.`)
  }
  if (payload?.repository?.full_name !== 'Lyon1984/PoolAI') {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must belong to repository Lyon1984/PoolAI.`)
  }
  if (payload?.head_repository?.full_name !== 'Lyon1984/PoolAI') {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence head repository must be Lyon1984/PoolAI.`)
  }
  if (payload?.event !== 'pull_request') {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must come from a pull_request event.`)
  }
  if (payload?.path !== expectedPath) {
    payloadFailures.push(`ADR 0006 ${expectedName} evidence must run the exact ${expectedPath} workflow path.`)
  }
  if (payload?.workflow_id !== workflow?.id || payload?.workflow_url !== workflow?.url) {
    payloadFailures.push(`ADR 0006 ${expectedName} run must bind the exact active workflow id and URL.`)
  }
  return payloadFailures
}

const acceptedEvidenceFixture = parseAdr0006Evidence(adr0006GovernanceFixtures.acceptedAdr)
const acceptedCommentId = acceptedEvidenceFixture.approvalUrl.match(/#issuecomment-([1-9][0-9]*)$/u)[1]
const validCommentPayloadFixture = {
  url: `https://api.github.com/repos/Lyon1984/PoolAI/issues/comments/${acceptedCommentId}`,
  issue_url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/44',
  html_url: acceptedEvidenceFixture.approvalUrl,
  user: { login: 'Lyon1984' },
  body: buildAdr0006ApprovalBody(acceptedEvidenceFixture),
}
const validSigningLifecycleCommentPayloadFixture = {
  url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/comments/5046436932',
  issue_url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/44',
  html_url: adr0006SigningLifecycleApprovalUrl,
  user: { login: 'Lyon1984' },
  body: adr0006SigningLifecycleApprovalBody,
}
const validQualityRunPayloadFixture = {
  url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/runs/5012345678',
  html_url: acceptedEvidenceFixture.qualityCiUrl,
  name: 'quality-gate',
  status: 'completed',
  conclusion: 'success',
  head_sha: acceptedEvidenceFixture.candidateHead,
  repository: { full_name: 'Lyon1984/PoolAI' },
  head_repository: { full_name: 'Lyon1984/PoolAI' },
  event: 'pull_request',
  path: '.github/workflows/quality-gate.yml',
  workflow_id: 101,
  workflow_url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/101',
}
const validQualityWorkflowPayloadFixture = {
  id: 101,
  name: 'quality-gate',
  path: '.github/workflows/quality-gate.yml',
  state: 'active',
  url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/101',
  html_url: 'https://github.com/Lyon1984/PoolAI/blob/main/.github/workflows/quality-gate.yml',
}
if (validateAdr0006CommentPayload(validCommentPayloadFixture, acceptedEvidenceFixture).length > 0) {
  fail('ADR 0006 GitHub-evidence self-test rejected a valid Issue #44 approval comment.')
}
if (validateAdr0006SigningLifecycleCommentPayload(
  validSigningLifecycleCommentPayloadFixture,
).length > 0) {
  fail('ADR 0006 GitHub-evidence self-test rejected the valid signing-lifecycle comment.')
}
if (validateAdr0006WorkflowPayload(
  validQualityWorkflowPayloadFixture,
  'quality-gate',
  '.github/workflows/quality-gate.yml',
).length > 0) {
  fail('ADR 0006 GitHub-evidence self-test rejected the valid quality-gate workflow identity.')
}
if (validateAdr0006RunPayload(
  validQualityRunPayloadFixture,
  validQualityWorkflowPayloadFixture,
  'quality-gate',
  '.github/workflows/quality-gate.yml',
  acceptedEvidenceFixture.qualityCiUrl,
  acceptedEvidenceFixture.candidateHead,
).length > 0) {
  fail('ADR 0006 GitHub-evidence self-test rejected a valid quality-gate run.')
}
for (const [label, payload] of [
  ['the wrong Issue', { ...validCommentPayloadFixture, issue_url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/45' }],
  ['a different comment API URL', { ...validCommentPayloadFixture, url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/comments/5012345679' }],
  ['a spoofed comment URL', { ...validCommentPayloadFixture, html_url: `${acceptedEvidenceFixture.approvalUrl}/forged` }],
  ['a different author', { ...validCommentPayloadFixture, user: { login: 'lyon1984' } }],
  ['a body suffix', { ...validCommentPayloadFixture, body: `${validCommentPayloadFixture.body}\nAPPROVED` }],
]) {
  if (validateAdr0006CommentPayload(payload, acceptedEvidenceFixture).length === 0) {
    fail(`ADR 0006 GitHub-evidence self-test accepted ${label}.`)
  }
}
for (const [label, payload] of [
  ['the wrong Issue', { ...validSigningLifecycleCommentPayloadFixture, issue_url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/45' }],
  ['a different URL', { ...validSigningLifecycleCommentPayloadFixture, html_url: `${adr0006SigningLifecycleApprovalUrl}/forged` }],
  ['a different author', { ...validSigningLifecycleCommentPayloadFixture, user: { login: 'lyon1984' } }],
  ['a body suffix', { ...validSigningLifecycleCommentPayloadFixture, body: `${adr0006SigningLifecycleApprovalBody}APPROVED` }],
]) {
  if (validateAdr0006SigningLifecycleCommentPayload(payload).length === 0) {
    fail(`ADR 0006 signing-lifecycle self-test accepted ${label}.`)
  }
}

const runReadOnlyGit = (argumentsList, encoding = 'utf8') => spawnSync(
  'git',
  argumentsList,
  {
    cwd: root,
    encoding,
    maxBuffer: 16 * 1024 * 1024,
  },
)

const readGitBlob = (commit, path) => {
  const result = runReadOnlyGit(['show', `${commit}:${path}`])
  if (result.status !== 0) {
    return {
      source: null,
      error: (result.stderr || result.stdout || 'no output').trim(),
    }
  }
  return { source: result.stdout, error: null }
}

const readGitTreeEntry = (commit, path) => {
  const result = runReadOnlyGit(['ls-tree', '-z', commit, '--', path], null)
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout]
      .find((buffer) => buffer.length > 0)
      ?? Buffer.from('no output')
    return {
      entry: null,
      error: diagnostic.toString('utf8').trim(),
      missing: false,
    }
  }
  const source = result.stdout.toString('utf8')
  if (source.length === 0) {
    return { entry: null, error: null, missing: true }
  }
  const match = source.match(/^([0-7]{6}) (blob|tree|commit) ([0-9a-f]{40})\t([^\0]+)\0$/u)
  if (!match || match[4] !== path) {
    return {
      entry: null,
      error: 'the path does not resolve to exactly one canonical Git tree entry',
      missing: false,
    }
  }
  return {
    entry: {
      mode: match[1],
      type: match[2],
      object: match[3],
    },
    error: null,
    missing: false,
  }
}

const readAdr0006GovernanceSnapshot = (commit) => ({
  adr: readGitBlob(commit, adr0006Path),
  plan: readGitBlob(commit, systemPlanPath),
  memory: readGitBlob(commit, currentStatePath),
  entries: new Map(adr0006ProtectedSigningFiles.map((path) => [
    path,
    readGitTreeEntry(commit, path),
  ])),
})

const normalizeAdr0006GovernanceMetadata = (source) => {
  const ignoredLabels = [
    'Status',
    'Decider',
    'Approval evidence',
    'Approved candidate head',
    'Approved CI',
  ]
  return source.split('\n')
    .filter((line) => !ignoredLabels.some((label) => line.startsWith(`- ${label}:`)))
    .join('\n')
}

const normalizeAdr0006PlanStatus = (source, expectedStatusLine) => {
  const sectionResult = extractSystemPlanSection62(source)
  if (sectionResult.failures.length > 0 || !sectionResult.section) {
    return null
  }
  const sectionMatches = sectionResult.section.split(/\r?\n/u)
    .filter((line) => line === expectedStatusLine)
  const sourceLines = source.split('\n')
  const sourceMatchIndexes = sourceLines
    .map((line, index) => [line.endsWith('\r') ? line.slice(0, -1) : line, index])
    .filter(([line]) => line === expectedStatusLine)
    .map(([, index]) => index)
  if (sectionMatches.length !== 1 || sourceMatchIndexes.length !== 1) {
    return null
  }
  const matchIndex = sourceMatchIndexes[0]
  const carriageReturn = sourceLines[matchIndex].endsWith('\r') ? '\r' : ''
  sourceLines[matchIndex] = `- ADR 0006 治理状态：<SIGNING_STATE>${carriageReturn}`
  return sourceLines.join('\n')
}

const normalizeAdr0006MemoryStatus = (source, expectedStatusLine) => {
  const sourceLines = source.split('\n')
  const sourceMatchIndexes = sourceLines
    .map((line, index) => [line.endsWith('\r') ? line.slice(0, -1) : line, index])
    .filter(([line]) => line === expectedStatusLine)
    .map(([, index]) => index)
  if (sourceMatchIndexes.length !== 1) {
    return null
  }
  const matchIndex = sourceMatchIndexes[0]
  const carriageReturn = sourceLines[matchIndex].endsWith('\r') ? '\r' : ''
  sourceLines[matchIndex] = `- ADR 0006 治理状态：<SIGNING_STATE>${carriageReturn}`
  return sourceLines.join('\n')
}

const normalizationPrefixFixture = '- ADR 0006 治理状态：forged outside section 6.2'
const normalizedPrefixFixture = normalizeAdr0006PlanStatus(
  adr0006GovernanceFixtures.proposedPlan.replace(
    'Fixture tail.',
    `${normalizationPrefixFixture}\nFixture tail.`,
  ),
  adr0006ProposedPlanStatus,
)
if (!normalizedPrefixFixture?.includes(normalizationPrefixFixture)) {
  fail('ADR 0006 plan-normalization self-test removed a non-canonical prefix line outside section 6.2.')
}
if (normalizeAdr0006PlanStatus(
  adr0006GovernanceFixtures.proposedPlan.replace(
    adr0006ProposedPlanStatus,
    `${adr0006ProposedPlanStatus}\n${adr0006ProposedPlanStatus}`,
  ),
  adr0006ProposedPlanStatus,
) !== null) {
  fail('ADR 0006 plan-normalization self-test accepted duplicate canonical status lines.')
}
if (normalizeAdr0006MemoryStatus(
  `${adr0006GovernanceFixtures.proposedMemory}\n${adr0006ProposedMemoryStatus}`,
  adr0006ProposedMemoryStatus,
) !== null) {
  fail('ADR 0006 current-state normalization self-test accepted duplicate canonical status lines.')
}

const validateAdr0006GovernanceSnapshot = (
  snapshot,
  label,
  expectedStatus,
  expectedEvidence = null,
  allowHistoricalBaseMemory = false,
) => {
  const snapshotFailures = []
  const reject = (message) => snapshotFailures.push(message)
  for (const [fileLabel, blob] of [
    ['ADR', snapshot.adr],
    ['system plan', snapshot.plan],
    ['current state', snapshot.memory],
  ]) {
    if (blob.error) {
      reject(`ADR 0006 ${label} ${fileLabel} could not be read from Git: ${blob.error}`)
    }
  }
  for (const [path, treeResult] of snapshot.entries) {
    if (treeResult.missing) {
      reject(`ADR 0006 ${label} is missing ${path}.`)
    } else if (treeResult.error) {
      reject(`ADR 0006 ${label} tree entry for ${path} is invalid: ${treeResult.error}`)
    } else if (treeResult.entry.mode !== '100644' || treeResult.entry.type !== 'blob') {
      reject(`ADR 0006 ${label} must keep ${path} as a regular 100644 blob.`)
    }
  }
  if (snapshotFailures.length > 0) {
    return snapshotFailures
  }

  for (const governanceFailure of validateAdr0006Governance(
    snapshot.adr.source,
    snapshot.plan.source,
  )) {
    reject(`ADR 0006 ${label} governance is invalid: ${governanceFailure}`)
  }
  const snapshotEvidence = parseAdr0006Evidence(snapshot.adr.source)
  if (snapshotEvidence.status !== expectedStatus) {
    reject(`ADR 0006 ${label} must contain the exact ${expectedStatus} ADR state.`)
  }
  if (expectedEvidence
      && JSON.stringify(snapshotEvidence) !== JSON.stringify(expectedEvidence)) {
    reject(`ADR 0006 ${label} metadata must equal the checked ADR metadata exactly.`)
  }
  for (const memoryFailure of validateAdr0006CurrentState(
    snapshot.memory.source,
    expectedStatus,
    expectedStatus === 'Accepted' ? snapshotEvidence.approvalUrl : undefined,
    selectAdr0006CurrentStateReferenceDigests(
      snapshot.entries.get(currentStatePath)?.entry?.object,
      allowHistoricalBaseMemory,
    ),
  )) {
    reject(`ADR 0006 ${label} current state is invalid: ${memoryFailure}`)
  }
  return snapshotFailures
}

const sameGitTreeEntry = (left, right) => left
  && right
  && left.mode === right.mode
  && left.type === right.type
  && left.object === right.object

const exactAdr0006Status = (snapshot, expectedStatus) => !snapshot.adr.error
  && adrMetadataValues(snapshot.adr.source, 'Status').length === 1
  && adrMetadataLikeCount(snapshot.adr.source, 'Status') === 1
  && parseAdr0006Evidence(snapshot.adr.source).status === expectedStatus

const adr0006SnapshotStatus = (snapshot, allowAbsent = false) => {
  if (allowAbsent && snapshot.entries.get(adr0006Path)?.missing) {
    return 'Absent'
  }
  if (exactAdr0006Status(snapshot, 'Proposed')) {
    return 'Proposed'
  }
  if (exactAdr0006Status(snapshot, 'Accepted')) {
    return 'Accepted'
  }
  return null
}

const selectAdr0006RefPolicy = (baseStatus, headStatus, eventName) => {
  if (!['pull_request', 'push'].includes(eventName)) {
    return 'invalid'
  }
  if (headStatus === 'Proposed') {
    return ['Absent', 'Proposed'].includes(baseStatus) ? 'unsigned' : 'invalid'
  }
  if (headStatus !== 'Accepted') {
    return 'invalid'
  }
  if (baseStatus === 'Accepted') {
    return 'accepted-base'
  }
  if (!['Absent', 'Proposed'].includes(baseStatus)) {
    return 'invalid'
  }
  return eventName === 'pull_request' ? 'signing-transition' : 'accepted-push'
}

for (const [label, baseStatus, headStatus, eventName, expectedPolicy] of [
  ['missing base with Proposed head', 'Absent', 'Proposed', 'pull_request', 'unsigned'],
  ['Proposed base with Proposed head', 'Proposed', 'Proposed', 'pull_request', 'unsigned'],
  ['missing base with Accepted PR head', 'Absent', 'Accepted', 'pull_request', 'signing-transition'],
  ['Proposed base with Accepted PR head', 'Proposed', 'Accepted', 'pull_request', 'signing-transition'],
  ['Accepted base with Accepted PR head', 'Accepted', 'Accepted', 'pull_request', 'accepted-base'],
  ['missing base with Accepted push head', 'Absent', 'Accepted', 'push', 'accepted-push'],
  ['Accepted base with Accepted push head', 'Accepted', 'Accepted', 'push', 'accepted-base'],
  ['Accepted-to-Proposed rollback', 'Accepted', 'Proposed', 'pull_request', 'invalid'],
  ['invalid base state', null, 'Accepted', 'pull_request', 'invalid'],
  ['invalid head state', 'Proposed', null, 'pull_request', 'invalid'],
  ['invalid event', 'Proposed', 'Accepted', 'workflow_dispatch', 'invalid'],
]) {
  if (selectAdr0006RefPolicy(baseStatus, headStatus, eventName) !== expectedPolicy) {
    fail(`ADR 0006 ref-policy self-test rejected ${label}.`)
  }
}

const treeEntryFixture = {
  mode: '100644',
  type: 'blob',
  object: '0123456789abcdef0123456789abcdef01234567',
}
if (!sameGitTreeEntry(treeEntryFixture, { ...treeEntryFixture })) {
  fail('ADR 0006 protected-tree self-test rejected identical blob and mode identity.')
}
for (const [label, mutation] of [
  ['a mode change', { mode: '120000' }],
  ['a type change', { type: 'commit' }],
  ['a blob change', { object: '1123456789abcdef0123456789abcdef01234567' }],
]) {
  if (sameGitTreeEntry(treeEntryFixture, { ...treeEntryFixture, ...mutation })) {
    fail(`ADR 0006 protected-tree self-test accepted ${label}.`)
  }
}

const validateAdr0006ExactSigningTransition = (evidence, signingHead) => {
  const transitionFailures = []
  const reject = (message) => transitionFailures.push(message)
  const candidateHead = evidence.candidateHead
  const verifiedSigningHead = runReadOnlyGit(['rev-parse', '--verify', `${signingHead}^{commit}`])
  const verifiedCandidateHead = runReadOnlyGit(['rev-parse', '--verify', `${candidateHead}^{commit}`])
  if (verifiedSigningHead.status !== 0 || verifiedSigningHead.stdout.trim() !== signingHead) {
    reject('ADR0006_SIGNING_HEAD must resolve to the exact 40-character PR head commit.')
  }
  if (verifiedCandidateHead.status !== 0 || verifiedCandidateHead.stdout.trim() !== candidateHead) {
    reject('ADR 0006 Approved candidate head must resolve to the exact declared commit.')
  }
  if (transitionFailures.length > 0) {
    return transitionFailures
  }

  const ancestor = runReadOnlyGit(['merge-base', '--is-ancestor', candidateHead, signingHead])
  if (ancestor.status !== 0) {
    reject('ADR 0006 Approved candidate head must be an ancestor of ADR0006_SIGNING_HEAD.')
    return transitionFailures
  }

  const changedFilesResult = runReadOnlyGit(
    ['diff', '--name-only', '--no-renames', '-z', candidateHead, signingHead, '--'],
    null,
  )
  if (changedFilesResult.status !== 0) {
    reject(`ADR 0006 signing-only diff could not be read: ${changedFilesResult.stderr.toString('utf8').trim()}`)
    return transitionFailures
  }
  const changedFiles = changedFilesResult.stdout.toString('utf8').split('\0').filter(Boolean)
  const allowedSigningFiles = [adr0006Path, systemPlanPath, currentStatePath]
  if (duplicates(changedFiles).length > 0 || !sameValues(changedFiles, allowedSigningFiles)) {
    reject(`ADR 0006 candidate..signing-head must change exactly ${allowedSigningFiles.join(' and ')}.`)
  }

  const candidateAdr = readGitBlob(candidateHead, adr0006Path)
  const candidatePlan = readGitBlob(candidateHead, systemPlanPath)
  const candidateMemory = readGitBlob(candidateHead, currentStatePath)
  const signedAdr = readGitBlob(signingHead, adr0006Path)
  const signedPlan = readGitBlob(signingHead, systemPlanPath)
  const signedMemory = readGitBlob(signingHead, currentStatePath)
  for (const [label, blob] of [
    ['candidate ADR', candidateAdr],
    ['candidate system plan', candidatePlan],
    ['candidate current state', candidateMemory],
    ['signed-head ADR', signedAdr],
    ['signed-head system plan', signedPlan],
    ['signed-head current state', signedMemory],
  ]) {
    if (blob.error) {
      reject(`ADR 0006 ${label} could not be read from Git: ${blob.error}`)
    }
  }
  for (const [label, commit] of [
    ['candidate', candidateHead],
    ['signed-head', signingHead],
  ]) {
    for (const path of [adr0006Path, systemPlanPath, currentStatePath]) {
      const treeResult = readGitTreeEntry(commit, path)
      if (treeResult.missing) {
        reject(`ADR 0006 ${label} is missing ${path}.`)
      } else if (treeResult.error) {
        reject(`ADR 0006 ${label} tree entry for ${path} is invalid: ${treeResult.error}`)
      } else if (treeResult.entry.mode !== '100644' || treeResult.entry.type !== 'blob') {
        reject(`ADR 0006 ${label} must keep ${path} as a regular 100644 blob.`)
      }
    }
  }
  if (transitionFailures.length > 0) {
    return transitionFailures
  }

  for (const governanceFailure of validateAdr0006Governance(
    candidateAdr.source,
    candidatePlan.source,
  )) {
    reject(`ADR 0006 candidate governance is invalid: ${governanceFailure}`)
  }
  for (const governanceFailure of validateAdr0006Governance(
    signedAdr.source,
    signedPlan.source,
  )) {
    reject(`ADR 0006 signed-head governance is invalid: ${governanceFailure}`)
  }
  if (parseAdr0006Evidence(candidateAdr.source).status !== 'Proposed') {
    reject('ADR 0006 Approved candidate head must contain the exact Proposed ADR state.')
  }
  const signedEvidence = parseAdr0006Evidence(signedAdr.source)
  if (signedEvidence.status !== 'Accepted') {
    reject('ADR0006_SIGNING_HEAD must contain the exact Accepted ADR state.')
  }
  if (JSON.stringify(signedEvidence) !== JSON.stringify(evidence)) {
    reject('ADR0006_SIGNING_HEAD governance metadata must equal the checked ADR metadata exactly.')
  }
  for (const memoryFailure of validateAdr0006CurrentState(
    candidateMemory.source,
    'Proposed',
  )) {
    reject(`ADR 0006 candidate current state is invalid: ${memoryFailure}`)
  }
  for (const memoryFailure of validateAdr0006CurrentState(
    signedMemory.source,
    'Accepted',
    signedEvidence.approvalUrl,
  )) {
    reject(`ADR 0006 signed-head current state is invalid: ${memoryFailure}`)
  }
  if (normalizeAdr0006GovernanceMetadata(candidateAdr.source)
      !== normalizeAdr0006GovernanceMetadata(signedAdr.source)) {
    reject('ADR 0006 candidate and signing head differ outside the allowed governance metadata.')
  }
  const normalizedCandidatePlan = normalizeAdr0006PlanStatus(
    candidatePlan.source,
    adr0006ProposedPlanStatus,
  )
  const normalizedSignedPlan = normalizeAdr0006PlanStatus(
    signedPlan.source,
    signedEvidence.approvalUrl
      ? adr0006AcceptedPlanStatus(signedEvidence.approvalUrl)
      : '',
  )
  if (!normalizedCandidatePlan
      || !normalizedSignedPlan
      || normalizedCandidatePlan !== normalizedSignedPlan) {
    reject('ADR 0006 candidate and signing head system plans differ outside the normalized status bullet.')
  }
  const normalizedCandidateMemory = normalizeAdr0006MemoryStatus(
    candidateMemory.source,
    adr0006ProposedMemoryStatus,
  )
  const normalizedSignedMemory = normalizeAdr0006MemoryStatus(
    signedMemory.source,
    signedEvidence.approvalUrl
      ? adr0006AcceptedMemoryStatus(signedEvidence.approvalUrl)
      : '',
  )
  if (!normalizedCandidateMemory
      || !normalizedSignedMemory
      || normalizedCandidateMemory !== normalizedSignedMemory) {
    reject('ADR 0006 candidate and signing head current-state files differ outside the canonical status bullet.')
  }
  return transitionFailures
}

const selectUniqueAdr0006SigningTransition = (candidates, validateCandidate) => {
  const validCandidates = candidates.filter(
    (candidate) => validateCandidate(candidate).length === 0,
  )
  return validCandidates.length === 1 ? validCandidates[0] : null
}

if (selectUniqueAdr0006SigningTransition(
  ['candidate-a', 'candidate-b'],
  (candidate) => candidate === 'candidate-b' ? [] : ['invalid'],
) !== 'candidate-b') {
  fail('ADR 0006 transition-selection self-test rejected one unique valid signing commit.')
}
for (const [label, validator] of [
  ['no valid signing commit', () => ['invalid']],
  ['multiple valid signing commits', () => []],
]) {
  if (selectUniqueAdr0006SigningTransition(
    ['candidate-a', 'candidate-b'],
    validator,
  ) !== null) {
    fail(`ADR 0006 transition-selection self-test accepted ${label}.`)
  }
}

const validateAdr0006UnsignedRefState = (
  baseHead,
  signingHead,
  checkoutHead,
  eventName,
) => {
  const stateFailures = []
  const reject = (message) => stateFailures.push(message)
  for (const [label, commit] of [
    ['ADR0006_BASE_HEAD', baseHead],
    ['ADR0006_SIGNING_HEAD', signingHead],
    ['ADR0006_CHECKOUT_HEAD', checkoutHead],
  ]) {
    const verifiedCommit = runReadOnlyGit(['rev-parse', '--verify', `${commit}^{commit}`])
    if (verifiedCommit.status !== 0 || verifiedCommit.stdout.trim() !== commit) {
      reject(`${label} must resolve to its exact 40-character commit.`)
    }
  }
  if (stateFailures.length > 0) {
    return stateFailures
  }

  const baseSnapshot = readAdr0006GovernanceSnapshot(baseHead)
  const headSnapshot = readAdr0006GovernanceSnapshot(signingHead)
  const checkoutSnapshot = readAdr0006GovernanceSnapshot(checkoutHead)
  const baseStatus = adr0006SnapshotStatus(baseSnapshot, true)
  const headStatus = adr0006SnapshotStatus(headSnapshot)
  const checkoutStatus = adr0006SnapshotStatus(checkoutSnapshot)
  if (selectAdr0006RefPolicy(baseStatus, headStatus, eventName) !== 'unsigned'
      || checkoutStatus !== 'Proposed') {
    reject('ADR 0006 unsigned state requires an absent/Proposed base and exact Proposed event and checkout heads.')
    return stateFailures
  }
  if (baseStatus === 'Proposed') {
    for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
      baseSnapshot,
      'base',
      'Proposed',
    )) {
      reject(snapshotFailure)
    }
  }
  for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
    headSnapshot,
    'head',
    'Proposed',
  )) {
    reject(snapshotFailure)
  }
  for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
    checkoutSnapshot,
    'checkout',
    'Proposed',
  )) {
    reject(snapshotFailure)
  }
  return stateFailures
}

const validateAdr0006AcceptedRefHistory = (
  evidence,
  baseHead,
  signingHead,
  checkoutHead,
  eventName,
) => {
  const historyFailures = []
  const reject = (message) => historyFailures.push(message)
  const verifiedBaseHead = runReadOnlyGit(['rev-parse', '--verify', `${baseHead}^{commit}`])
  const verifiedSigningHead = runReadOnlyGit(['rev-parse', '--verify', `${signingHead}^{commit}`])
  const verifiedCheckoutHead = runReadOnlyGit(['rev-parse', '--verify', `${checkoutHead}^{commit}`])
  if (verifiedBaseHead.status !== 0 || verifiedBaseHead.stdout.trim() !== baseHead) {
    reject('ADR0006_BASE_HEAD must resolve to the exact 40-character event-base commit.')
  }
  if (verifiedSigningHead.status !== 0 || verifiedSigningHead.stdout.trim() !== signingHead) {
    reject('ADR0006_SIGNING_HEAD must resolve to the exact 40-character event-head commit.')
  }
  if (verifiedCheckoutHead.status !== 0 || verifiedCheckoutHead.stdout.trim() !== checkoutHead) {
    reject('ADR0006_CHECKOUT_HEAD must resolve to the exact 40-character checkout commit.')
  }
  if (eventName === 'push' && checkoutHead !== signingHead) {
    reject('A push must use the same exact ADR0006_CHECKOUT_HEAD and ADR0006_SIGNING_HEAD.')
  }
  if (historyFailures.length > 0) {
    return historyFailures
  }

  const baseSnapshot = readAdr0006GovernanceSnapshot(baseHead)
  const signedSnapshot = readAdr0006GovernanceSnapshot(signingHead)
  const checkoutSnapshot = readAdr0006GovernanceSnapshot(checkoutHead)
  for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
    signedSnapshot,
    'head',
    'Accepted',
    evidence,
  )) {
    reject(snapshotFailure)
  }
  for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
    checkoutSnapshot,
    'checkout',
    'Accepted',
    evidence,
  )) {
    reject(snapshotFailure)
  }
  if (historyFailures.length > 0) {
    return historyFailures
  }

  const baseStatus = adr0006SnapshotStatus(baseSnapshot, true)
  const headStatus = adr0006SnapshotStatus(signedSnapshot)
  const historyPolicy = selectAdr0006RefPolicy(baseStatus, headStatus, eventName)
  if (!['accepted-base', 'signing-transition', 'accepted-push'].includes(historyPolicy)) {
    reject('ADR 0006 Accepted state has no valid fail-closed base/head history policy.')
    return historyFailures
  }

  if (historyPolicy === 'accepted-base') {
    for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
      baseSnapshot,
      'base',
      'Accepted',
      evidence,
      true,
    )) {
      reject(snapshotFailure)
    }
    const baseAdrEntry = baseSnapshot.entries.get(adr0006Path)?.entry
    const signedAdrEntry = signedSnapshot.entries.get(adr0006Path)?.entry
    const checkoutAdrEntry = checkoutSnapshot.entries.get(adr0006Path)?.entry
    if (!sameGitTreeEntry(baseAdrEntry, signedAdrEntry)
        || !sameGitTreeEntry(baseAdrEntry, checkoutAdrEntry)
        || baseSnapshot.adr.source !== signedSnapshot.adr.source
        || baseSnapshot.adr.source !== checkoutSnapshot.adr.source) {
      reject('An Accepted ADR 0006 must remain byte- and mode-identical across base, event head, and checkout.')
    }
    return historyFailures
  }
  if (baseStatus === 'Proposed') {
    for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
      baseSnapshot,
      'base',
      'Proposed',
    )) {
      reject(snapshotFailure)
    }
  }
  if (historyFailures.length > 0 || historyPolicy === 'accepted-push') {
    return historyFailures
  }

  const candidateHead = evidence.candidateHead
  const verifiedCandidateHead = runReadOnlyGit(
    ['rev-parse', '--verify', `${candidateHead}^{commit}`],
  )
  if (verifiedCandidateHead.status !== 0
      || verifiedCandidateHead.stdout.trim() !== candidateHead) {
    reject('ADR 0006 Approved candidate head must resolve to the exact declared commit.')
    return historyFailures
  }
  const candidateAncestor = runReadOnlyGit(
    ['merge-base', '--is-ancestor', candidateHead, signingHead],
  )
  if (candidateAncestor.status !== 0) {
    reject('ADR 0006 Approved candidate head must be an ancestor of ADR0006_SIGNING_HEAD.')
    return historyFailures
  }

  const commitList = runReadOnlyGit([
    'rev-list',
    '--reverse',
    '--ancestry-path',
    `${candidateHead}..${signingHead}`,
  ])
  if (commitList.status !== 0) {
    reject(`ADR 0006 signing history could not be read: ${(commitList.stderr || commitList.stdout).trim()}`)
    return historyFailures
  }
  const candidates = commitList.stdout.split(/\r?\n/u).filter(Boolean)
  const transitionEdges = []
  for (const candidate of candidates) {
    const candidateSnapshot = readAdr0006GovernanceSnapshot(candidate)
    if (!exactAdr0006Status(candidateSnapshot, 'Accepted')) {
      continue
    }
    const parentsResult = runReadOnlyGit(['rev-list', '--parents', '-n', '1', candidate])
    if (parentsResult.status !== 0) {
      reject('ADR 0006 signing-transition parents could not be read.')
      return historyFailures
    }
    const commits = parentsResult.stdout.trim().split(/\s+/u)
    if (commits[0] !== candidate || commits.length < 2) {
      reject('ADR 0006 signing-transition ancestry is malformed.')
      return historyFailures
    }
    const parentSnapshots = commits.slice(1).map(readAdr0006GovernanceSnapshot)
    if (parentSnapshots.some((snapshot) => exactAdr0006Status(snapshot, 'Proposed'))) {
      transitionEdges.push({ candidate, parentCommits: commits.slice(1), parentSnapshots })
    }
  }
  if (transitionEdges.length !== 1) {
    reject('ADR 0006 candidate..event-head history must contain exactly one Proposed-to-Accepted transition edge.')
    return historyFailures
  }
  const [{ candidate: signingTransition, parentCommits, parentSnapshots }] = transitionEdges
  if (parentCommits.length !== 1) {
    reject('ADR 0006 signing transition must be one non-merge commit.')
    return historyFailures
  }
  for (const snapshotFailure of validateAdr0006GovernanceSnapshot(
    parentSnapshots[0],
    'signing-transition parent',
    'Proposed',
  )) {
    reject(snapshotFailure)
  }
  for (const transitionFailure of validateAdr0006ExactSigningTransition(
    evidence,
    signingTransition,
  )) {
    reject(transitionFailure)
  }
  if (historyFailures.length > 0) {
    return historyFailures
  }

  const transitionSnapshot = readAdr0006GovernanceSnapshot(signingTransition)
  const descendantList = runReadOnlyGit([
    'rev-list',
    '--ancestry-path',
    `${signingTransition}..${signingHead}`,
  ])
  if (descendantList.status !== 0) {
    reject('ADR 0006 post-signing descendants could not be read.')
    return historyFailures
  }
  const descendantSnapshots = descendantList.stdout
    .split(/\r?\n/u)
    .filter(Boolean)
    .map((commit) => [commit, readAdr0006GovernanceSnapshot(commit)])
  for (const path of adr0006ProtectedSigningFiles) {
    const transitionEntry = transitionSnapshot.entries.get(path)
    const signedEntry = signedSnapshot.entries.get(path)
    const checkoutEntry = checkoutSnapshot.entries.get(path)
    if (transitionEntry?.error
        || signedEntry?.error
        || checkoutEntry?.error
        || !sameGitTreeEntry(transitionEntry?.entry, signedEntry?.entry)
        || !sameGitTreeEntry(transitionEntry?.entry, checkoutEntry?.entry)) {
      reject(`ADR 0006 post-signing bytes or mode changed at the event or checkout head for ${path}.`)
    }
    for (const [commit, descendantSnapshot] of descendantSnapshots) {
      const descendantEntry = descendantSnapshot.entries.get(path)
      if (descendantEntry?.error
          || !sameGitTreeEntry(transitionEntry?.entry, descendantEntry?.entry)) {
        reject(`ADR 0006 post-signing commit ${commit} changed bytes or mode for ${path}.`)
      }
    }
  }
  return historyFailures
}

const adr0006GithubApiResources = Object.freeze({
  issueComments: Object.freeze({
    url: 'https://api.github.com/repos/Lyon1984/PoolAI/issues/44/comments?per_page=100',
    collectionProperty: null,
  }),
  qualityRuns: Object.freeze({
    url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/quality-gate.yml/runs?event=pull_request&per_page=100',
    collectionProperty: 'workflow_runs',
  }),
  securityRuns: Object.freeze({
    url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/security-evidence.yml/runs?event=pull_request&per_page=100',
    collectionProperty: 'workflow_runs',
  }),
  qualityWorkflow: Object.freeze({
    url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/quality-gate.yml',
  }),
  securityWorkflow: Object.freeze({
    url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/security-evidence.yml',
  }),
})
const adr0006GithubApiPageSize = 100
const adr0006GithubApiMaxPages = 100

const selectUniqueGithubEvidencePayload = (collection, expectedHtmlUrl, label) => {
  if (!Array.isArray(collection)) {
    throw new Error(`${label} GitHub API collection is not an array.`)
  }
  const matches = collection.filter((payload) => payload?.html_url === expectedHtmlUrl)
  if (matches.length !== 1) {
    throw new Error(`${label} GitHub API collection must contain exactly one declared evidence URL.`)
  }
  return matches[0]
}

const registeredAdr0006GithubApiResource = (resourceName) => {
  const resource = adr0006GithubApiResources[resourceName]
  if (!resource || typeof resource.url !== 'string') {
    throw new Error('ADR 0006 GitHub API request used an unregistered fixed resource.')
  }
  return resource
}

const githubApiRequest = async (resourceName, token, page = null) => {
  const resource = registeredAdr0006GithubApiResource(resourceName)
  const isCollection = Object.hasOwn(resource, 'collectionProperty')
  if ((isCollection && (!Number.isSafeInteger(page) || page < 1 || page > adr0006GithubApiMaxPages))
      || (!isCollection && page !== null)) {
    throw new Error('ADR 0006 GitHub API request used an invalid internal page number.')
  }
  const url = isCollection ? `${resource.url}&page=${page}` : resource.url
  const response = await fetch(url, {
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'User-Agent': 'PoolAI-ADR0006-evidence-validator',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    redirect: 'error',
    signal: AbortSignal.timeout(15_000),
  })
  if (!response.ok) {
    throw new Error(`GitHub API returned HTTP ${response.status} for ${url}`)
  }
  return response.json()
}

const collectRegisteredGithubApiCollection = async (resourceName, loadPage) => {
  const resource = registeredAdr0006GithubApiResource(resourceName)
  if (!Object.hasOwn(resource, 'collectionProperty')) {
    throw new Error(`${resourceName} is not a registered GitHub API collection.`)
  }
  const collected = []
  for (let page = 1; page <= adr0006GithubApiMaxPages; page += 1) {
    const payload = await loadPage(page)
    const pageItems = resource.collectionProperty === null
      ? payload
      : payload?.[resource.collectionProperty]
    if (!Array.isArray(pageItems)) {
      throw new Error(`${resourceName} GitHub API page is not the registered collection shape.`)
    }
    collected.push(...pageItems)
    if (pageItems.length < adr0006GithubApiPageSize) {
      return collected
    }
  }
  throw new Error(`${resourceName} GitHub API collection exceeded the fail-closed page limit.`)
}

const githubApiGet = async (resourceName, token) => {
  const resource = registeredAdr0006GithubApiResource(resourceName)
  return Object.hasOwn(resource, 'collectionProperty')
    ? collectRegisteredGithubApiCollection(
      resourceName,
      (page) => githubApiRequest(resourceName, token, page),
    )
    : githubApiRequest(resourceName, token)
}

const unrelatedGithubPayloadFixture = { html_url: 'https://github.com/Lyon1984/PoolAI/other' }
if (selectUniqueGithubEvidencePayload(
  [unrelatedGithubPayloadFixture, validCommentPayloadFixture],
  acceptedEvidenceFixture.approvalUrl,
  'self-test comment',
) !== validCommentPayloadFixture) {
  fail('ADR 0006 GitHub-evidence self-test failed to select the one exact declared payload URL.')
}
for (const [label, collection] of [
  ['a non-array API collection', { workflow_runs: [] }],
  ['a missing declared payload', [unrelatedGithubPayloadFixture]],
  ['duplicate declared payloads', [validCommentPayloadFixture, { ...validCommentPayloadFixture }]],
]) {
  let rejected = false
  try {
    selectUniqueGithubEvidencePayload(
      collection,
      acceptedEvidenceFixture.approvalUrl,
      'self-test comment',
    )
  } catch {
    rejected = true
  }
  if (!rejected) {
    fail(`ADR 0006 GitHub-evidence self-test accepted ${label}.`)
  }
}

const fullUnrelatedRunPageFixture = Array.from(
  { length: adr0006GithubApiPageSize },
  (_value, index) => ({ html_url: `https://github.com/Lyon1984/PoolAI/actions/runs/${6000000000 + index}` }),
)
const twoPageRunCollectionFixture = await collectRegisteredGithubApiCollection(
  'qualityRuns',
  async (page) => ({
    workflow_runs: page === 1 ? fullUnrelatedRunPageFixture : [validQualityRunPayloadFixture],
  }),
)
if (selectUniqueGithubEvidencePayload(
  twoPageRunCollectionFixture,
  acceptedEvidenceFixture.qualityCiUrl,
  'self-test paginated run',
) !== validQualityRunPayloadFixture) {
  fail('ADR 0006 GitHub-evidence self-test failed to select evidence from the second API page.')
}

const expectGithubCollectionRejection = async (label, action) => {
  let rejected = false
  try {
    await action()
  } catch {
    rejected = true
  }
  if (!rejected) {
    fail(`ADR 0006 GitHub-evidence self-test accepted ${label}.`)
  }
}

await expectGithubCollectionRejection('a comments page with a non-array payload', () =>
  collectRegisteredGithubApiCollection('issueComments', async () => ({ comments: [] })))
await expectGithubCollectionRejection('a runs page missing workflow_runs', () =>
  collectRegisteredGithubApiCollection('qualityRuns', async () => ({})))
await expectGithubCollectionRejection('a runs page with a non-array workflow_runs value', () =>
  collectRegisteredGithubApiCollection('qualityRuns', async () => ({ workflow_runs: {} })))
await expectGithubCollectionRejection('a full paginated collection without the declared run', async () => {
  const collection = await collectRegisteredGithubApiCollection(
    'qualityRuns',
    async (page) => ({
      workflow_runs: page === 1
        ? fullUnrelatedRunPageFixture
        : [unrelatedGithubPayloadFixture],
    }),
  )
  selectUniqueGithubEvidencePayload(
    collection,
    acceptedEvidenceFixture.qualityCiUrl,
    'self-test missing paginated run',
  )
})
await expectGithubCollectionRejection('the same declared run duplicated across API pages', async () => {
  const firstPage = [validQualityRunPayloadFixture, ...fullUnrelatedRunPageFixture.slice(1)]
  const collection = await collectRegisteredGithubApiCollection(
    'qualityRuns',
    async (page) => ({
      workflow_runs: page === 1 ? firstPage : [validQualityRunPayloadFixture],
    }),
  )
  selectUniqueGithubEvidencePayload(
    collection,
    acceptedEvidenceFixture.qualityCiUrl,
    'self-test duplicate paginated run',
  )
})
let maxPageProbeCalls = 0
await expectGithubCollectionRejection('a collection that fills the fail-closed page limit', () =>
  collectRegisteredGithubApiCollection('qualityRuns', async () => {
    maxPageProbeCalls += 1
    return { workflow_runs: fullUnrelatedRunPageFixture }
  }))
if (maxPageProbeCalls !== adr0006GithubApiMaxPages) {
  fail('ADR 0006 GitHub-evidence page-limit self-test did not exhaust exactly the registered maximum pages.')
}

const validateAdr0006GithubEvidence = async () => {
  const evidenceFailures = []
  const reject = (message) => evidenceFailures.push(message)
  const evidence = parseAdr0006Evidence(adr0006)
  const eventName = process.env.ADR0006_EVENT_NAME
  const baseHead = process.env.ADR0006_BASE_HEAD
  const checkoutHead = process.env.ADR0006_CHECKOUT_HEAD
  const signingHead = process.env.ADR0006_SIGNING_HEAD
  if (!['pull_request', 'push'].includes(eventName)) {
    reject('ADR 0006 GitHub evidence requires ADR0006_EVENT_NAME=pull_request or push.')
  }
  if (!/^(?!0{40}$)[0-9a-f]{40}$/u.test(signingHead ?? '')) {
    reject('ADR 0006 GitHub evidence requires one exact ADR0006_SIGNING_HEAD SHA.')
  }
  if (!/^(?!0{40}$)[0-9a-f]{40}$/u.test(baseHead ?? '')) {
    reject('ADR 0006 GitHub evidence requires one exact ADR0006_BASE_HEAD SHA.')
  }
  if (!/^(?!0{40}$)[0-9a-f]{40}$/u.test(checkoutHead ?? '')) {
    reject('ADR 0006 GitHub evidence requires one exact ADR0006_CHECKOUT_HEAD SHA.')
  }
  if (evidenceFailures.length > 0) {
    return evidenceFailures
  }

  if (evidence.status === 'Proposed') {
    for (const stateFailure of validateAdr0006UnsignedRefState(
      baseHead,
      signingHead,
      checkoutHead,
      eventName,
    )) {
      reject(stateFailure)
    }
    return evidenceFailures
  }
  if (evidence.status !== 'Accepted') {
    reject('ADR 0006 GitHub evidence can be checked only for an exact Proposed or Accepted state.')
    return evidenceFailures
  }

  const token = process.env.GITHUB_TOKEN
  if (typeof token !== 'string' || token.trim().length === 0) {
    reject('Accepted ADR 0006 GitHub evidence requires a non-empty GITHUB_TOKEN.')
  }
  for (const transitionFailure of validateAdr0006AcceptedRefHistory(
    evidence,
    baseHead,
    signingHead,
    checkoutHead,
    eventName,
  )) {
    reject(transitionFailure)
  }
  if (evidenceFailures.length > 0) {
    return evidenceFailures
  }

  try {
    const [comments, qualityRuns, securityRuns, qualityWorkflow, securityWorkflow] = await Promise.all([
      githubApiGet('issueComments', token),
      githubApiGet('qualityRuns', token),
      githubApiGet('securityRuns', token),
      githubApiGet('qualityWorkflow', token),
      githubApiGet('securityWorkflow', token),
    ])
    const comment = selectUniqueGithubEvidencePayload(
      comments,
      evidence.approvalUrl,
      'ADR 0006 approval comment',
    )
    const signingLifecycleComment = selectUniqueGithubEvidencePayload(
      comments,
      adr0006SigningLifecycleApprovalUrl,
      'ADR 0006 signing-lifecycle clarification comment',
    )
    const qualityRun = selectUniqueGithubEvidencePayload(
      qualityRuns,
      evidence.qualityCiUrl,
      'ADR 0006 quality-gate run',
    )
    const securityRun = selectUniqueGithubEvidencePayload(
      securityRuns,
      evidence.securityCiUrl,
      'ADR 0006 security-evidence run',
    )
    for (const payloadFailure of validateAdr0006CommentPayload(comment, evidence)) {
      reject(payloadFailure)
    }
    for (const payloadFailure of validateAdr0006SigningLifecycleCommentPayload(
      signingLifecycleComment,
    )) {
      reject(payloadFailure)
    }
    for (const payloadFailure of validateAdr0006WorkflowPayload(
      qualityWorkflow,
      'quality-gate',
      '.github/workflows/quality-gate.yml',
    )) {
      reject(payloadFailure)
    }
    for (const payloadFailure of validateAdr0006WorkflowPayload(
      securityWorkflow,
      'security-evidence',
      '.github/workflows/security-evidence.yml',
    )) {
      reject(payloadFailure)
    }
    for (const payloadFailure of validateAdr0006RunPayload(
      qualityRun,
      qualityWorkflow,
      'quality-gate',
      '.github/workflows/quality-gate.yml',
      evidence.qualityCiUrl,
      evidence.candidateHead,
    )) {
      reject(payloadFailure)
    }
    for (const payloadFailure of validateAdr0006RunPayload(
      securityRun,
      securityWorkflow,
      'security-evidence',
      '.github/workflows/security-evidence.yml',
      evidence.securityCiUrl,
      evidence.candidateHead,
    )) {
      reject(payloadFailure)
    }
  } catch (error) {
    reject(`Accepted ADR 0006 GitHub API evidence verification failed closed: ${error.message}`)
  }
  return evidenceFailures
}
for (const [label, payload] of [
  ['the wrong workflow name', { ...validQualityRunPayloadFixture, name: 'quality' }],
  ['an in-progress run', { ...validQualityRunPayloadFixture, status: 'in_progress' }],
  ['a failed run', { ...validQualityRunPayloadFixture, conclusion: 'failure' }],
  ['a different head', { ...validQualityRunPayloadFixture, head_sha: '1123456789abcdef0123456789abcdef01234567' }],
  ['a URL suffix', { ...validQualityRunPayloadFixture, html_url: `${acceptedEvidenceFixture.qualityCiUrl}/attempts/1` }],
  ['a different repository', { ...validQualityRunPayloadFixture, repository: { full_name: 'Lyon1984/Other' } }],
  ['a different head repository', { ...validQualityRunPayloadFixture, head_repository: { full_name: 'Lyon1984/Other' } }],
  ['a push event', { ...validQualityRunPayloadFixture, event: 'push' }],
  ['a different workflow path', { ...validQualityRunPayloadFixture, path: '.github/workflows/other.yml' }],
  ['a different workflow id', { ...validQualityRunPayloadFixture, workflow_id: 102 }],
  ['a different workflow URL', { ...validQualityRunPayloadFixture, workflow_url: 'https://api.github.com/repos/Lyon1984/PoolAI/actions/workflows/102' }],
]) {
  if (validateAdr0006RunPayload(
    payload,
    validQualityWorkflowPayloadFixture,
    'quality-gate',
    '.github/workflows/quality-gate.yml',
    acceptedEvidenceFixture.qualityCiUrl,
    acceptedEvidenceFixture.candidateHead,
  ).length === 0) {
    fail(`ADR 0006 GitHub-evidence self-test accepted ${label}.`)
  }
}
for (const [label, payload] of [
  ['a different workflow name', { ...validQualityWorkflowPayloadFixture, name: 'quality' }],
  ['a different workflow path', { ...validQualityWorkflowPayloadFixture, path: '.github/workflows/other.yml' }],
  ['a disabled workflow', { ...validQualityWorkflowPayloadFixture, state: 'disabled_manually' }],
  ['a mismatched workflow id URL', { ...validQualityWorkflowPayloadFixture, id: 102 }],
]) {
  if (validateAdr0006WorkflowPayload(
    payload,
    'quality-gate',
    '.github/workflows/quality-gate.yml',
  ).length === 0) {
    fail(`ADR 0006 GitHub-evidence self-test accepted ${label}.`)
  }
}
if (xunitRunnerConfig.failSkips !== true) {
  fail('Traceability test evidence requires tests/xunit.runner.json to set failSkips to true.')
}
if (!qualityGate.split('\n').includes('node eng/test/verify-fail-skips.mjs')) {
  fail('The quality gate must execute the dynamic xUnit skip policy probe.')
}
if (!qualityGate.split('\n').includes('node eng/test/verify-repository-file-safety.mjs')) {
  fail('The quality gate must execute the repository evidence file safety probe.')
}

let compiledTestInventory = null
if (compiledTests) {
  const discovery = spawnSync(
    'dotnet',
    ['test', 'PoolAI.sln', '--no-build', '--list-tests'],
    {
      cwd: root,
      encoding: 'utf8',
      env: {
        ...process.env,
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
        DOTNET_NOLOGO: '1',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
        DOTNET_MULTILEVEL_LOOKUP: '0',
        MSBUILDDISABLENODEREUSE: '1',
      },
      maxBuffer: 16 * 1024 * 1024,
    },
  )
  if (discovery.status !== 0) {
    fail(`Compiled xUnit discovery failed: ${(discovery.stderr || discovery.stdout || 'no output').trim()}`)
  } else {
    compiledTestInventory = discovery.stdout.split(/\r?\n/u).map((line) => line.trim())
  }
}

const expandRegistrySection = (cell, prefix) => {
  const match = cell.match(new RegExp(`${prefix}-(\\d{3}(?:[–-]\\d{3})?(?:/\\d{3}(?:[–-]\\d{3})?)*)`, 'u'))
  if (!match) {
    return []
  }

  const ids = []
  for (const token of match[1].split('/')) {
    const range = token.match(/^(\d{3})(?:[–-](\d{3}))?$/u)
    if (!range) {
      fail(`Cannot parse ${prefix} coverage token from the workstream matrix: ${token}`)
      continue
    }
    const start = Number(range[1])
    const end = Number(range[2] ?? range[1])
    for (let number = start; number <= end; number += 1) {
      ids.push(`${prefix}-${String(number).padStart(3, '0')}`)
    }
  }
  return ids
}

const applicabilityById = new Map()
for (const line of systemPlan.split('\n')) {
  const row = line.match(/^\| (WS-\d{2}) [^|]*\| ([^|]+) \| ([^|]+) \|/u)
  if (!row || !row[3].includes('DEC-') || !row[3].includes('AC-')) {
    continue
  }
  const [, workstream, , coverageCell] = row
  for (const id of [
    ...expandRegistrySection(coverageCell, 'DEC'),
    ...expandRegistrySection(coverageCell, 'AC'),
  ]) {
    if (!applicabilityById.has(id)) {
      applicabilityById.set(id, new Set())
    }
    applicabilityById.get(id).add(workstream)
  }
}

const validatePath = (path, label) => {
  try {
    return resolveCanonicalRepositoryPath(root, path)
  } catch (error) {
    if (error?.code === repositoryFileErrorCodes.invalidRelativePath) {
      fail(`${label} must be a non-empty repository-relative path.`)
    } else if (error?.code === repositoryFileErrorCodes.lexicalEscape) {
      fail(`${label} resolves outside the repository: ${path}`)
    } else if (error?.code === repositoryFileErrorCodes.canonicalEscape) {
      fail(`${label} points outside the repository: ${path}`)
    } else if (error?.code === 'ENOENT') {
      fail(`${label} does not exist: ${path}`)
    } else {
      fail(`${label} cannot be resolved safely (${error?.code ?? 'UNKNOWN'}): ${path}`)
    }
    return null
  }
}

const validateMapping = (entry, label) => {
  if (!/^WS-0[1-8]$/u.test(entry.workstream ?? '')) {
    fail(`${label}.workstream must be WS-01..WS-08.`)
  } else if (!systemPlan.includes(`| ${entry.workstream} `)) {
    fail(`${label}.workstream is not registered in the system-plan matrix: ${entry.workstream}`)
  } else if (!applicabilityById.get(entry.id)?.has(entry.workstream)) {
    fail(`${label}.workstream is not an allowed owner for ${entry.id} in the system-plan traceability matrix.`)
  }
  if (!Array.isArray(entry.epics) || entry.epics.length === 0
      || entry.epics.some((epic) => !/^M[0-6]-E[1-9][0-9]*$/u.test(epic))) {
    fail(`${label}.epics must contain valid M0..M6 Epic IDs.`)
  } else {
    if (duplicates(entry.epics).length > 0) {
      fail(`${label}.epics contains duplicates: ${duplicates(entry.epics).join(', ')}`)
    }
    for (const epic of entry.epics) {
      if (!executionSpec.includes(`| ${epic} `)) {
        fail(`${label}.epics contains an unregistered Epic: ${epic}`)
      }
    }
  }
  if (!Array.isArray(entry.contracts) || entry.contracts.length === 0) {
    fail(`${label}.contracts must be non-empty.`)
  } else {
    if (duplicates(entry.contracts).length > 0) {
      fail(`${label}.contracts contains duplicates: ${duplicates(entry.contracts).join(', ')}`)
    }
    for (const [index, path] of entry.contracts.entries()) {
      validatePath(path, `${label}.contracts[${index}]`)
    }
  }
}

const validatePlannedTest = (value, label) => {
  if (!allowedSuites.has(value.testSuite)) {
    fail(`${label}.testSuite must name one of the six physical test projects.`)
  }
  if (typeof value.plannedTest !== 'string' || !/^[A-Z][A-Za-z0-9]+$/u.test(value.plannedTest)) {
    fail(`${label}.plannedTest must be a stable PascalCase test name.`)
  }
}

const stripCSharpCommentsAndLiterals = (source) => {
  const output = source.split('')
  const blank = (index) => {
    if (output[index] !== '\n' && output[index] !== '\r') {
      output[index] = ' '
    }
  }
  let index = 0
  while (index < source.length) {
    if (source.startsWith('//', index)) {
      blank(index)
      blank(index + 1)
      index += 2
      while (index < source.length && source[index] !== '\n') {
        blank(index)
        index += 1
      }
      continue
    }
    if (source.startsWith('/*', index)) {
      blank(index)
      blank(index + 1)
      index += 2
      while (index < source.length && !source.startsWith('*/', index)) {
        blank(index)
        index += 1
      }
      if (index < source.length) {
        blank(index)
        blank(index + 1)
        index += 2
      }
      continue
    }
    if (source[index] === '"') {
      let quoteCount = 1
      while (source[index + quoteCount] === '"') {
        quoteCount += 1
      }
      if (quoteCount >= 3) {
        const delimiter = '"'.repeat(quoteCount)
        for (let offset = 0; offset < quoteCount; offset += 1) {
          blank(index + offset)
        }
        index += quoteCount
        while (index < source.length && !source.startsWith(delimiter, index)) {
          blank(index)
          index += 1
        }
        for (let offset = 0; offset < quoteCount && index < source.length; offset += 1) {
          blank(index)
          index += 1
        }
        continue
      }

      const verbatim = index > 0 && source[index - 1] === '@'
      blank(index)
      index += 1
      while (index < source.length) {
        if (verbatim && source.startsWith('""', index)) {
          blank(index)
          blank(index + 1)
          index += 2
        } else if (!verbatim && source[index] === '\\') {
          blank(index)
          if (index + 1 < source.length) {
            blank(index + 1)
          }
          index += 2
        } else if (source[index] === '"') {
          blank(index)
          index += 1
          break
        } else {
          blank(index)
          index += 1
        }
      }
      continue
    }
    if (source[index] === "'") {
      blank(index)
      index += 1
      while (index < source.length) {
        if (source[index] === '\\') {
          blank(index)
          if (index + 1 < source.length) {
            blank(index + 1)
          }
          index += 2
        } else if (source[index] === "'") {
          blank(index)
          index += 1
          break
        } else {
          blank(index)
          index += 1
        }
      }
      continue
    }
    index += 1
  }
  return output.join('')
}

const stripCSharpConditionalRegions = (source) => {
  let depth = 0
  return source.split(/(?<=\n)/u).map((line) => {
    const directive = line.match(/^\s*#\s*(if|elif|else|endif)\b/u)?.[1]
    if (directive === 'if') {
      depth += 1
      return line.replace(/[^\r\n]/gu, ' ')
    }
    if (directive === 'endif') {
      const stripped = line.replace(/[^\r\n]/gu, ' ')
      depth = Math.max(0, depth - 1)
      return stripped
    }
    if (depth > 0 || directive === 'elif' || directive === 'else') {
      return line.replace(/[^\r\n]/gu, ' ')
    }
    return line
  }).join('')
}

const isXunitTestMethod = (source, symbol) => {
  const escapedSymbol = symbol.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')
  const method = new RegExp(
    `^\\s*\\[(?:Fact|Theory)\\]\\s*$`
      + `(?:\\r?\\n\\s*\\[[^\\]\\r\\n]+\\]\\s*$)*`
      + `\\r?\\n\\s*(?:public|internal)\\s+(?:async\\s+)?`
      + `(?:Task|ValueTask|void)\\s+${escapedSymbol}\\s*\\(`,
    'mu',
  )
  const activeSource = stripCSharpConditionalRegions(source)
  return method.test(stripCSharpCommentsAndLiterals(activeSource))
}

const isDiscoveredTest = (path, symbol, inventory) => {
  if (!Array.isArray(inventory)) {
    return false
  }
  const segments = path.split('/')
  const suite = segments[1]
  const className = basename(path, '.cs')
  const fullyQualifiedName = `${suite}.${className}.${symbol}`
  return inventory.some((testName) => testName === fullyQualifiedName
    || testName.startsWith(`${fullyQualifiedName}(`))
}

const isCompiledTestEvidence = (source, path, symbol, inventory) =>
  isXunitTestMethod(source, symbol) && isDiscoveredTest(path, symbol, inventory)

for (const decoy of [
  '/* [Fact]\npublic void CommentOnly() { } */',
  'const string Value = "[Fact] public void CommentOnly(";',
  'const string Value = """[Fact] public void CommentOnly(""";',
  '[Fact(Skip = "pending")]\npublic void CommentOnly() { }',
  '#if false\n[Fact]\npublic void CommentOnly() { }\n#endif',
  'var value = $"{"[Fact] public void CommentOnly("}";',
]) {
  if (isXunitTestMethod(decoy, 'CommentOnly')) {
    fail('Traceability validator self-test accepted xUnit evidence from a C# comment or string literal.')
  }
}
if (!isXunitTestMethod('[Theory]\n[InlineData(1)]\npublic void RealTest(int value) { }', 'RealTest')) {
  fail('Traceability validator self-test rejected a real attributed xUnit method.')
}
const nestedInterpolationDecoy = 'var value = $"{@"\n[Fact]\npublic void FakeEvidence(\n"}";'
if (isCompiledTestEvidence(
  nestedInterpolationDecoy,
  'tests/PoolAI.UnitTests/DecoyTests.cs',
  'FakeEvidence',
  ['PoolAI.UnitTests.DecoyTests.SomeOtherTest'],
)) {
  fail('Traceability validator self-test accepted a non-discovered method from nested interpolation.')
}
if (!isCompiledTestEvidence(
  '[Fact]\npublic void RealTest() { }',
  'tests/PoolAI.UnitTests/DecoyTests.cs',
  'RealTest',
  ['PoolAI.UnitTests.DecoyTests.RealTest'],
)) {
  fail('Traceability validator self-test rejected a compiled/discovered xUnit method shape.')
}

const validateEvidence = (evidence, label) => {
  if (!Array.isArray(evidence) || evidence.length === 0) {
    fail(`${label} must contain at least one repository evidence reference.`)
    return
  }

  const allowedCommands = new Map([
    ['eng/policies/forbidden-scope.sh', 'eng/policies/forbidden-scope.sh'],
    ['tools/contracts/cli.mjs', 'pnpm --dir frontend contracts:check'],
  ])
  for (const [index, item] of evidence.entries()) {
    const itemLabel = `${label}[${index}]`
    let source
    try {
      source = withReadOnlyRepositoryFile(
        root,
        item?.path,
        (descriptor) => readFileSync(descriptor, 'utf8'),
      )
    } catch (error) {
      if (error?.code === repositoryFileErrorCodes.invalidRelativePath) {
        fail(`${itemLabel}.path must be a non-empty repository-relative path.`)
      } else if (error?.code === repositoryFileErrorCodes.lexicalEscape) {
        fail(`${itemLabel}.path resolves outside the repository: ${item?.path}`)
      } else if (error?.code === repositoryFileErrorCodes.canonicalEscape) {
        fail(`${itemLabel}.path points outside the repository: ${item?.path}`)
      } else if (error?.code === 'ENOENT') {
        fail(`${itemLabel}.path does not exist: ${item?.path}`)
      } else if (error?.code === repositoryFileErrorCodes.notRegularFile) {
        fail(`${itemLabel}.path must identify a file, not a directory.`)
      } else {
        fail(`${itemLabel}.path could not be opened safely (${error?.code ?? 'UNKNOWN'}).`)
      }
      continue
    }

    if (item.kind === 'test') {
      if (!item.path.startsWith('tests/') || !item.path.endsWith('.cs')) {
        fail(`${itemLabel} test evidence must identify a C# file under tests/.`)
        continue
      }
      if (typeof item.symbol !== 'string' || !/^[A-Z][A-Za-z0-9]+$/u.test(item.symbol)) {
        fail(`${itemLabel}.symbol must be a stable PascalCase xUnit method name.`)
        continue
      }
      if (!isXunitTestMethod(source, item.symbol)) {
        fail(`${itemLabel}.symbol is not an xUnit [Fact]/[Theory] method in ${item.path}: ${item.symbol}`)
      }
      if (compiledTests && !isDiscoveredTest(item.path, item.symbol, compiledTestInventory)) {
        fail(`${itemLabel}.symbol is not present in compiled xUnit discovery: ${item.symbol}`)
      }
      if (item.command !== undefined) {
        fail(`${itemLabel}.command is not allowed for test evidence.`)
      }
    } else if (item.kind === 'quality-gate-command') {
      const expectedCommand = allowedCommands.get(item.path)
      if (!expectedCommand || item.command !== expectedCommand) {
        fail(`${itemLabel} is not an approved traceability command for ${item.path}.`)
        continue
      }
      if (!qualityGate.split('\n').includes(item.command)) {
        fail(`${itemLabel}.command is not executed directly by eng/test/quality-gate.sh.`)
      }
      if (item.path === 'tools/contracts/cli.mjs'
          && frontendPackage.scripts?.['contracts:check'] !== 'node ../tools/contracts/cli.mjs all --check') {
        fail(`${itemLabel} contract command no longer resolves to the checked contract CLI.`)
      }
      if (item.symbol !== undefined) {
        fail(`${itemLabel}.symbol is not allowed for quality-gate command evidence.`)
      }
    } else {
      fail(`${itemLabel}.kind must be test or quality-gate-command.`)
    }
  }
}

const validateVerification = (verification, label) => {
  if (!verification || !['planned', 'partial', 'implemented-local'].includes(verification.status)) {
    fail(`${label}.status must be planned, partial, or implemented-local.`)
    return
  }

  if (verification.status === 'planned') {
    validatePlannedTest(verification, label)
    return
  }

  if (Array.isArray(verification.slices)) {
    if (verification.status !== 'partial' || verification.slices.length === 0) {
      fail(`${label}.slices are allowed only for a non-empty partial verification.`)
      return
    }
    const sliceIds = verification.slices.map((slice) => slice.id)
    if (duplicates(sliceIds).length > 0) {
      fail(`${label}.slices contains duplicate IDs: ${duplicates(sliceIds).join(', ')}`)
    }
    for (const [index, slice] of verification.slices.entries()) {
      const sliceLabel = `${label}.slices[${index}]`
      if (typeof slice.id !== 'string' || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(slice.id)) {
        fail(`${sliceLabel}.id must be lower kebab-case.`)
      }
      if (!/^M[0-6]-E[1-9][0-9]*$/u.test(slice.epic ?? '')) {
        fail(`${sliceLabel}.epic must be a valid M0..M6 Epic ID.`)
      }
      if (slice.status === 'implemented-local') {
        validateEvidence(slice.evidence, `${sliceLabel}.evidence`)
      } else if (slice.status === 'planned') {
        validatePlannedTest(slice, sliceLabel)
      } else {
        fail(`${sliceLabel}.status must be planned or implemented-local.`)
      }
    }
    if (!verification.slices.some((slice) => slice.status === 'implemented-local')
        || !verification.slices.some((slice) => slice.status === 'planned')) {
      fail(`${label}.slices must preserve both implemented and remaining planned work.`)
    }
    return
  }

  validateEvidence(verification.evidence, `${label}.evidence`)
  if (verification.status === 'partial') {
    validatePlannedTest(verification, label)
  }
}

for (const placeholderReference of auditableReferencePolicyCases.invalid) {
  if (isAuditableHttpsReference(placeholderReference)) {
    fail(`Traceability validator self-test accepted a reserved/local external reference: ${placeholderReference}`)
  }
}
if (!isAuditableHttpsReference(auditableReferencePolicyCases.valid)) {
  fail('Traceability validator self-test rejected an auditable HTTPS run reference shape.')
}
for (const placeholderOwner of namedOwnerPolicyCases.invalid) {
  if (isNamedOwner(placeholderOwner)) {
    fail(`Traceability validator self-test accepted a placeholder owner: ${placeholderOwner}`)
  }
}
if (!isNamedOwner(namedOwnerPolicyCases.valid)) {
  fail('Traceability validator self-test rejected a named owner shape.')
}
for (const placeholderTaskId of taskIdPolicyCases.invalid) {
  if (isAuditableTaskId(placeholderTaskId)) {
    fail(`Traceability validator self-test accepted a placeholder task ID: ${placeholderTaskId}`)
  }
}
if (!isAuditableTaskId(taskIdPolicyCases.valid)) {
  fail('Traceability validator self-test rejected an auditable task ID shape.')
}
if (!taskIdPolicyCases.equivalentUrls.every(isAuditableTaskId)
    || new Set(taskIdPolicyCases.equivalentUrls.map(canonicalAuditableTaskId)).size !== 1) {
  fail('Traceability validator self-test failed canonical task URL equivalence.')
}

const collectPlannedNames = (verification, plannedNames) => {
  if (verification?.plannedTest) {
    plannedNames.push(verification.plannedTest)
  }
  for (const slice of verification?.slices ?? []) {
    if (slice.plannedTest) {
      plannedNames.push(slice.plannedTest)
    }
  }
}

if (manifest.schemaVersion !== 1 || manifest.release !== 'R1.1') {
  fail('Traceability manifest must use schemaVersion 1 for release R1.1.')
}

for (const [key, expectedPath] of Object.entries({
  decisions: 'docs/开发执行规格-v1.0.md',
  acceptanceCriteria: 'docs/开发执行规格-v1.0.md',
  workstreamMatrix: 'docs/系统重构方案-v1.0.md',
})) {
  if (manifest.registries?.[key] !== expectedPath) {
    fail(`registries.${key} must remain ${expectedPath}.`)
  } else {
    validatePath(expectedPath, `registries.${key}`)
  }
}

if (!sameValues(Object.keys(manifest.externalEvidence ?? {}), externalKeys)) {
  fail(`externalEvidence must define exactly: ${externalKeys.join(', ')}.`)
}
for (const key of externalKeys) {
  const value = manifest.externalEvidence?.[key]
  if (!value || !['pending', 'verified'].includes(value.status)) {
    fail(`externalEvidence.${key}.status must be pending or verified.`)
  } else if (value.status === 'pending' && value.evidence !== null) {
    fail(`externalEvidence.${key}.evidence must be null while pending.`)
  } else if (value.status === 'verified' && !isAuditableHttpsReference(value.evidence)) {
    fail(`externalEvidence.${key}.evidence must be an auditable non-placeholder HTTPS reference when verified.`)
  }
}
if (manifest.externalEvidence?.m0ExitReview?.status === 'verified') {
  for (const prerequisite of externalKeys.filter((key) => key !== 'm0ExitReview')) {
    if (manifest.externalEvidence?.[prerequisite]?.status !== 'verified') {
      fail(`M0 exit review cannot be verified while ${prerequisite} remains pending.`)
    }
  }
}

if (!Array.isArray(manifest.decisions) || !Array.isArray(manifest.acceptanceCriteria)) {
  fail('decisions and acceptanceCriteria must both be arrays.')
} else {
  const decisionIds = manifest.decisions.map((entry) => entry.id)
  const acceptanceIds = manifest.acceptanceCriteria.map((entry) => entry.id)
  if (!sameValues(decisionIds, expectedDecisionIds) || duplicates(decisionIds).length > 0) {
    fail('decisions must contain DEC-001..DEC-042 exactly once.')
  }
  if (!sameValues(acceptanceIds, expectedAcceptanceIds) || duplicates(acceptanceIds).length > 0) {
    fail('acceptanceCriteria must contain AC-001..AC-045 exactly once.')
  }
  if (!sameValues(Object.keys(manifest.decisionVerification ?? {}), expectedDecisionIds)) {
    fail('decisionVerification must contain DEC-001..DEC-042 exactly once.')
  }

  const mappingSnapshot = [...manifest.decisions, ...manifest.acceptanceCriteria]
    .map((entry) => [entry.id, entry.workstream, entry.epics])
  const mappingDigest = createHash('sha256')
    .update(JSON.stringify(mappingSnapshot))
    .digest('hex')
  if (mappingDigest !== expectedMappingDigest) {
    fail(`DEC/AC workstream and Epic mapping review lock changed: ${mappingDigest}.`)
  }

  const taskSystemPending = manifest.externalEvidence?.taskSystem?.status === 'pending'
  const signoffPending = manifest.externalEvidence?.decisionSignoff?.status === 'pending'
  const plannedNames = []
  for (const [index, decision] of manifest.decisions.entries()) {
    const label = `decisions[${index}](${decision.id})`
    validateMapping(decision, label)
    const verification = manifest.decisionVerification?.[decision.id]
    validateVerification(verification, `decisionVerification.${decision.id}`)
    collectPlannedNames(verification, plannedNames)
    if (taskSystemPending && (decision.externalTaskId !== null || decision.owner !== null)) {
      fail(`${label} cannot invent an external task or owner while taskSystem is pending.`)
    } else if (!taskSystemPending
        && (!isAuditableTaskId(decision.externalTaskId) || !isNamedOwner(decision.owner))) {
      fail(`${label} requires an auditable task ID and named owner once taskSystem is verified.`)
    }
    if (signoffPending && decision.signoff !== null) {
      fail(`${label} cannot invent signoff while decisionSignoff is pending.`)
    } else if (!signoffPending && !isAuditableHttpsReference(decision.signoff)) {
      fail(`${label} requires an auditable signoff HTTPS reference once decisionSignoff is verified.`)
    }
  }

  for (const [index, criterion] of manifest.acceptanceCriteria.entries()) {
    const label = `acceptanceCriteria[${index}](${criterion.id})`
    validateMapping(criterion, label)
    validateVerification(criterion.verification, `${label}.verification`)
    if (taskSystemPending && (criterion.externalTaskId !== null || criterion.owner !== null)) {
      fail(`${label} cannot invent an external task or owner while taskSystem is pending.`)
    } else if (!taskSystemPending
        && (!isAuditableTaskId(criterion.externalTaskId) || !isNamedOwner(criterion.owner))) {
      fail(`${label} requires an auditable task ID and named owner once taskSystem is verified.`)
    }
    collectPlannedNames(criterion.verification, plannedNames)
  }
  if (duplicates(plannedNames).length > 0) {
    fail(`planned test names must be unique: ${duplicates(plannedNames).join(', ')}`)
  }

  const acceptanceById = new Map(manifest.acceptanceCriteria.map((entry) => [entry.id, entry]))
  const ac045Slices = acceptanceById.get('AC-045')?.verification?.slices ?? []
  const expectedAc045 = new Map([
    ['contract-error-schema', ['M0-E2', 'implemented-local']],
    ['event-projection', ['M3-E4', 'planned']],
    ['responses-error-shape', ['M4-E2', 'planned']],
    ['chat-error-shape', ['M4-E3', 'planned']],
    ['models-error-shape', ['M4-E4', 'planned']],
    ['usage-projection', ['M5-E1', 'planned']],
    ['usage-error-shape', ['M5-E2', 'planned']],
    ['release-acceptance', ['M6-E5', 'planned']],
  ])
  if (!sameValues(ac045Slices.map((slice) => slice.id), [...expectedAc045.keys()])) {
    fail('AC-045 must keep all contract, event, protocol, usage, and release slices.')
  }
  for (const slice of ac045Slices) {
    const expected = expectedAc045.get(slice.id)
    if (expected && (slice.epic !== expected[0] || slice.status !== expected[1])) {
      fail(`AC-045/${slice.id} must remain ${expected[0]} ${expected[1]}.`)
    }
  }
  if (!sameValues(
    ac045Slices.map((slice) => slice.epic),
    acceptanceById.get('AC-045')?.epics ?? [],
  )) {
    fail('AC-045 slice Epics must cover its top-level implementation Epics exactly once.')
  }

  const locallyEvidencedM0Epics = new Set()
  for (const criterion of manifest.acceptanceCriteria) {
    if (['partial', 'implemented-local'].includes(criterion.verification?.status)
        && Array.isArray(criterion.verification?.evidence)) {
      criterion.epics.filter((epic) => epic.startsWith('M0-'))
        .forEach((epic) => locallyEvidencedM0Epics.add(epic))
    }
    for (const slice of criterion.verification?.slices ?? []) {
      if (slice.status === 'implemented-local' && slice.epic.startsWith('M0-')) {
        locallyEvidencedM0Epics.add(slice.epic)
      }
    }
  }
  const requiredM0Epics = ['M0-E1', 'M0-E2', 'M0-E3', 'M0-E4']
  if (!sameValues(locallyEvidencedM0Epics, requiredM0Epics)) {
    fail(`Local traceability evidence must cover exactly M0-E1..M0-E4; found ${sorted(locallyEvidencedM0Epics).join(', ')}.`)
  }
}

const registeredDecisions = executionSpec.split('\n')
  .map((line) => line.match(/^\| (DEC-\d{3}) \|/u)?.[1])
  .filter(Boolean)
const registeredAcceptance = executionSpec.split('\n')
  .map((line) => line.match(/^\| (AC-\d{3}) [^|]+\|/u)?.[1])
  .filter(Boolean)
if (!sameValues(registeredDecisions, expectedDecisionIds) || duplicates(registeredDecisions).length > 0) {
  fail('The authoritative decision registry no longer contains DEC-001..DEC-042 exactly once.')
}
if (!sameValues(registeredAcceptance, expectedAcceptanceIds) || duplicates(registeredAcceptance).length > 0) {
  fail('The authoritative acceptance registry no longer contains AC-001..AC-045 exactly once.')
}

if (githubEvidence && failures.length === 0) {
  for (const evidenceFailure of await validateAdr0006GithubEvidence()) {
    fail(evidenceFailure)
  }
}

if (failures.length > 0) {
  console.error(failures.map((failure) => `- ${failure}`).join('\n'))
  process.exitCode = 1
} else {
  const decisionStatuses = Object.groupBy(
    Object.values(manifest.decisionVerification),
    (verification) => verification.status,
  )
  const acceptanceStatuses = Object.groupBy(
    manifest.acceptanceCriteria,
    (criterion) => criterion.verification.status,
  )
  console.log(
    `${compiledTests
      ? 'Traceability valid with compiled xUnit discovery'
      : githubEvidence
        ? `Traceability and ADR 0006 GitHub evidence valid (${parseAdr0006Evidence(adr0006).status})`
        : 'Traceability structure valid; compiled xUnit discovery pending'}: `
      + `${manifest.decisions.length} decisions `
      + `(${decisionStatuses['implemented-local']?.length ?? 0} implemented-local, `
      + `${decisionStatuses.partial?.length ?? 0} partial, ${decisionStatuses.planned?.length ?? 0} planned); `
      + `${manifest.acceptanceCriteria.length} acceptance criteria `
      + `(${acceptanceStatuses['implemented-local']?.length ?? 0} implemented-local, `
      + `${acceptanceStatuses.partial?.length ?? 0} partial, ${acceptanceStatuses.planned?.length ?? 0} planned).`,
  )
}
