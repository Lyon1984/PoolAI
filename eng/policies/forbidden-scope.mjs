import { readdir, readFile, stat } from 'node:fs/promises'
import { basename, join, relative, resolve } from 'node:path'

const repositoryRoot = resolve(import.meta.dirname, '..', '..')
const searchRoots = ['src', 'frontend/src', 'deploy/config', '.github']
const excludedDirectories = new Set(['node_modules', 'dist', 'bin', 'obj'])
const forbidden = /\b(payment|billing|pricing|balance|refund|promo|redeem|affiliate|commission)\b|personal[-_ ]?quota|purchasable[-_ ]?quota/i
const guardMarker = 'poolai-forbidden-scope-guard'

const sourceFiles = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = []

  for (const entry of entries) {
    const path = join(directory, entry.name)
    const isLockPath = entry.name.toLowerCase().includes('lock')
    if (entry.isDirectory() && !excludedDirectories.has(entry.name) && !isLockPath) {
      files.push(...(await sourceFiles(path)))
    } else if (entry.isFile()
      && entry.name !== 'AGENTS.md'
      && !isLockPath) {
      files.push(path)
    }
  }

  return files
}

const existingRoots = []
for (const root of searchRoots) {
  const path = resolve(repositoryRoot, root)
  try {
    if ((await stat(path)).isDirectory()) {
      existingRoots.push(path)
    }
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error
    }
  }
}

if (existingRoots.length === 0) {
  console.log('No production/configuration roots exist yet.')
  process.exit(0)
}

const matches = []
for (const root of existingRoots) {
  const files = await sourceFiles(root)
  for (const file of files.sort()) {
    const lines = (await readFile(file, 'utf8')).split(/\r?\n/)
    for (const [index, line] of lines.entries()) {
      if (!line.includes(guardMarker) && forbidden.test(line)) {
        matches.push(`${relative(repositoryRoot, file)}:${index + 1}:${line.trim()}`)
      }
    }
  }
}

if (matches.length > 0) {
  console.error('Forbidden commercial or personal-quota scope found:')
  console.error(matches.join('\n'))
  process.exitCode = 1
} else {
  console.log('Forbidden-scope scan passed.')
}
