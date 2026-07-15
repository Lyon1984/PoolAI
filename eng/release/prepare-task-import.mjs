import { randomUUID } from 'node:crypto'
import {
  closeSync,
  constants,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  realpathSync,
  renameSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs'
import { resolve } from 'node:path'
import {
  auditableReferencePolicyCases,
  canonicalAuditableTaskId,
  isAuditableHttpsReference,
  isAuditableTaskId,
  isNamedOwner,
  namedOwnerPolicyCases,
  taskIdPolicyCases,
} from '../policies/auditable-reference.mjs'

const root = resolve(import.meta.dirname, '..', '..')
const executionSpecPath = 'docs/开发执行规格-v1.0.md'
const systemPlanPath = 'docs/系统重构方案-v1.0.md'
const traceabilityPath = 'docs/traceability/release-1-traceability.json'
const epicRegistryPath = 'docs/traceability/delivery-epics.json'
const executionSpec = readFileSync(resolve(root, executionSpecPath), 'utf8')
const systemPlan = readFileSync(resolve(root, systemPlanPath), 'utf8')
const traceability = JSON.parse(readFileSync(resolve(root, traceabilityPath), 'utf8'))
const epicRegistry = JSON.parse(readFileSync(resolve(root, epicRegistryPath), 'utf8'))
const failures = []
const expectedEpicCounts = new Map([
  ['M0', 4],
  ['M1', 5],
  ['M2', 4],
  ['M3', 5],
  ['M4', 5],
  ['M5', 6],
  ['M6', 5],
  ['M7', 4],
])
const expectedEpicIds = [...expectedEpicCounts.entries()]
  .flatMap(([milestone, count]) => Array.from(
    { length: count },
    (_, index) => `${milestone}-E${index + 1}`,
  ))
const intentionallyWithoutDirectR1DecisionOrAcceptance = new Set([
  'M3-E5',
  'M5-E6',
  'M7-E1',
  'M7-E2',
  'M7-E3',
  'M7-E4',
])

const fail = (message) => failures.push(message)
const sorted = (values) => [...values].sort()
const sameValues = (left, right) => JSON.stringify(sorted(left)) === JSON.stringify(sorted(right))
const duplicates = (values) => [...new Set(values.filter((value, index) => values.indexOf(value) !== index))]
const uniqueObjects = (values) => [...new Map(values.map((value) => [JSON.stringify(value), value])).values()]

const taskSystemEvidenceFailure = (evidence) => {
  if (!['pending', 'verified'].includes(evidence?.status)) {
    return 'Canonical task-system evidence must be pending or verified.'
  }
  if (evidence.status === 'pending' && evidence.evidence !== null) {
    return 'Canonical task-system evidence must be null while pending.'
  }
  if (evidence.status === 'verified' && !isAuditableHttpsReference(evidence.evidence)) {
    return 'Canonical task-system evidence must be an auditable non-placeholder HTTPS reference when verified.'
  }
  return null
}

const parseEpicBacklog = () => {
  const start = executionSpec.indexOf('## 11. M0–M7 可拆分 Backlog')
  const end = executionSpec.indexOf('## 12. 全局 Definition of Done', start)
  if (start < 0 || end < 0) {
    fail('Cannot locate the authoritative M0-M7 backlog in the execution specification.')
    return []
  }

  const epics = []
  let currentMilestone = null
  for (const line of executionSpec.slice(start, end).split('\n')) {
    const heading = line.match(/^### (M[0-7])：(.+)$/u)
    if (heading) {
      currentMilestone = heading[1]
      continue
    }

    const row = line.match(/^\| (M[0-7]-E\d+) ([^|]+) \| ([^|]+) \| ([^|]+) \| ([^|]+) \|$/u)
    if (!row) {
      continue
    }
    const [, id, title, dependencies, deliverables, definitionOfDone] = row
    if (currentMilestone !== id.slice(0, 2)) {
      fail(`${id} is outside its milestone heading in the execution specification.`)
    }
    epics.push({
      id,
      milestone: id.slice(0, 2),
      title: title.trim(),
      dependencies: dependencies.trim(),
      deliverables: deliverables.trim(),
      definitionOfDone: definitionOfDone.trim(),
    })
  }
  return epics
}

const parsePrimaryWorkstreamMap = () => {
  const start = systemPlan.indexOf('任务系统导入使用下面的唯一 primary WS')
  const end = systemPlan.indexOf('若 Story 无法填写上述任一列', start)
  if (start < 0 || end < 0) {
    fail('Cannot locate the task-import primary workstream table in the system plan.')
    return new Map()
  }

  const mapping = new Map()
  for (const line of systemPlan.slice(start, end).split('\n')) {
    const row = line.match(/^\| M([0-7])-E(\d+)(?:[–-]E(\d+))? \| (WS-0[1-8]) \| ([^|]+) \|$/u)
    if (!row) {
      continue
    }
    const [, milestoneNumber, startNumberText, endNumberText, primaryWorkstream, supportingCell] = row
    const startNumber = Number(startNumberText)
    const endNumber = Number(endNumberText ?? startNumberText)
    const supportingWorkstreams = supportingCell.trim() === '—'
      ? []
      : [...supportingCell.matchAll(/WS-0[1-8]/gu)].map((match) => match[0])
    if (duplicates(supportingWorkstreams).length > 0 || supportingWorkstreams.includes(primaryWorkstream)) {
      fail(`Invalid supporting workstreams for M${milestoneNumber}-E${startNumberText}.`)
    }
    for (let epicNumber = startNumber; epicNumber <= endNumber; epicNumber += 1) {
      const id = `M${milestoneNumber}-E${epicNumber}`
      if (mapping.has(id)) {
        fail(`Primary workstream table maps ${id} more than once.`)
      }
      mapping.set(id, { primaryWorkstream, supportingWorkstreams })
    }
  }
  return mapping
}

const collectVerification = (sourceId, verification, epicId, parentEpics) => {
  const relatedPlannedTests = []
  const relatedLocalEvidence = []
  if (parentEpics.includes(epicId)) {
    if (verification?.plannedTest) {
      relatedPlannedTests.push({
        sourceId,
        testSuite: verification.testSuite,
        plannedTest: verification.plannedTest,
      })
    }
    relatedLocalEvidence.push(...(verification?.evidence ?? []).map((evidence) => ({ sourceId, ...evidence })))
  }
  for (const slice of verification?.slices ?? []) {
    if (slice.epic !== epicId) {
      continue
    }
    if (slice.plannedTest) {
      relatedPlannedTests.push({
        sourceId,
        testSuite: slice.testSuite,
        plannedTest: slice.plannedTest,
      })
    }
    relatedLocalEvidence.push(...(slice.evidence ?? []).map((evidence) => ({ sourceId, ...evidence })))
  }
  return {
    relatedPlannedTests: uniqueObjects(relatedPlannedTests),
    relatedLocalEvidence: uniqueObjects(relatedLocalEvidence),
  }
}

const csvCell = (value) => `"${String(value ?? '').replaceAll('"', '""')}"`

const renderCsv = (epics) => {
  const headers = [
    'epic_id',
    'title',
    'release',
    'milestone',
    'primary_workstream',
    'supporting_workstreams',
    'dependencies',
    'deliverables',
    'definition_of_done',
    'decision_ids',
    'acceptance_ids',
    'contracts',
    'related_planned_tests',
    'external_task_id',
    'owner',
  ]
  const rows = epics.map((epic) => [
    epic.id,
    epic.title,
    epic.release,
    epic.milestone,
    epic.primaryWorkstream,
    epic.supportingWorkstreams.join(';'),
    epic.dependencies,
    epic.deliverables,
    epic.definitionOfDone,
    epic.decisionIds.join(';'),
    epic.acceptanceIds.join(';'),
    epic.contracts.join(';'),
    epic.relatedPlannedTests
      .map((test) => `${test.sourceId}:${test.testSuite}.${test.plannedTest}`)
      .join(';'),
    epic.externalTaskId,
    epic.owner,
  ])
  return [headers, ...rows].map((row) => row.map(csvCell).join(',')).join('\r\n').concat('\r\n')
}

const backlog = parseEpicBacklog()
const primaryWorkstreamMap = parsePrimaryWorkstreamMap()
const registryEntries = epicRegistry.epics ?? []
const registryById = new Map(registryEntries.map((entry) => [entry.id, entry]))

if (epicRegistry.schemaVersion !== 1 || epicRegistry.scope !== 'M0-M7') {
  fail('Delivery Epic registry must use schemaVersion 1 and scope M0-M7.')
}
if (epicRegistry.registry !== executionSpecPath || epicRegistry.workstreamRegistry !== systemPlanPath) {
  fail('Delivery Epic registry must point to the authoritative execution specification and system plan.')
}
if (epicRegistry.externalEvidenceSource
    !== `${traceabilityPath}#/externalEvidence/taskSystem`) {
  fail('Delivery Epic registry must reference the canonical task-system external evidence gate.')
}

const backlogIds = backlog.map((epic) => epic.id)
const registryIds = registryEntries.map((entry) => entry.id)
if (!sameValues(backlogIds, expectedEpicIds) || duplicates(backlogIds).length > 0) {
  fail('Execution specification must contain M0-E1..M7-E4 with the frozen per-milestone counts.')
}
if (!sameValues([...primaryWorkstreamMap.keys()], expectedEpicIds)) {
  fail('System-plan primary workstream table must cover all 38 M0-M7 Epics exactly once.')
}
if (!sameValues(registryIds, expectedEpicIds) || duplicates(registryIds).length > 0) {
  fail('Delivery Epic registry must contain all 38 M0-M7 Epics exactly once.')
}

const taskSystemEvidence = traceability.externalEvidence?.taskSystem
const taskSystemStatus = taskSystemEvidence?.status
const taskSystemFailure = taskSystemEvidenceFailure(taskSystemEvidence)
if (taskSystemFailure) {
  fail(taskSystemFailure)
}
for (const invalidEvidence of [
  { status: 'pending', evidence: auditableReferencePolicyCases.valid },
  { status: 'verified', evidence: null },
  { status: 'verified', evidence: 'https://tasks.example.com./task-1234' },
]) {
  if (!taskSystemEvidenceFailure(invalidEvidence)) {
    fail(`Task-import validator self-test accepted invalid canonical task-system evidence: ${JSON.stringify(invalidEvidence)}`)
  }
}

for (const placeholderReference of auditableReferencePolicyCases.invalid) {
  if (isAuditableTaskId(placeholderReference)) {
    fail(`Task-import validator self-test accepted a placeholder task reference: ${placeholderReference}`)
  }
}
if (!isAuditableHttpsReference(auditableReferencePolicyCases.valid)) {
  fail('Task-import validator self-test rejected an auditable HTTPS task reference shape.')
}
for (const placeholderOwner of namedOwnerPolicyCases.invalid) {
  if (isNamedOwner(placeholderOwner)) {
    fail(`Task-import validator self-test accepted a placeholder owner: ${placeholderOwner}`)
  }
}
if (!isNamedOwner(namedOwnerPolicyCases.valid)) {
  fail('Task-import validator self-test rejected a named owner shape.')
}
for (const placeholderTaskId of taskIdPolicyCases.invalid) {
  if (isAuditableTaskId(placeholderTaskId)) {
    fail(`Task-import validator self-test accepted a placeholder task ID: ${placeholderTaskId}`)
  }
}
if (!isAuditableTaskId(taskIdPolicyCases.valid)) {
  fail('Task-import validator self-test rejected an auditable task ID shape.')
}
if (!taskIdPolicyCases.equivalentUrls.every(isAuditableTaskId)
    || new Set(taskIdPolicyCases.equivalentUrls.map(canonicalAuditableTaskId)).size !== 1) {
  fail('Task-import validator self-test failed canonical task URL equivalence.')
}

for (const entry of registryEntries) {
  const expectedWorkstreams = primaryWorkstreamMap.get(entry.id)
  if (entry.primaryWorkstream !== expectedWorkstreams?.primaryWorkstream
      || !sameValues(entry.supportingWorkstreams ?? [], expectedWorkstreams?.supportingWorkstreams ?? [])) {
    fail(`${entry.id} workstream ownership differs from the authoritative system-plan table.`)
  }
  if (taskSystemStatus === 'pending' && (entry.externalTaskId !== null || entry.owner !== null)) {
    fail(`${entry.id} cannot invent an external task ID or owner while task-system evidence is pending.`)
  }
  if (taskSystemStatus === 'verified'
      && (!isAuditableTaskId(entry.externalTaskId) || !isNamedOwner(entry.owner))) {
    fail(`${entry.id} requires an auditable external task ID and named owner after import is verified.`)
  }
}
if (taskSystemStatus === 'verified'
    && duplicates(registryEntries.map((entry) => canonicalAuditableTaskId(entry.externalTaskId))).length > 0) {
  fail('Verified Epic task IDs must be unique.')
}

const mergedEpics = backlog.map((epic) => {
  const registry = registryById.get(epic.id)
  const decisions = traceability.decisions.filter((decision) => decision.epics.includes(epic.id))
  const acceptanceCriteria = traceability.acceptanceCriteria
    .filter((criterion) => criterion.epics.includes(epic.id))
  const verification = [
    ...decisions.map((decision) => collectVerification(
      decision.id,
      traceability.decisionVerification[decision.id],
      epic.id,
      decision.epics,
    )),
    ...acceptanceCriteria.map((criterion) => collectVerification(
      criterion.id,
      criterion.verification,
      epic.id,
      criterion.epics,
    )),
  ]
  return {
    ...epic,
    release: epic.milestone === 'M7' ? 'R1.2' : 'R1.1',
    primaryWorkstream: registry?.primaryWorkstream,
    supportingWorkstreams: registry?.supportingWorkstreams ?? [],
    decisionIds: decisions.map((decision) => decision.id).sort(),
    acceptanceIds: acceptanceCriteria.map((criterion) => criterion.id).sort(),
    contracts: sorted(new Set([
      executionSpecPath,
      systemPlanPath,
      ...decisions.flatMap((decision) => decision.contracts),
      ...acceptanceCriteria.flatMap((criterion) => criterion.contracts),
    ])),
    relatedPlannedTests: uniqueObjects(verification.flatMap((item) => item.relatedPlannedTests)),
    relatedLocalEvidence: uniqueObjects(verification.flatMap((item) => item.relatedLocalEvidence)),
    externalTaskId: registry?.externalTaskId ?? null,
    owner: registry?.owner ?? null,
  }
})

const withoutDirectMapping = mergedEpics
  .filter((epic) => epic.decisionIds.length === 0 && epic.acceptanceIds.length === 0)
  .map((epic) => epic.id)
if (!sameValues(withoutDirectMapping, intentionallyWithoutDirectR1DecisionOrAcceptance)) {
  fail(`Unexpected Epics without direct DEC/AC mapping: ${withoutDirectMapping.join(', ')}.`)
}

const args = process.argv.slice(2)
const checkOnly = args.length === 1 && args[0] === '--check'
const generatePreview = args.length === 2 && args[0] === '--output-dir'
if (!checkOnly && !generatePreview) {
  fail('Use exactly --check or --output-dir <repository-relative artifacts path>.')
}

if (failures.length > 0) {
  process.stderr.write(`${failures.map((failure) => `- ${failure}`).join('\n')}\n`)
  process.exitCode = 1
} else if (checkOnly) {
  process.stdout.write(
    `Task import registry valid: ${mergedEpics.length} Epics; `
      + `${mergedEpics.length - withoutDirectMapping.length} with direct R1.1 DEC/AC mapping; `
      + `${withoutDirectMapping.length} intentionally without direct R1.1 mapping; `
      + `external task system ${taskSystemStatus}.\n`,
  )
} else {
  const outputArgument = args[1]
  const outputSegments = outputArgument.split('/')
  if (outputSegments[0] !== 'artifacts'
      || outputSegments.slice(1).some((segment) => segment === '' || segment === '.' || segment === '..')) {
    throw new Error('Task import output must be a repository-relative path under artifacts/.')
  }
  const artifactsDirectory = resolve(root, 'artifacts')
  mkdirSync(artifactsDirectory, { recursive: true })
  if (!lstatSync(artifactsDirectory).isDirectory()) {
    throw new Error('Task import artifacts root must be a real directory, not a symbolic link.')
  }
  const realArtifactsDirectory = realpathSync(artifactsDirectory)
  let outputDirectory = artifactsDirectory
  let realOutputDirectory = realArtifactsDirectory
  for (const segment of outputSegments.slice(1)) {
    outputDirectory = resolve(outputDirectory, segment)
    try {
      const existing = lstatSync(outputDirectory)
      if (!existing.isDirectory()) {
        throw new Error(`Task import output path component must be a real directory: ${outputDirectory}`)
      }
    } catch (error) {
      if (error?.code !== 'ENOENT') {
        throw error
      }
      mkdirSync(outputDirectory)
    }
    const expectedRealDirectory = resolve(realOutputDirectory, segment)
    realOutputDirectory = realpathSync(outputDirectory)
    if (realOutputDirectory !== expectedRealDirectory) {
      throw new Error(`Task import output path component resolves outside artifacts/: ${outputDirectory}`)
    }
  }
  const suffix = taskSystemStatus === 'pending' ? 'preview' : 'verified'
  const payload = {
    schemaVersion: 1,
    mode: suffix,
    taskSystemStatus,
    sources: [executionSpecPath, systemPlanPath, traceabilityPath, epicRegistryPath],
    epicCount: mergedEpics.length,
    intentionallyWithoutDirectR1DecisionOrAcceptance: sorted(intentionallyWithoutDirectR1DecisionOrAcceptance),
    epics: mergedEpics,
  }
  const jsonPath = resolve(realOutputDirectory, `poolai-m0-m7-epics.${suffix}.json`)
  const csvPath = resolve(realOutputDirectory, `poolai-m0-m7-epics.${suffix}.csv`)
  const outputFiles = [
    [jsonPath, `${JSON.stringify(payload, null, 2)}\n`],
    [csvPath, renderCsv(mergedEpics)],
  ]
  for (const [outputPath] of outputFiles) {
    try {
      if (!lstatSync(outputPath).isFile()) {
        throw new Error(`Task import output target must be absent or a regular file: ${outputPath}`)
      }
    } catch (error) {
      if (error?.code !== 'ENOENT') {
        throw error
      }
    }
  }
  for (const [outputPath, content] of outputFiles) {
    const temporaryPath = `${outputPath}.${process.pid}.${randomUUID()}.tmp`
    let descriptor
    try {
      descriptor = openSync(
        temporaryPath,
        constants.O_CREAT | constants.O_EXCL | constants.O_WRONLY | constants.O_NOFOLLOW,
        0o600,
      )
      writeFileSync(descriptor, content, 'utf8')
      closeSync(descriptor)
      descriptor = undefined
      renameSync(temporaryPath, outputPath)
    } catch (error) {
      if (descriptor !== undefined) {
        closeSync(descriptor)
      }
      try {
        unlinkSync(temporaryPath)
      } catch (cleanupError) {
        if (cleanupError?.code !== 'ENOENT') {
          error.cause = cleanupError
        }
      }
      throw error
    }
  }
  process.stdout.write(`Task import ${suffix} generated: ${jsonPath}\nTask import ${suffix} generated: ${csvPath}\n`)
}
