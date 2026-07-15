import { execFileSync } from 'node:child_process'
import { readdir, readFile, writeFile } from 'node:fs/promises'
import { basename, dirname, relative, resolve, sep } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const reportDirectory = resolve(root, process.argv[2] ?? 'artifacts/coverage/dotnet')
const measureOnly = process.argv.includes('--measure-only')
const withoutFrontend = process.argv.includes('--without-frontend')
const requireDiff = process.env.COVERAGE_REQUIRE_DIFF?.trim().toLowerCase() === 'true'
const frontendLcovPath = resolve(root, 'frontend', 'coverage', 'lcov.info')
const criticalBaselinePath = resolve(root, 'eng', 'test', 'critical-coverage-baseline.json')
const criticalBaseline = JSON.parse(await readFile(criticalBaselinePath, 'utf8'))
const requiredCriticalAreas = Object.freeze(['auth', 'quota', 'secret'])
const explicitlyExcludedSources = new Set([
  'frontend/src/api/generated/error-codes-v1.ts',
  'frontend/src/api/generated/openapi-v1.ts',
  'frontend/src/env.d.ts',
  'src/PoolAI.Contracts/Generated/ErrorCodesV1.g.cs',
  'src/PoolAI.Contracts/Generated/OpenApiV1.g.cs',
])
const thresholds = Object.freeze({
  overallLine: 75,
  frontendLine: 75,
  domainApplicationLine: 85,
  domainApplicationBranch: 75,
  modifiedLine: 90,
})

const repositoryPath = (path) => relative(root, path).split(sep).join('/')

const findFiles = async (directory, predicate) => {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = []

  for (const entry of entries) {
    const path = resolve(directory, entry.name)
    if (entry.isDirectory()) {
      files.push(...await findFiles(path, predicate))
    } else if (entry.isFile() && predicate(entry.name)) {
      files.push(path)
    }
  }

  return files
}

const discoverProductionProjects = async () => Promise.all(
  (await findFiles(resolve(root, 'src'), (name) => name.endsWith('.csproj')))
    .sort()
    .map(async (projectPath) => {
      const project = await readFile(projectPath, 'utf8')
      const explicitAssemblyName = project.match(/<AssemblyName>\s*([^<]+?)\s*<\/AssemblyName>/u)?.[1]
      return {
        assembly: `${explicitAssemblyName ?? basename(projectPath, '.csproj')}.dll`,
        directory: repositoryPath(dirname(projectPath)),
        project: repositoryPath(projectPath),
      }
    }),
)

