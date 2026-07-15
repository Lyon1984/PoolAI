import { readdir, readFile } from 'node:fs/promises'
import { extname, join, relative, resolve } from 'node:path'

const repositoryRoot = resolve(import.meta.dirname, '..', '..')

const rules = [
  {
    directory: 'src',
    extensions: new Set(['.cs']),
    pattern: /\bExcludeFrom(?:Code)?Coverage(?:Attribute)?\b/,
  },
  {
    directory: 'frontend/src',
    extensions: new Set(['.js', '.jsx', '.ts', '.tsx', '.vue']),
    pattern: /\b(?:istanbul|v8|c8)\s+ignore\b|\bnode:coverage\s+(?:ignore|disable)\b/i,
  },
]

const sourceFiles = async (directory, extensions) => {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = []

  for (const entry of entries) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) {
      files.push(...await sourceFiles(path, extensions))
    } else if (entry.isFile() && extensions.has(extname(entry.name))) {
      files.push(path)
    }
  }

  return files
}

const matches = []
for (const rule of rules) {
  const directory = resolve(repositoryRoot, rule.directory)
  const files = await sourceFiles(directory, rule.extensions)

  for (const file of files.sort()) {
    const lines = (await readFile(file, 'utf8')).split(/\r?\n/)
    for (const [index, line] of lines.entries()) {
      if (rule.pattern.test(line)) {
        matches.push(`${relative(repositoryRoot, file)}:${index + 1}:${line.trim()}`)
      }
    }
  }
}

if (matches.length > 0) {
  console.error('Production source coverage suppression is forbidden:')
  console.error(matches.join('\n'))
  process.exitCode = 1
} else {
  console.log('Coverage-integrity scan passed.')
}
