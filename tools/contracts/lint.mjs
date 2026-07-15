import { createRequire } from 'node:module'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const directory = path.dirname(fileURLToPath(import.meta.url))
const repositoryRoot = path.resolve(directory, '../..')
const requireFromFrontend = createRequire(
  new URL('../../frontend/package.json', import.meta.url),
)
const { ESLint } = requireFromFrontend('eslint')

const eslint = new ESLint({
  cwd: repositoryRoot,
  overrideConfigFile: path.join(directory, 'eslint.config.mjs'),
})
const results = await eslint.lintFiles(['tools/contracts/**/*.mjs'])
const formatter = await eslint.loadFormatter('stylish')
const report = formatter.format(results)
if (report) {
  process.stdout.write(report)
}

const errorCount = results.reduce((total, result) => total + result.errorCount, 0)
const warningCount = results.reduce((total, result) => total + result.warningCount, 0)
if (errorCount > 0 || warningCount > 0) {
  process.exitCode = 1
}
