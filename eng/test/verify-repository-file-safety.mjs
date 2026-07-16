import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import {
  fstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'

import {
  repositoryFileErrorCodes,
  resolveCanonicalRepositoryPath,
  withReadOnlyRepositoryFile,
} from '../policies/repository-file.mjs'

const repositoryRoot = mkdtempSync(resolve(tmpdir(), 'poolai-repository-file-'))
const outsideRoot = mkdtempSync(resolve(tmpdir(), 'poolai-repository-file-outside-'))

try {
  const evidencePath = resolve(repositoryRoot, 'evidence.txt')
  const outsidePath = resolve(outsideRoot, 'outside.txt')
  writeFileSync(evidencePath, 'original evidence\n', 'utf8')
  writeFileSync(outsidePath, 'outside evidence\n', 'utf8')

  const canonicalEvidence = resolveCanonicalRepositoryPath(repositoryRoot, 'evidence.txt')
  assert.equal(canonicalEvidence, realpathSync(evidencePath))
  assert.equal(
    withReadOnlyRepositoryFile(
      repositoryRoot,
      'evidence.txt',
      (descriptor) => readFileSync(descriptor, 'utf8'),
    ),
    'original evidence\n',
  )

  assert.throws(
    () => resolveCanonicalRepositoryPath(repositoryRoot, '../outside.txt'),
    (error) => error?.code === repositoryFileErrorCodes.invalidRelativePath,
  )
  assert.throws(
    () => resolveCanonicalRepositoryPath(repositoryRoot, 'missing.txt'),
    (error) => error?.code === 'ENOENT',
  )

  const outsideLink = resolve(repositoryRoot, 'outside-link.txt')
  symlinkSync(outsidePath, outsideLink)
  assert.throws(
    () => resolveCanonicalRepositoryPath(repositoryRoot, 'outside-link.txt'),
    (error) => error?.code === repositoryFileErrorCodes.canonicalEscape,
  )

  const finalLink = resolve(repositoryRoot, 'final-link.txt')
  symlinkSync(evidencePath, finalLink)
  assert.throws(
    () => withReadOnlyRepositoryFile(repositoryRoot, 'final-link.txt', () => undefined),
    (error) => error?.code === 'ELOOP',
  )

  const directoryPath = resolve(repositoryRoot, 'directory')
  mkdirSync(directoryPath)
  assert.throws(
    () => withReadOnlyRepositoryFile(repositoryRoot, 'directory', () => undefined),
    (error) => error?.code === repositoryFileErrorCodes.notRegularFile,
  )

  const fifoPath = resolve(repositoryRoot, 'evidence.fifo')
  const mkfifo = spawnSync('mkfifo', [fifoPath], { encoding: 'utf8' })
  if (mkfifo.status !== 0) {
    throw new Error(`mkfifo probe setup failed: ${(mkfifo.stderr || mkfifo.stdout).trim()}`)
  }
  assert.throws(
    () => withReadOnlyRepositoryFile(repositoryRoot, 'evidence.fifo', () => undefined),
    (error) => error?.code === repositoryFileErrorCodes.notRegularFile,
  )

  const parentPath = resolve(repositoryRoot, 'parent')
  const originalParentPath = resolve(repositoryRoot, 'parent-original')
  const outsideParentPath = resolve(outsideRoot, 'parent')
  mkdirSync(parentPath)
  mkdirSync(outsideParentPath)
  writeFileSync(resolve(parentPath, 'nested.txt'), 'inside nested evidence\n', 'utf8')
  writeFileSync(resolve(outsideParentPath, 'nested.txt'), 'outside nested evidence\n', 'utf8')
  assert.equal(
    resolveCanonicalRepositoryPath(repositoryRoot, 'parent/nested.txt'),
    realpathSync(resolve(parentPath, 'nested.txt')),
  )
  renameSync(parentPath, originalParentPath)
  symlinkSync(outsideParentPath, parentPath, 'dir')
  assert.throws(
    () => withReadOnlyRepositoryFile(
      repositoryRoot,
      'parent/nested.txt',
      (descriptor) => readFileSync(descriptor, 'utf8'),
    ),
    (error) => error?.code === repositoryFileErrorCodes.canonicalEscape,
  )

  const stablePath = resolve(repositoryRoot, 'stable.txt')
  const movedPath = resolve(repositoryRoot, 'stable-original.txt')
  writeFileSync(stablePath, 'stable original\n', 'utf8')
  const stableRead = withReadOnlyRepositoryFile(repositoryRoot, 'stable.txt', (descriptor) => {
    renameSync(stablePath, movedPath)
    writeFileSync(stablePath, 'replacement path\n', 'utf8')
    return readFileSync(descriptor, 'utf8')
  })
  assert.equal(stableRead, 'stable original\n')
  assert.equal(readFileSync(stablePath, 'utf8'), 'replacement path\n')

  let successfulDescriptor
  withReadOnlyRepositoryFile(repositoryRoot, 'evidence.txt', (descriptor) => {
    successfulDescriptor = descriptor
  })
  assert.throws(
    () => fstatSync(successfulDescriptor),
    (error) => error?.code === 'EBADF',
  )

  let failedDescriptor
  assert.throws(
    () => withReadOnlyRepositoryFile(repositoryRoot, 'evidence.txt', (descriptor) => {
      failedDescriptor = descriptor
      throw new Error('intentional descriptor cleanup probe')
    }),
    /intentional descriptor cleanup probe/u,
  )
  assert.throws(
    () => fstatSync(failedDescriptor),
    (error) => error?.code === 'EBADF',
  )
} finally {
  rmSync(repositoryRoot, { force: true, recursive: true })
  rmSync(outsideRoot, { force: true, recursive: true })
}

console.log(
  'Repository evidence file safety valid: canonical bounds, parent replacement, O_NOFOLLOW, '
    + 'non-blocking special-file rejection, stable descriptors, and descriptor cleanup passed.',
)