const normalizeSourcePath = (filename) => {
  const normalized = filename.replaceAll('\\', '/').replace(/^\.\//, '')
  const normalizedRoot = root.replaceAll('\\', '/')
  const repositoryRelative = normalized.startsWith(`${normalizedRoot}/`)
    ? normalized.slice(normalizedRoot.length + 1)
    : normalized

  if (repositoryRelative.startsWith('src/')) {
    return repositoryRelative
  }

  const sourceIndex = repositoryRelative.lastIndexOf('/src/')
  if (sourceIndex >= 0) {
    return repositoryRelative.slice(sourceIndex + 1)
  }

  return `src/${repositoryRelative}`
}

const mergeReport = (lines, branches, documents, assemblies, report) => {
  for (const [module, moduleDocuments] of Object.entries(report)) {
    assemblies.add(module)
    for (const [document, classes] of Object.entries(moduleDocuments)) {
      const path = normalizeSourcePath(document)
      documents.add(path)
      for (const [className, methods] of Object.entries(classes)) {
        for (const [methodName, method] of Object.entries(methods)) {
          for (const [numberText, hits] of Object.entries(method.Lines ?? {})) {
            const number = Number(numberText)
            const key = `${path}:${number}`
            const previous = lines.get(key) ?? { path, line: number, hits: 0 }
            previous.hits = Math.max(previous.hits, Number(hits))
            lines.set(key, previous)
          }

          for (const branch of method.Branches ?? []) {
            const identity = [
              module,
              path,
              className,
              methodName,
              branch.Line,
              branch.Offset,
              branch.EndOffset,
              branch.Path,
              branch.Ordinal,
            ].join(':')
            const previous = branches.get(identity) ?? { path, hits: 0 }
            previous.hits = Math.max(previous.hits, Number(branch.Hits))
            branches.set(identity, previous)
          }
        }
      }
    }
  }
}

const mergeLcov = (lines, documents, lcov) => {
  let path

  for (const entry of lcov.split('\n')) {
    if (entry.startsWith('SF:')) {
      const filename = entry.slice(3).replaceAll('\\', '/')
      const normalizedRoot = root.replaceAll('\\', '/')
      path = filename.startsWith(`${normalizedRoot}/`)
        ? filename.slice(normalizedRoot.length + 1)
        : filename.startsWith('frontend/')
          ? filename
          : `frontend/${filename.replace(/^\.\//, '')}`
      documents.add(path)
      continue
    }

    if (entry === 'end_of_record') {
      path = undefined
      continue
    }

    const data = entry.match(/^DA:(\d+),(\d+)/u)
    if (path === undefined || data === null) {
      continue
    }

    const number = Number(data[1])
    const hits = Number(data[2])
    const key = `${path}:${number}`
    const previous = lines.get(key) ?? { path, line: number, hits: 0 }
    previous.hits = Math.max(previous.hits, hits)
    lines.set(key, previous)
  }
}

const isDomainOrApplication = (path) => {
  if (path.startsWith('src/PoolAI.Application.Orchestration/')) {
    return !path.endsWith('/DependencyInjection.cs')
  }

  return path.startsWith('src/Modules/')
    && (path.includes('/Domain/') || path.includes('/Application/'))
}

const matchesCriticalArea = (path, policy) => policy.pathIncludes.some((value) => path.includes(value))
  || policy.pathEndsWith.some((value) => path.endsWith(value))

const calculate = (lines, branches = []) => {
  const values = [...lines]
  const branchValues = [...branches]
  const linesValid = values.length
  const linesCovered = values.filter((line) => line.hits > 0).length
  const branchesValid = branchValues.length
  const branchesCovered = branchValues.filter((branch) => branch.hits > 0).length

  return {
    linesCovered,
    linesValid,
    lineRate: linesValid === 0 ? null : (linesCovered / linesValid) * 100,
    branchesCovered,
    branchesValid,
    branchRate: branchesValid === 0 ? null : (branchesCovered / branchesValid) * 100,
  }
}

const validateCriticalBaseline = (baseline, label) => {
  if (baseline.schemaVersion !== 2) {
    throw new Error(`${label} must use critical coverage baseline schemaVersion 2.`)
  }

  const areaNames = Object.keys(baseline.areas ?? {}).sort()
  if (JSON.stringify(areaNames) !== JSON.stringify(requiredCriticalAreas)) {
    throw new Error(`${label} must define exactly these critical areas: ${requiredCriticalAreas.join(', ')}.`)
  }

  for (const [name, area] of Object.entries(baseline.areas)) {
    if (!Array.isArray(area.pathIncludes) || !Array.isArray(area.pathEndsWith)
        || area.pathIncludes.length + area.pathEndsWith.length === 0) {
      throw new Error(`${label} critical area ${name} must define a non-empty path policy.`)
    }

    for (const field of ['linesCovered', 'linesValid', 'branchesCovered', 'branchesValid']) {
      if (!Number.isInteger(area[field]) || area[field] < 0) {
        throw new Error(`${label} critical area ${name}.${field} must be a non-negative integer.`)
      }
    }

    if (area.linesValid === 0 || area.linesCovered > area.linesValid) {
      throw new Error(`${label} critical area ${name} must have a non-zero valid line denominator.`)
    }
    if (area.branchesCovered > area.branchesValid) {
      throw new Error(`${label} critical area ${name} has invalid branch counts.`)
    }

    const expectedLineRate = (area.linesCovered / area.linesValid) * 100
    if (typeof area.lineRate !== 'number' || Math.abs(area.lineRate - expectedLineRate) > 1e-10) {
      throw new Error(`${label} critical area ${name} has a line rate inconsistent with its counts.`)
    }

    const expectedBranchRate = area.branchesValid === 0
      ? null
      : (area.branchesCovered / area.branchesValid) * 100
    const branchRateMatches = expectedBranchRate === null
      ? area.branchRate === null
      : typeof area.branchRate === 'number' && Math.abs(area.branchRate - expectedBranchRate) <= 1e-10
    if (!branchRateMatches) {
      throw new Error(`${label} critical area ${name} has a branch rate inconsistent with its counts.`)
    }
  }
}

const loadBaseCriticalBaseline = (base) => {
  if (base === null) {
    return { baseline: null, reason: 'no Git comparison base is available' }
  }

  const object = `${base}:eng/test/critical-coverage-baseline.json`
  try {
    execFileSync('git', ['cat-file', '-e', object], { cwd: root, stdio: 'ignore' })
  } catch {
    return { baseline: null, reason: 'the comparison base predates the ratcheted critical baseline' }
  }

  const baseline = JSON.parse(execFileSync('git', ['show', object], { cwd: root, encoding: 'utf8' }))
  if (baseline.schemaVersion !== 2) {
    return { baseline: null, reason: 'the comparison base uses the legacy critical baseline schema' }
  }

  validateCriticalBaseline(baseline, 'Git base')
  return { baseline, reason: null }
}

const frontendExecutableExtension = /\.(?:js|jsx|ts|tsx|vue)$/u
const frontendTestOrDeclaration = /\.(?:test|spec)\.(?:js|jsx|ts|tsx|vue)$/u
const isProductionSource = (path) => (path.startsWith('src/') && path.endsWith('.cs'))
  || (path.startsWith('frontend/src/')
    && frontendExecutableExtension.test(path)
    && !frontendTestOrDeclaration.test(path)
    && !path.endsWith('.d.ts'))

const changedCoverageSelection = (lines, documents, projects, observedAssemblies) => {
  const requestedBase = process.env.COVERAGE_DIFF_BASE?.trim()
  if (!requestedBase || /^0+$/u.test(requestedBase)) {
    return {
      base: null,
      files: [],
      lines: [],
      inventoryMissing: [],
      reason: 'COVERAGE_DIFF_BASE is not set to a usable commit',
    }
  }

  const diffArguments = [
    '--diff-filter=ACMR',
    `${requestedBase}...HEAD`,
    '--',
    'src',
    'frontend/src',
  ]
  const files = execFileSync('git', ['diff', '--name-only', ...diffArguments], {
    cwd: root,
    encoding: 'utf8',
  }).split('\n').map((path) => path.trim()).filter((path) => path && isProductionSource(path))
  const diff = execFileSync('git', ['diff', '--unified=0', ...diffArguments], {
    cwd: root,
    encoding: 'utf8',
  })
  let path
  const changed = new Set()

  for (const line of diff.split('\n')) {
    if (line.startsWith('+++ b/')) {
      path = line.slice(6)
      continue
    }

    const hunk = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/u)
    if (path === undefined || hunk === null) {
      continue
    }

    const start = Number(hunk[1])
    const count = Number(hunk[2] ?? 1)
    for (let offset = 0; offset < count; offset += 1) {
      const key = `${path}:${start + offset}`
      if (lines.has(key)) {
        changed.add(key)
      }
    }
  }

  const inventoryMissing = files.filter((file) => {
    if (explicitlyExcludedSources.has(file) || documents.has(file)) {
      return false
    }

    if (file.startsWith('frontend/src/')) {
      return true
    }

    const owner = projects
      .filter((project) => file.startsWith(`${project.directory}/`))
      .sort((left, right) => right.directory.length - left.directory.length)[0]
    return owner === undefined || !observedAssemblies.has(owner.assembly)
  })

  return {
    base: requestedBase,
    files,
    lines: [...changed].map((key) => lines.get(key)),
    inventoryMissing,
    reason: files.length === 0
      ? 'the diff contains no production source files'
      : changed.size === 0
        ? 'the source diff contains no coverable lines'
        : null,
  }
}

