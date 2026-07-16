import { closeSync, constants, fstatSync, openSync, realpathSync, statSync } from 'node:fs'
import { resolve, sep } from 'node:path'

export const repositoryFileErrorCodes = Object.freeze({
  canonicalEscape: 'POOLAI_REPOSITORY_CANONICAL_ESCAPE',
  invalidRelativePath: 'POOLAI_INVALID_REPOSITORY_RELATIVE_PATH',
  lexicalEscape: 'POOLAI_REPOSITORY_LEXICAL_ESCAPE',
  noNonblock: 'POOLAI_NO_NONBLOCK',
  noNoFollow: 'POOLAI_NO_NOFOLLOW',
  notRegularFile: 'POOLAI_NOT_REGULAR_FILE',
  pathChanged: 'POOLAI_REPOSITORY_PATH_CHANGED',
})

const repositoryFileError = (code, message) => Object.assign(new Error(message), { code })

const isInside = (root, candidate) => candidate === root || candidate.startsWith(`${root}${sep}`)

const resolveLexicalRepositoryPath = (root, path) => {
  if (typeof path !== 'string' || path.length === 0 || path.startsWith('/') || path.includes('..')) {
    throw repositoryFileError(
      repositoryFileErrorCodes.invalidRelativePath,
      'Expected a non-empty repository-relative path.',
    )
  }

  const repositoryRoot = realpathSync(resolve(root))
  const resolved = resolve(repositoryRoot, path)
  if (!isInside(repositoryRoot, resolved)) {
    throw repositoryFileError(
      repositoryFileErrorCodes.lexicalEscape,
      'Path resolves outside the repository.',
    )
  }
  return { repositoryRoot, resolved }
}

const requireCanonicalInsideRepository = (repositoryRoot, resolved) => {
  const canonical = realpathSync(resolved)
  if (!isInside(repositoryRoot, canonical)) {
    throw repositoryFileError(
      repositoryFileErrorCodes.canonicalEscape,
      'Path points outside the repository.',
    )
  }
  return canonical
}

export const resolveCanonicalRepositoryPath = (root, path) => {
  const { repositoryRoot, resolved } = resolveLexicalRepositoryPath(root, path)
  return requireCanonicalInsideRepository(repositoryRoot, resolved)
}

export const withReadOnlyRepositoryFile = (root, path, operation) => {
  const { repositoryRoot, resolved } = resolveLexicalRepositoryPath(root, path)
  if (!Number.isInteger(constants.O_NOFOLLOW)) {
    throw repositoryFileError(
      repositoryFileErrorCodes.noNoFollow,
      'O_NOFOLLOW support is required for repository evidence files.',
    )
  }
  if (!Number.isInteger(constants.O_NONBLOCK)) {
    throw repositoryFileError(
      repositoryFileErrorCodes.noNonblock,
      'O_NONBLOCK support is required for repository evidence files.',
    )
  }
  if (typeof operation !== 'function') {
    throw new TypeError('Repository file operation must be a function.')
  }

  let descriptor
  try {
    descriptor = openSync(
      resolved,
      constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
    )
    const openedFile = fstatSync(descriptor)
    if (!openedFile.isFile()) {
      throw repositoryFileError(
        repositoryFileErrorCodes.notRegularFile,
        'Repository evidence path is not a regular file.',
      )
    }

    const canonical = requireCanonicalInsideRepository(repositoryRoot, resolved)
    const currentFile = statSync(canonical)
    const confirmedCanonical = realpathSync(resolved)
    if (confirmedCanonical !== canonical
        || currentFile.dev !== openedFile.dev
        || currentFile.ino !== openedFile.ino) {
      throw repositoryFileError(
        repositoryFileErrorCodes.pathChanged,
        'Repository evidence path changed while it was being opened.',
      )
    }

    return operation(descriptor, canonical)
  } finally {
    if (descriptor !== undefined) {
      closeSync(descriptor)
    }
  }
}
