import { readFile } from 'node:fs/promises'
import path from 'node:path'

import { parseErrorCatalog } from './catalog.mjs'
import { contractPaths, invariant } from './context.mjs'
import {
  validateChatSse,
  validateGatewayProblemFixture,
  validateResponsesSse,
} from './fixtures.mjs'
import { generateCSharp, generateTypeScript } from './generator.mjs'
import { validateOpenApi } from './openapi.mjs'
import { scanSqlP0001Codes, validateSqlErrorMapping } from './sql-errors.mjs'

function expectFailure(action, expectedMessage) {
  try {
    action()
  } catch (error) {
    invariant(
      error.message.includes(expectedMessage),
      `Negative contract test expected "${expectedMessage}", received "${error.message}".`,
    )
    return
  }
  throw new Error(`Negative contract test did not fail: ${expectedMessage}`)
}

function findOperation(openApi, operationId) {
  for (const [route, pathItem] of Object.entries(openApi.paths)) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (operation?.operationId === operationId) {
        return { method, operation, pathItem, route }
      }
    }
  }
  throw new Error(`Self-test could not find operation ${operationId}.`)
}

function withoutParameter(operation, componentName) {
  operation.parameters = (operation.parameters ?? []).filter(
    (parameter) => parameter.$ref !== `#/components/parameters/${componentName}`,
  )
}