validateCriticalBaseline(criticalBaseline, 'Current branch')

const reports = (await findFiles(reportDirectory, (name) => name === 'coverage.json')).sort()
if (reports.length === 0) {
  throw new Error(`No coverage.json reports found under ${reportDirectory}`)
}

const productionProjects = await discoverProductionProjects()
const expectedBackendAssemblies = [...new Set(productionProjects.map((project) => project.assembly))].sort()
const backendLines = new Map()
const backendBranches = new Map()
const coverageDocuments = new Set()
const observedBackendAssemblies = new Set()
for (const report of reports) {
  mergeReport(
    backendLines,
    backendBranches,
    coverageDocuments,
    observedBackendAssemblies,
    JSON.parse(await readFile(report, 'utf8')),
  )
}

const frontendLines = new Map()
if (!withoutFrontend) {
  mergeLcov(frontendLines, coverageDocuments, await readFile(frontendLcovPath, 'utf8'))
}

const allLines = new Map([...backendLines, ...frontendLines])
const overall = calculate(backendLines.values(), backendBranches.values())
const frontend = calculate(frontendLines.values())
const domainApplication = calculate(
  [...backendLines.values()].filter((line) => isDomainOrApplication(line.path)),
  [...backendBranches.values()].filter((branch) => isDomainOrApplication(branch.path)),
)
const criticalAreas = Object.fromEntries(Object.entries(criticalBaseline.areas).map(([name, policy]) => [
  name,
  calculate(
    [...backendLines.values()].filter((line) => matchesCriticalArea(line.path, policy)),
    [...backendBranches.values()].filter((branch) => matchesCriticalArea(branch.path, policy)),
  ),
]))
const missingBackendAssemblies = expectedBackendAssemblies
  .filter((assembly) => !observedBackendAssemblies.has(assembly))
