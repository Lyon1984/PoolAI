#!/usr/bin/env node

import { parseErrorCatalog } from './lib/catalog.mjs'
import { runCompatibilitySelfTests } from './lib/compatibility-self-tests.mjs'
import { runCompatibilityWindowSelfTests } from './lib/compatibility-window-self-tests.mjs'
import {
  validateCompatibilityResetDecisions,
  validateContractsAgainstGitBase,
} from './lib/compatibility.mjs'
import { validateCompatibilityWindowDecisions } from './lib/compatibility-windows.mjs'
import { ContractFailure, loadContractSources } from './lib/context.mjs'
import { validateFixtures } from './lib/fixtures.mjs'
import { generateContracts } from './lib/generator.mjs'
import { validateOpenApi } from './lib/openapi.mjs'
import { runSelfTests } from './lib/self-tests.mjs'
import { validateSqlErrorMap } from './lib/sql-errors.mjs'

const command = process.argv[2] ?? 'all'
const check = process.argv.includes('--check')
const supportedCommands = new Set(['all', 'compatibility', 'generate', 'test', 'validate'])

if (!supportedCommands.has(command)) {
  process.stderr.write(
    `Unknown command ${command}. Use validate, test, compatibility, generate, or all; generate/all accept --check.\n`,
  )
  process.exitCode = 2
} else if (check && !['all', 'generate'].includes(command)) {
  process.stderr.write('--check is supported only by generate and all.\n')
  process.exitCode = 2
} else {
  try {
    const sources = await loadContractSources()
    const catalog = parseErrorCatalog(sources.errorCatalogSource)
    const openApiResult = validateOpenApi(sources.openApi, catalog)
    const resetState = validateCompatibilityResetDecisions(sources.compatibilityResetSource)
    const windowState = validateCompatibilityWindowDecisions(sources.compatibilityWindowSource)
    process.stdout.write(
      `OpenAPI valid: ${openApiResult.operations} operations, ${openApiResult.compiledSchemas} AJV-compiled component schemas, ${openApiResult.references} local refs.\n`,
    )
    process.stdout.write(`Error catalog valid: ${catalog.entries.length} stable codes.\n`)
    process.stdout.write(
      `Compatibility reset registry valid: ${resetState.registry.resets.length} accepted exact transition.\n`,
    )
    process.stdout.write(
      `Compatibility window registry valid: ${windowState.registry.windows.length} strict transition candidate.\n`,
    )

    if (command === 'compatibility') {
      const baseRef = process.env.CONTRACT_DIFF_BASE
      if (!baseRef) {
        throw new ContractFailure('CONTRACT_DIFF_BASE is required for compatibility checks.')
      }
      const compatibilityResult = await validateContractsAgainstGitBase({
        baseRef,
        compatibilityResetSource: sources.compatibilityResetSource,
        compatibilityWindowSource: sources.compatibilityWindowSource,
        headErrorCatalogSource: sources.errorCatalogSource,
        headOpenApi: sources.openApi,
        headOpenApiSource: sources.openApiSource,
      })
      process.stdout.write(
        `Contract compatibility valid against ${compatibilityResult.baseRef}: ${compatibilityResult.operations} existing operations, ${compatibilityResult.schemas} existing schemas, ${compatibilityResult.errorCodes} existing stable error codes, and ${compatibilityResult.sseFixtures} existing SSE fixtures preserved.\n`,
      )
      if (compatibilityResult.resetId !== undefined) {
        process.stdout.write(
          `Exact compatibility reset applied: ${compatibilityResult.resetId}; ${compatibilityResult.waivedFailures} registered breaking diagnostics consumed.\n`,
        )
      }
      if (compatibilityResult.windowId !== undefined) {
        process.stdout.write(
          `Exact compatibility window applied: ${compatibilityResult.windowId}; ${compatibilityResult.waivedFailures} registered breaking diagnostics consumed.\n`,
        )
      }
    }

    if (command === 'test' || command === 'all') {
      const fixtureResult = await validateFixtures(openApiResult.validateSchema, catalog)
      const sqlResult = await validateSqlErrorMap(catalog.codes)
      const compatibilitySelfTestResult = runCompatibilitySelfTests(sources)
      const compatibilityWindowSelfTestResult = runCompatibilityWindowSelfTests(sources)
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
      process.stdout.write(
        `Compatibility self-tests passed: ${compatibilitySelfTestResult.additiveCases} additive cases, ${compatibilitySelfTestResult.breakingCases} breaking cases, ${compatibilitySelfTestResult.resetCases} exact-reset cases.\n`,
      )
      process.stdout.write(
        `Compatibility-window self-tests passed: ${compatibilityWindowSelfTestResult.cases} strict cases.\n`,
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
