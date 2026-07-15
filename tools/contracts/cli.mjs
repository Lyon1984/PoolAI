#!/usr/bin/env node

import { parseErrorCatalog } from './lib/catalog.mjs'
import { ContractFailure, loadContractSources } from './lib/context.mjs'
import { validateFixtures } from './lib/fixtures.mjs'
import { generateContracts } from './lib/generator.mjs'
import { validateOpenApi } from './lib/openapi.mjs'
import { runSelfTests } from './lib/self-tests.mjs'
import { validateSqlErrorMap } from './lib/sql-errors.mjs'

const command = process.argv[2] ?? 'all'
const check = process.argv.includes('--check')
const supportedCommands = new Set(['all', 'generate', 'test', 'validate'])

if (!supportedCommands.has(command)) {
  process.stderr.write(
    `Unknown command ${command}. Use validate, test, generate, or all; generate/all accept --check.\n`,
  )
  process.exitCode = 2
} else if (check && !['all', 'generate'].includes(command)) {
  process.stderr.write('--check is supported only by generate and all.\n')
  process.exitCode = 2
} else {
  try {
    const sources = await loadContractSources()
    const catalog = parseErrorCatalog(sources.errorCatalogSource)
    const openApiResult = validateOpenApi(sources.openApi)
    process.stdout.write(
      `OpenAPI valid: ${openApiResult.operations} operations, ${openApiResult.compiledSchemas} AJV-compiled component schemas, ${openApiResult.references} local refs.\n`,
    )
    process.stdout.write(`Error catalog valid: ${catalog.entries.length} stable codes.\n`)

    if (command === 'test' || command === 'all') {
      const fixtureResult = await validateFixtures(openApiResult.validateSchema, catalog.codes)
      const sqlResult = await validateSqlErrorMap(catalog.codes)
      const selfTestResult = await runSelfTests({
        ...sources,
        catalog,
        sqlMapping: sqlResult.mapping,
        sqlSources: sqlResult.sqlSources,
        validateSchema: openApiResult.validateSchema,
      })
      process.stdout.write(
        `Fixtures valid: ${fixtureResult.fixtureCount} files, ${fixtureResult.sseFrames} SSE frames.\n`,
      )
      process.stdout.write(`SQL P0001 map valid: ${sqlResult.mappedCodes} literal codes.\n`)
      process.stdout.write(
        `Validator self-tests passed: ${selfTestResult.negativeCases} negative cases, ${selfTestResult.deterministicGenerators} deterministic generators.\n`,
      )
    }

    if (command === 'generate' || command === 'all') {
      const outputFiles = await generateContracts({ ...sources, catalog, check })
      process.stdout.write(
        `${check ? 'Generated outputs are current' : 'Generated outputs written'}: ${outputFiles.length} files.\n`,
      )
    }
  } catch (error) {
    const prefix = error instanceof ContractFailure ? 'Contract failure' : 'Contract tooling failure'
    process.stderr.write(`${prefix}: ${error.message}\n`)
    if (process.env.CONTRACT_DEBUG === '1' && error.stack) {
      process.stderr.write(`${error.stack}\n`)
    }
    process.exitCode = 1
  }
}
