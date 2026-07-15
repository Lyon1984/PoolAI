import { spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import {
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { relative, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const artifactsRoot = resolve(root, 'artifacts')
const traceability = JSON.parse(readFileSync(
  resolve(root, 'docs/traceability/release-1-traceability.json'),
  'utf8',
))
const suffix = traceability.externalEvidence?.taskSystem?.status === 'verified'
  ? 'verified'
  : 'preview'
const outputNames = [
  `poolai-m0-m7-epics.${suffix}.json`,
  `poolai-m0-m7-epics.${suffix}.csv`,
]
const sentinel = 'task-import-symlink-sentinel\n'

mkdirSync(artifactsRoot, { recursive: true })

for (const outputName of outputNames) {
  const outputDirectory = mkdtempSync(resolve(artifactsRoot, 'task-import-safety-'))
  const externalDirectory = mkdtempSync(resolve(tmpdir(), 'poolai-task-import-safety-'))
  const externalTarget = resolve(externalDirectory, outputName)
  const linkedOutput = resolve(outputDirectory, outputName)
  try {
    writeFileSync(externalTarget, sentinel, 'utf8')
    symlinkSync(externalTarget, linkedOutput)
    const result = spawnSync(
      process.execPath,
      [
        resolve(root, 'eng/release/prepare-task-import.mjs'),
        '--output-dir',
        relative(root, outputDirectory),
      ],
      { cwd: root, encoding: 'utf8' },
    )
    if (result.status === 0) {
      throw new Error(`Task import unexpectedly accepted symlink output target ${outputName}.`)
    }
    if (readFileSync(externalTarget, 'utf8') !== sentinel) {
      throw new Error(`Task import followed symlink output target ${outputName}.`)
    }
    if (!lstatSync(linkedOutput).isSymbolicLink()) {
      throw new Error(`Task import replaced symlink output target ${outputName} before rejecting it.`)
    }
  } finally {
    rmSync(outputDirectory, { recursive: true, force: true })
    rmSync(externalDirectory, { recursive: true, force: true })
  }
}

const outsideArtifactsDirectory = mkdtempSync(resolve(root, '.task-import-safety-outside-'))
const linkedOutputDirectory = resolve(artifactsRoot, `task-import-directory-safety-${randomUUID()}`)
const escapedChildName = 'created-before-rejection'
const escapedOutputDirectory = resolve(linkedOutputDirectory, escapedChildName)
try {
  symlinkSync(outsideArtifactsDirectory, linkedOutputDirectory, 'dir')
  const result = spawnSync(
    process.execPath,
    [
      resolve(root, 'eng/release/prepare-task-import.mjs'),
      '--output-dir',
      relative(root, escapedOutputDirectory),
    ],
    { cwd: root, encoding: 'utf8' },
  )
  if (result.status === 0) {
    throw new Error('Task import unexpectedly accepted an output directory symlink outside artifacts/.')
  }
  if (existsSync(resolve(outsideArtifactsDirectory, escapedChildName))) {
    throw new Error('Task import created a directory outside artifacts/ before rejecting a directory symlink.')
  }
  if (!lstatSync(linkedOutputDirectory).isSymbolicLink()) {
    throw new Error('Task import replaced the unsafe output directory symlink before rejecting it.')
  }
} finally {
  try {
    unlinkSync(linkedOutputDirectory)
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error
    }
  }
  rmSync(outsideArtifactsDirectory, { recursive: true, force: true })
}

process.stdout.write(
  `Task import output safety valid: ${outputNames.length} symlink targets and `
    + '1 directory escape rejected without out-of-scope writes.\n',
)
