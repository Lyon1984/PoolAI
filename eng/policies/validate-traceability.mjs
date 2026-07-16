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
const qualityGate = readFileSync(resolve(root, 'eng/test/quality-gate.sh'), 'utf8')
const frontendPackage = JSON.parse(readFileSync(resolve(root, 'frontend/package.json'), 'utf8'))
const xunitRunnerConfig = JSON.parse(readFileSync(resolve(root, 'tests/xunit.runner.json'), 'utf8'))
const failures = []
const validationModes = process.argv.slice(2)
const structureOnly = validationModes.length === 1 && validationModes[0] === '--structure-only'
const compiledTests = validationModes.length === 1 && validationModes[0] === '--compiled-tests'
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

if (!structureOnly && !compiledTests) {
  fail('Choose exactly one traceability mode: --structure-only or --compiled-tests.')
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
    `${compiledTests ? 'Traceability valid with compiled xUnit discovery' : 'Traceability structure valid; compiled xUnit discovery pending'}: `
      + `${manifest.decisions.length} decisions `
      + `(${decisionStatuses['implemented-local']?.length ?? 0} implemented-local, `
      + `${decisionStatuses.partial?.length ?? 0} partial, ${decisionStatuses.planned?.length ?? 0} planned); `
      + `${manifest.acceptanceCriteria.length} acceptance criteria `
      + `(${acceptanceStatuses['implemented-local']?.length ?? 0} implemented-local, `
      + `${acceptanceStatuses.partial?.length ?? 0} partial, ${acceptanceStatuses.planned?.length ?? 0} planned).`,
  )
}