export async function runSelfTests({
  catalog,
  errorCatalogSource,
  openApi,
  openApiSource,
  sqlMapping,
  sqlSources,
  validateSchema,
}) {
  let negativeCases = 0
  const negative = (action, expectedMessage) => {
    expectFailure(action, expectedMessage)
    negativeCases += 1
  }

  const externalReference = structuredClone(openApi)
  externalReference.components.schemas.User.properties.id = {
    $ref: 'https://poolai.invalid/schemas/Uuid',
  }
  negative(() => validateOpenApi(externalReference), 'External $ref is forbidden')

  const brokenReference = structuredClone(openApi)
  brokenReference.components.schemas.User.properties.id = {
    $ref: '#/components/schemas/DoesNotExist',
  }
  negative(() => validateOpenApi(brokenReference), 'Broken local reference')

  const unsupportedSchema = structuredClone(openApi)
  unsupportedSchema.components.schemas.Uuid.prefixItems = [{ type: 'string' }]
  negative(() => validateOpenApi(unsupportedSchema), 'Unsupported schema keyword prefixItems')

  const unreferencedInvalidSchema = structuredClone(openApi)
  unreferencedInvalidSchema.components.schemas.UnreferencedInvalidSchema = {
    minimum: 'not-a-number',
    type: 'number',
  }
  negative(() => validateOpenApi(unreferencedInvalidSchema), 'minimum')

  const duplicateOperation = structuredClone(openApi)
  findOperation(duplicateOperation, 'login').operation.operationId = 'createResponse'
  negative(() => validateOpenApi(duplicateOperation), 'Duplicate operationId createResponse')

  const wrongGatewaySecurity = structuredClone(openApi)
  findOperation(wrongGatewaySecurity, 'createResponse').operation.security = [{ UserJwt: [] }]
  negative(
    () => validateOpenApi(wrongGatewaySecurity),
    'createResponse must declare only PoolApiKey security',
  )

  const implicitAnonymousSecurity = structuredClone(openApi)
  delete findOperation(implicitAnonymousSecurity, 'login').operation.security
  negative(
    () => validateOpenApi(implicitAnonymousSecurity),
    'login must explicitly declare security: []',
  )

  const wrongControlSecurity = structuredClone(openApi)
  findOperation(wrongControlSecurity, 'adminGetUser').operation.security = [{ PoolApiKey: [] }]
  negative(
    () => validateOpenApi(wrongControlSecurity),
    'adminGetUser must declare only UserJwt security',
  )

  const missingPathParameter = structuredClone(openApi)
  withoutParameter(findOperation(missingPathParameter, 'adminGetUser').operation, 'UserId')
  negative(
    () => validateOpenApi(missingPathParameter),
    'adminGetUser path parameters must exactly match template',
  )

  const duplicatePathParameter = structuredClone(openApi)
  findOperation(duplicatePathParameter, 'adminGetUser').operation.parameters.push({
    $ref: '#/components/parameters/UserId',
  })
  negative(() => validateOpenApi(duplicatePathParameter), 'repeats parameter path:userId')

  const optionalPathParameter = structuredClone(openApi)
  optionalPathParameter.components.parameters.UserId.required = false
  negative(
    () => validateOpenApi(optionalPathParameter),
    'adminGetUser path parameters must set required=true',
  )

  const missingRoles = structuredClone(openApi)
  delete findOperation(missingRoles, 'adminGetUser').operation['x-required-roles']
  negative(() => validateOpenApi(missingRoles), 'adminGetUser must declare x-required-roles')

  const missingIdempotency = structuredClone(openApi)
  withoutParameter(findOperation(missingIdempotency, 'adminCreateUser').operation, 'IdempotencyKey')
  negative(
    () => validateOpenApi(missingIdempotency),
    'adminCreateUser must require Idempotency-Key',
  )

  const missingIfMatch = structuredClone(openApi)
  withoutParameter(findOperation(missingIfMatch, 'adminUpdateUser').operation, 'IfMatch')
  negative(() => validateOpenApi(missingIfMatch), 'adminUpdateUser must require If-Match')

  const missingEtag = structuredClone(openApi)
  delete findOperation(missingEtag, 'adminCreateUser').operation.responses['201'].headers.ETag
  negative(
    () => validateOpenApi(missingEtag),
    'adminCreateUser successful responses must include ETag',
  )

  const wrongMediaProjection = structuredClone(openApi)
  wrongMediaProjection.components.responses.GatewayBadRequest.content['application/json'].schema = {
    $ref: '#/components/schemas/ControlPlaneProblem',
  }
  negative(() => validateOpenApi(wrongMediaProjection), 'createResponse 400 must project GatewayProblem')

  const firstCatalogLine = `| \`${catalog.entries[0].code}\` | 400 | 否 | — | duplicate |`
  const duplicateCatalog = errorCatalogSource.replace(
    '\n## 4. Responses 与 Chat 流错误',
    `\n${firstCatalogLine}\n\n## 4. Responses 与 Chat 流错误`,
  )
  negative(
    () => parseErrorCatalog(duplicateCatalog),
    'Duplicate error code',
  )
  invariant(
    !catalog.codes.has('prepared') && !catalog.codes.has('business_output_started'),
    'Attempt phases were incorrectly parsed as stable error codes.',
  )

  const responseErrorFixtureName = 'responses-stream-error.sse'
  const responseErrorFixture = await readFile(
    path.join(contractPaths.fixtures, responseErrorFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateResponsesSse(
        responseErrorFixture.replace('"sequence_number":3', '"sequence_number":4'),
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'non-contiguous sequence_number',
  )

  const responseCompletedFixtureName = 'responses-stream-completed.sse'
  const responseCompletedFixture = await readFile(
    path.join(contractPaths.fixtures, responseCompletedFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateResponsesSse(
        responseCompletedFixture.replace('"input_tokens":8', '"input_tokens":9007199254740992'),
        responseCompletedFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'input_tokens must be a non-negative safe integer',
  )
  negative(
    () =>
      validateResponsesSse(
        responseCompletedFixture.replace('"total_tokens":10', '"total_tokens":11'),
        responseCompletedFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'usage totals do not add up',
  )

  const responseFunctionFixtureName = 'responses-stream-function-call.sse'
  const responseFunctionFixture = await readFile(
    path.join(contractPaths.fixtures, responseFunctionFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateResponsesSse(
        responseFunctionFixture.replace(
          '"arguments":"{\\"city\\":\\"上海\\"}"',
          '"arguments":"{\\"city\\":\\"北京\\"}"',
        ),
        responseFunctionFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'function done arguments do not match concatenated deltas',
  )

  const gatewayFixtureName = 'gateway-upstream-usage-out-of-range.json'
  const gatewayFixture = JSON.parse(
    await readFile(path.join(contractPaths.fixtures, gatewayFixtureName), 'utf8'),
  )
  gatewayFixture.error.code = 'internal_error'
  negative(
    () => validateGatewayProblemFixture(gatewayFixture, gatewayFixtureName, catalog.codes),
    'outer code must equal error.code',
  )

  const chatFixtureName = 'chat-completions-error.sse'
  const chatFixture = await readFile(path.join(contractPaths.fixtures, chatFixtureName), 'utf8')
  negative(
    () => validateChatSse(`${chatFixture.trim()}\n\ndata: [DONE]\n`, chatFixtureName, catalog.codes),
    'Chat error must be last',
  )

  const missingSqlMapping = structuredClone(sqlMapping)
  missingSqlMapping.entries.shift()
  negative(
    () =>
      validateSqlErrorMapping({
        errorCodes: catalog.codes,
        mapping: missingSqlMapping,
        sqlSources,
      }),
    'Literal P0001 codes missing from the map',
  )

  const staleSqlMapping = structuredClone(sqlMapping)
  staleSqlMapping.entries.push({
    classification: 'internal',
    public_code: 'internal_error',
    source: '0001_baseline.sql',
    sql_code: 'stale_contract_code',
  })
  negative(
    () =>
      validateSqlErrorMapping({
        errorCodes: catalog.codes,
        mapping: staleSqlMapping,
        sqlSources,
      }),
    'Stale P0001 mappings',
  )

  negative(
    () =>
      scanSqlP0001Codes(
        'DO $body$ BEGIN PERFORM poolai_business_error(v_dynamic_code); END $body$;',
        'dynamic-helper.sql',
      ),
    'dynamic first argument',
  )
  invariant(
    scanSqlP0001Codes(
      "-- PERFORM poolai_business_error(v_dynamic_code);\nSELECT 'poolai_business_error(v_dynamic_code)';",
      'comments-and-strings.sql',
    ).size === 0,
    'SQL scanning treats a comment or quoted string as executable helper code.',
  )

  const unexpectedCSharpUnion = structuredClone(openApi)
  unexpectedCSharpUnion.components.schemas.UnexpectedUnion = {
    oneOf: [
      { $ref: '#/components/schemas/User' },
      { $ref: '#/components/schemas/Group' },
    ],
  }
  negative(
    () => generateCSharp(unexpectedCSharpUnion, openApiSource, errorCatalogSource),
    'Unapproved root object union UnexpectedUnion',
  )

  const generatedTypeScript = generateTypeScript(openApi, openApiSource, errorCatalogSource)
  const generatedCSharp = generateCSharp(openApi, openApiSource, errorCatalogSource)
  invariant(
    generatedTypeScript.includes(
      'export type ChatMessage = ChatSystemMessage | ChatDeveloperMessage | ChatUserMessage | ChatAssistantMessage | ChatToolMessage',
    ),
    'TypeScript ChatMessage must remain a discriminated union.',
  )
  invariant(
    !generatedCSharp.includes('class ChatMessage') &&
      !generatedCSharp.includes('class LoginResult') &&
      generatedCSharp.includes('IReadOnlyList<JsonElement> Messages'),
    'C# union allowlist must preserve ChatMessage/LoginResult payloads as JsonElement.',
  )
  invariant(
    generatedTypeScript === generateTypeScript(openApi, openApiSource, errorCatalogSource),
    'TypeScript generation is not deterministic.',
  )
  invariant(
    generatedCSharp === generateCSharp(openApi, openApiSource, errorCatalogSource),
    'C# generation is not deterministic.',
  )

  return { negativeCases, deterministicGenerators: 2 }
}
