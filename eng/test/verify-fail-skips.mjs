import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs'
import { resolve } from 'node:path'
import { spawnSync } from 'node:child_process'

const root = resolve(import.meta.dirname, '..', '..')
const configuration = process.env.Configuration ?? 'Debug'
const runnerConfigPath = resolve(root, 'tests/xunit.runner.json')
const runnerConfig = JSON.parse(readFileSync(runnerConfigPath, 'utf8'))
const testProjects = [
  'PoolAI.UnitTests',
  'PoolAI.ArchitectureTests',
  'PoolAI.ContractTests',
  'PoolAI.IntegrationTests',
  'PoolAI.EndToEndTests',
  'PoolAI.LoadTests',
]

if (runnerConfig.failSkips !== true) {
  throw new Error('tests/xunit.runner.json must set failSkips to true.')
}

for (const project of testProjects) {
  const copiedConfigPath = resolve(root, 'tests', project, 'bin', configuration, 'net10.0', 'xunit.runner.json')
  if (!existsSync(copiedConfigPath)) {
    throw new Error(`${project} build output is missing xunit.runner.json.`)
  }
  const copiedConfig = JSON.parse(readFileSync(copiedConfigPath, 'utf8'))
  if (copiedConfig.failSkips !== true) {
    throw new Error(`${project} build output does not enforce failSkips.`)
  }
}

const probeRoot = resolve(root, 'artifacts/test-policy/fail-skips-probe')
rmSync(probeRoot, { force: true, recursive: true })
mkdirSync(probeRoot, { recursive: true })

const probeName = 'PoolAI.UnitTests.FailSkipsGateProbe.DynamicSkipMustFail'
const probe = spawnSync(
    'dotnet',
  [
    'test',
    'tests/PoolAI.UnitTests/PoolAI.UnitTests.csproj',
    '--no-build',
    '--filter',
    `FullyQualifiedName=${probeName}`,
    '--logger',
    'trx;LogFileName=fail-skips-probe.trx',
    '--results-directory',
    probeRoot,
    '--verbosity',
    'minimal',
  ],
  {
    cwd: root,
    encoding: 'utf8',
    env: {
      ...process.env,
      POOLAI_FAIL_SKIPS_GATE_PROBE: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
      DOTNET_MULTILEVEL_LOOKUP: '0',
      MSBUILDDISABLENODEREUSE: '1',
    },
    maxBuffer: 16 * 1024 * 1024,
  },
)

const resultPath = resolve(probeRoot, 'fail-skips-probe.trx')
const result = existsSync(resultPath) ? readFileSync(resultPath, 'utf8') : ''
const output = `${probe.stdout ?? ''}\n${probe.stderr ?? ''}`.trim()
const probeResult = [...result.matchAll(/<UnitTestResult\b[^>]*>/gu)]
  .map((match) => match[0])
  .find((tag) => tag.includes(`testName="${probeName}"`))
const expectedFailure = probeResult?.includes('outcome="Failed"')
  && result.includes('FAIL_SKIP : Intentional quality-gate probe')

if (probe.status === 0 || !expectedFailure) {
  const detail = output.length > 0 ? `\n${output}` : ''
  throw new Error(`Dynamic xUnit skip probe was not recorded as the expected failed test.${detail}`)
}

rmSync(probeRoot, { force: true, recursive: true })
console.log(`xUnit failSkips policy valid for ${testProjects.length} projects; dynamic skip probe failed as required.`)