const modifiedSelection = changedCoverageSelection(
  allLines,
  coverageDocuments,
  productionProjects,
  observedBackendAssemblies,
)
const modified = modifiedSelection.base === null || modifiedSelection.lines.length === 0
  ? null
  : calculate(modifiedSelection.lines)
const baseCriticalBaseline = loadBaseCriticalBaseline(modifiedSelection.base)

const summary = {
  reports: reports.map(repositoryPath),
  thresholds,
  backendAssemblies: {
    expected: expectedBackendAssemblies,
    observed: [...observedBackendAssemblies].sort(),
    missing: missingBackendAssemblies,
  },
  overall,
  frontend: withoutFrontend ? {
    evaluated: false,
    reason: '--without-frontend was supplied',
  } : {
    evaluated: true,
    ...frontend,
  },
  domainApplication,
  criticalAreas,
  criticalBaseline,
  baseCriticalBaseline: baseCriticalBaseline.baseline ?? {
    evaluated: false,
    reason: baseCriticalBaseline.reason,
  },
  modified: modified === null ? {
    base: modifiedSelection.base,
    evaluated: false,
    files: modifiedSelection.files,
    inventoryMissing: modifiedSelection.inventoryMissing,
    reason: modifiedSelection.reason,
  } : {
    base: modifiedSelection.base,
    evaluated: true,
    files: modifiedSelection.files,
    inventoryMissing: modifiedSelection.inventoryMissing,
    ...modified,
  },
}

await writeFile(resolve(reportDirectory, 'coverage-summary.json'), `${JSON.stringify(summary, null, 2)}\n`)

const percentage = (value) => value === null ? 'N/A' : `${value.toFixed(2)}%`
console.log(`Coverage reports merged: ${reports.length}`)
console.log(`Production assemblies observed: ${observedBackendAssemblies.size}/${expectedBackendAssemblies.length}`)
console.log(`Overall line: ${percentage(overall.lineRate)} (${overall.linesCovered}/${overall.linesValid})`)
if (!withoutFrontend) {
  console.log(`Frontend line: ${percentage(frontend.lineRate)} (${frontend.linesCovered}/${frontend.linesValid})`)
}
console.log(`Domain/Application line: ${percentage(domainApplication.lineRate)} (${domainApplication.linesCovered}/${domainApplication.linesValid})`)
console.log(`Domain/Application branch: ${percentage(domainApplication.branchRate)} (${domainApplication.branchesCovered}/${domainApplication.branchesValid})`)
for (const [name, coverage] of Object.entries(criticalAreas)) {
  console.log(`Critical ${name}: line ${percentage(coverage.lineRate)}, branch ${percentage(coverage.branchRate)}`)
}
if (modified === null) {
  console.log(`Modified line: not evaluated (${summary.modified.reason})`)
} else {
  console.log(`Modified line: ${percentage(modified.lineRate)} (${modified.linesCovered}/${modified.linesValid}) against ${modifiedSelection.base}`)
}

const failures = []
if (missingBackendAssemblies.length > 0) {
  failures.push(`coverage inventory is missing production assemblies: ${missingBackendAssemblies.join(', ')}`)
}
if (overall.linesValid === 0) {
  failures.push('overall line coverage has a zero denominator')
} else if (overall.lineRate < thresholds.overallLine) {
  failures.push(`overall line coverage ${percentage(overall.lineRate)} is below ${thresholds.overallLine}%`)
}
if (!withoutFrontend) {
  if (frontend.linesValid === 0) {
    failures.push('frontend line coverage has a zero denominator')
  } else if (frontend.lineRate < thresholds.frontendLine) {
    failures.push(`frontend line coverage ${percentage(frontend.lineRate)} is below ${thresholds.frontendLine}%`)
  }
}
if (domainApplication.linesValid === 0) {
  failures.push('Domain/Application line coverage has a zero denominator')
} else if (domainApplication.lineRate < thresholds.domainApplicationLine) {
  failures.push(`Domain/Application line coverage ${percentage(domainApplication.lineRate)} is below ${thresholds.domainApplicationLine}%`)
}
if (domainApplication.branchesValid === 0) {
  failures.push('Domain/Application branch coverage has a zero denominator')
} else if (domainApplication.branchRate < thresholds.domainApplicationBranch) {
  failures.push(`Domain/Application branch coverage ${percentage(domainApplication.branchRate)} is below ${thresholds.domainApplicationBranch}%`)
}

const metricFields = [
  'linesCovered',
  'linesValid',
  'lineRate',
  'branchesCovered',
  'branchesValid',
  'branchRate',
]
for (const [name, baseline] of Object.entries(criticalBaseline.areas)) {
  const current = criticalAreas[name]
  for (const field of metricFields) {
    const expected = baseline[field]
    const actual = current[field]
    const matches = expected === null
      ? actual === null
      : typeof actual === 'number' && Math.abs(actual - expected) <= 1e-10
    if (!matches) {
      failures.push(`critical ${name} baseline ${field}=${expected} does not match current coverage ${actual}; ratchet the baseline to the verified result without lowering it`)
    }
  }
}

if (baseCriticalBaseline.baseline !== null) {
  for (const name of requiredCriticalAreas) {
    const baseArea = baseCriticalBaseline.baseline.areas[name]
    const currentArea = criticalBaseline.areas[name]
    const removedPathIncludes = baseArea.pathIncludes
      .filter((path) => !currentArea.pathIncludes.includes(path))
    const removedPathEndsWith = baseArea.pathEndsWith
      .filter((path) => !currentArea.pathEndsWith.includes(path))
    if (removedPathIncludes.length > 0 || removedPathEndsWith.length > 0) {
      failures.push(
        `critical ${name} path policy narrows the Git-base scope; removed pathIncludes: ${removedPathIncludes.join(', ') || 'none'}; removed pathEndsWith: ${removedPathEndsWith.join(', ') || 'none'}`,
      )
    }
    if (currentArea.lineRate + Number.EPSILON < baseArea.lineRate) {
      failures.push(`critical ${name} line coverage baseline declined from ${percentage(baseArea.lineRate)} to ${percentage(currentArea.lineRate)}`)
    }
    if (baseArea.branchRate !== null
        && (currentArea.branchRate === null
          || currentArea.branchRate + Number.EPSILON < baseArea.branchRate)) {
      failures.push(`critical ${name} branch coverage baseline declined from ${percentage(baseArea.branchRate)} to ${percentage(currentArea.branchRate)}`)
    }
  }
}

if (requireDiff && modifiedSelection.base === null) {
  failures.push('pull-request modified-line coverage requires a non-zero COVERAGE_DIFF_BASE')
}
if (modifiedSelection.inventoryMissing.length > 0) {
  failures.push(`changed production files are missing from coverage inventory: ${modifiedSelection.inventoryMissing.join(', ')}`)
}
if (modified !== null && modified.lineRate < thresholds.modifiedLine) {
  failures.push(`modified line coverage ${percentage(modified.lineRate)} is below ${thresholds.modifiedLine}%`)
}

if (failures.length > 0 && !measureOnly) {
  console.error(failures.map((failure) => `- ${failure}`).join('\n'))
  process.exitCode = 1
} else if (failures.length > 0) {
  console.warn(failures.map((failure) => `- measure only: ${failure}`).join('\n'))
} else {
  console.log('Coverage thresholds passed.')
}
