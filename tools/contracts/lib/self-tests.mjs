import { readFile } from 'node:fs/promises'
import path from 'node:path'

import { parseErrorCatalog } from './catalog.mjs'
import { contractPaths, invariant } from './context.mjs'
import {
  validateChatSse,
  validateControlPlaneProblemFixture,
  validateGatewayProblemFixture,
  validateResponsesSse,
} from './fixtures.mjs'
import { generateCSharp, generateTypeScript, generateTypeScriptErrors } from './generator.mjs'
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

function generatedClassBlock(source, className) {
  const marker = `public sealed class ${className}`
  const start = source.indexOf(marker)
  invariant(start >= 0, `Generated C# output is missing ${className}.`)
  const end = source.indexOf('\npublic sealed class ', start + marker.length)
  return source.slice(start, end < 0 ? undefined : end)
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
  const validateContract = (candidate) => validateOpenApi(candidate, catalog)

  const externalReference = structuredClone(openApi)
  externalReference.components.schemas.User.properties.id = {
    $ref: 'https://poolai.invalid/schemas/Uuid',
  }
  negative(() => validateContract(externalReference), 'External $ref is forbidden')

  const brokenReference = structuredClone(openApi)
  brokenReference.components.schemas.User.properties.id = {
    $ref: '#/components/schemas/DoesNotExist',
  }
  negative(() => validateContract(brokenReference), 'Broken local reference')

  const unsupportedSchema = structuredClone(openApi)
  unsupportedSchema.components.schemas.Uuid.prefixItems = [{ type: 'string' }]
  negative(() => validateContract(unsupportedSchema), 'Unsupported schema keyword prefixItems')

  const unreferencedInvalidSchema = structuredClone(openApi)
  unreferencedInvalidSchema.components.schemas.UnreferencedInvalidSchema = {
    minimum: 'not-a-number',
    type: 'number',
  }
  negative(() => validateContract(unreferencedInvalidSchema), 'minimum')

  const duplicateOperation = structuredClone(openApi)
  findOperation(duplicateOperation, 'login').operation.operationId = 'createResponse'
  negative(() => validateContract(duplicateOperation), 'Duplicate operationId createResponse')

  const wrongGatewaySecurity = structuredClone(openApi)
  findOperation(wrongGatewaySecurity, 'createResponse').operation.security = [{ UserJwt: [] }]
  negative(
    () => validateContract(wrongGatewaySecurity),
    'createResponse must declare only PoolApiKey security',
  )

  const implicitAnonymousSecurity = structuredClone(openApi)
  delete findOperation(implicitAnonymousSecurity, 'login').operation.security
  negative(
    () => validateContract(implicitAnonymousSecurity),
    'login must explicitly declare security: []',
  )

  const wrongControlSecurity = structuredClone(openApi)
  findOperation(wrongControlSecurity, 'adminGetUser').operation.security = [{ PoolApiKey: [] }]
  negative(
    () => validateContract(wrongControlSecurity),
    'adminGetUser must declare only UserJwt security',
  )

  const missingPathParameter = structuredClone(openApi)
  withoutParameter(findOperation(missingPathParameter, 'adminGetUser').operation, 'UserId')
  negative(
    () => validateContract(missingPathParameter),
    'adminGetUser path parameters must exactly match template',
  )

  const duplicatePathParameter = structuredClone(openApi)
  findOperation(duplicatePathParameter, 'adminGetUser').operation.parameters.push({
    $ref: '#/components/parameters/UserId',
  })
  negative(() => validateContract(duplicatePathParameter), 'repeats parameter path:userId')

  const optionalPathParameter = structuredClone(openApi)
  optionalPathParameter.components.parameters.UserId.required = false
  negative(
    () => validateContract(optionalPathParameter),
    'adminGetUser path parameters must set required=true',
  )

  const missingRoles = structuredClone(openApi)
  delete findOperation(missingRoles, 'adminGetUser').operation['x-required-roles']
  negative(() => validateContract(missingRoles), 'adminGetUser must declare x-required-roles')

  const missingIdempotency = structuredClone(openApi)
  withoutParameter(findOperation(missingIdempotency, 'adminCreateUser').operation, 'IdempotencyKey')
  negative(
    () => validateContract(missingIdempotency),
    'adminCreateUser must require Idempotency-Key',
  )

  const missingIfMatch = structuredClone(openApi)
  withoutParameter(findOperation(missingIfMatch, 'adminUpdateUser').operation, 'IfMatch')
  negative(() => validateContract(missingIfMatch), 'adminUpdateUser must require If-Match')

  const missingEtag = structuredClone(openApi)
  delete findOperation(missingEtag, 'adminCreateUser').operation.responses['201'].headers.ETag
  negative(
    () => validateContract(missingEtag),
    'adminCreateUser successful responses must include ETag',
  )

  const wrongMediaProjection = structuredClone(openApi)
  wrongMediaProjection.components.responses.GatewayBadRequest.content['application/json'].schema = {
    $ref: '#/components/schemas/ControlPlaneProblem',
  }
  negative(() => validateContract(wrongMediaProjection), 'createResponse 400 must project GatewayProblem')

  const missingBodyBadRequest = structuredClone(openApi)
  delete findOperation(missingBodyBadRequest, 'logout').operation.responses['400']
  negative(
    () => validateContract(missingBodyBadRequest),
    'logout with a request body must declare 400 invalid_request',
  )

  const missingAdminListBadRequest = structuredClone(openApi)
  delete findOperation(missingAdminListBadRequest, 'adminListGroups').operation.responses['400']
  negative(
    () => validateContract(missingAdminListBadRequest),
    'adminListGroups must declare 400 through #/components/responses/BadRequest for invalid query input',
  )

  const missingBodyPayloadTooLarge = structuredClone(openApi)
  delete findOperation(missingBodyPayloadTooLarge, 'login').operation.responses['413']
  negative(
    () => validateContract(missingBodyPayloadTooLarge),
    'login with a request body must declare 413 payload_too_large',
  )

  const missingBodyUnsupportedMediaType = structuredClone(openApi)
  delete findOperation(missingBodyUnsupportedMediaType, 'login').operation.responses['415']
  negative(
    () => validateContract(missingBodyUnsupportedMediaType),
    'login with a request body must declare 415 unsupported_media_type',
  )

  const swappedBodyMediaResponses = structuredClone(openApi)
  const swappedLogin = findOperation(swappedBodyMediaResponses, 'login').operation
  const payloadTooLargeResponse = swappedLogin.responses['413']
  swappedLogin.responses['413'] = swappedLogin.responses['415']
  swappedLogin.responses['415'] = payloadTooLargeResponse
  negative(
    () => validateContract(swappedBodyMediaResponses),
    'login 413 must reference #/components/responses/PayloadTooLarge for payload_too_large',
  )

  const wrongPayloadTooLargeCode = structuredClone(openApi)
  wrongPayloadTooLargeCode.components.responses.PayloadTooLarge['x-error-code'] =
    'unsupported_media_type'
  negative(
    () => validateContract(wrongPayloadTooLargeCode),
    'PayloadTooLarge must bind x-error-code payload_too_large',
  )

  const missingIdempotencyConflict = structuredClone(openApi)
  delete findOperation(missingIdempotencyConflict, 'revokeMyApiKey').operation.responses['409']
  negative(
    () => validateContract(missingIdempotencyConflict),
    'revokeMyApiKey must declare 409 idempotency_conflict',
  )

  const unknownExampleCode = structuredClone(openApi)
  const unknownGatewayExample =
    unknownExampleCode.components.responses.GatewayBadGateway.content['application/json'].examples
      .usageOutOfRange.value
  unknownGatewayExample.code = 'not_in_error_catalog'
  unknownGatewayExample.error.code = 'not_in_error_catalog'
  negative(() => validateContract(unknownExampleCode), 'uses unknown error code not_in_error_catalog')

  const mismatchedGatewayExample = structuredClone(openApi)
  mismatchedGatewayExample.components.responses.GatewayBadGateway.content[
    'application/json'
  ].examples.usageOutOfRange.value.error.code = 'internal_error'
  negative(() => validateContract(mismatchedGatewayExample), 'outer code must equal error.code')

  const wrongControlExampleStatus = structuredClone(openApi)
  wrongControlExampleStatus.components.responses.Conflict.content[
    'application/problem+json'
  ].examples.groupActivationNotReady.value.status = 422
  negative(() => validateContract(wrongControlExampleStatus), 'does not allow HTTP 422')

  const missingFixedRetryAfter = structuredClone(openApi)
  delete missingFixedRetryAfter.components.responses.GatewayTooManyRequests.content[
    'application/json'
  ].examples.quotaReserved.value.retry_after_seconds
  negative(
    () => validateContract(missingFixedRetryAfter),
    'retry_after_seconds must equal 1 for group_quota_reserved',
  )

  const firstCatalogLine = `| \`${catalog.entries[0].code}\` | 400 | 否 | — | duplicate |`
  const duplicateCatalog = errorCatalogSource.replace(
    '\n## 4. Responses 与 Chat 流错误',
    `\n${firstCatalogLine}\n\n## 4. Responses 与 Chat 流错误`,
  )
  negative(
    () => parseErrorCatalog(duplicateCatalog),
    'Duplicate error code',
  )
  const changedQuotaSemantics = errorCatalogSource.replace(
    '| `group_quota_reserved` | 429 | 是 | **1** |',
    '| `group_quota_reserved` | 500 | 否 | — |',
  )
  negative(
    () => parseErrorCatalog(changedQuotaSemantics),
    'Error catalog semantics changed for group_quota_reserved',
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
  negative(
    () =>
      validateResponsesSse(
        responseErrorFixture.replace(
          /^data: .*$/mu,
          'data: {"type":"response.created","sequence_number":0}',
        ),
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'response.created response is missing',
  )
  negative(
    () =>
      validateResponsesSse(
        `${responseErrorFixture.trim()}\n\nevent: error\ndata: {"type":"error","code":"upstream_stream_error","message":"duplicate","param":null,"sequence_number":4}\n`,
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'contains data after terminal event error',
  )
  negative(
    () =>
      validateResponsesSse(
        responseErrorFixture.replace('resp_0190f911', 'resp_drift'),
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'response.in_progress response id changed within the stream',
  )
  negative(
    () =>
      validateResponsesSse(
        responseErrorFixture.replace('"code":"upstream_stream_error"', '"code":"internal_error"'),
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'ResponseStreamEvent validation failed',
  )
  negative(
    () =>
      validateResponsesSse(
        responseErrorFixture.replace('"message":"上游流在完成前中断。"', '"message":""'),
        responseErrorFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'ResponseStreamEvent validation failed',
  )

  const firstByteTimeoutFixtureName = 'responses-stream-first-byte-timeout.sse'
  const firstByteTimeoutFixture = await readFile(
    path.join(contractPaths.fixtures, firstByteTimeoutFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateResponsesSse(
        firstByteTimeoutFixture.replace(
          'upstream_first_byte_timeout',
          'upstream_stream_error',
        ),
        firstByteTimeoutFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'has the wrong terminal code',
  )

  const responseCompletedFixtureName = 'responses-stream-completed.sse'
  const responseCompletedFixture = await readFile(
    path.join(contractPaths.fixtures, responseCompletedFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateResponsesSse(
        responseCompletedFixture.replace(',"usage":null', ''),
        responseCompletedFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'ResponseStreamEvent validation failed',
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

  const controlFixtureName = 'control-plane-validation-error.json'
  const controlFixture = JSON.parse(
    await readFile(path.join(contractPaths.fixtures, controlFixtureName), 'utf8'),
  )
  delete controlFixture.errors
  negative(
    () => validateControlPlaneProblemFixture(controlFixture, controlFixtureName, catalog),
    'validation_failed must contain field errors',
  )

  const chatFixtureName = 'chat-completions-error.sse'
  const chatFixture = await readFile(path.join(contractPaths.fixtures, chatFixtureName), 'utf8')
  negative(
    () => validateChatSse(`${chatFixture.trim()}\n\ndata: [DONE]\n`, chatFixtureName, catalog.codes),
    'Chat error must be last',
  )
  negative(
    () =>
      validateChatSse(
        chatFixture.replace('"message":"上游流在完成前中断。",', ''),
        chatFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'ChatCompletionErrorEvent validation failed',
  )

  const chatTextFixtureName = 'chat-completions-text.sse'
  const chatTextFixture = await readFile(
    path.join(contractPaths.fixtures, chatTextFixtureName),
    'utf8',
  )
  const chatTextFrames = chatTextFixture.trim().split(/\n\n/u)
  negative(
    () =>
      validateChatSse(
        [chatTextFrames[0], chatTextFrames[3], chatTextFrames[1], chatTextFrames[2], chatTextFrames[4]].join('\n\n'),
        chatTextFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'usage chunk must follow the finish chunk',
  )
  negative(
    () =>
      validateChatSse(
        chatTextFixture.replace('chatcmpl_0190f921', 'chatcmpl_drift'),
        chatTextFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'Chat id changed within the stream',
  )
  negative(
    () =>
      validateChatSse(
        chatTextFixture.replace(',"usage":null', ''),
        chatTextFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'include_usage business and finish chunks must contain usage: null',
  )

  const chatNoUsageFixtureName = 'chat-completions-text-no-usage.sse'
  const chatNoUsageFixture = await readFile(
    path.join(contractPaths.fixtures, chatNoUsageFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateChatSse(
        chatNoUsageFixture.replace(
          '\n\ndata: [DONE]',
          `\n\n${chatTextFrames[3].replace('chatcmpl_0190f921', 'chatcmpl_0190f922')}\n\ndata: [DONE]`,
        ),
        chatNoUsageFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'must not contain a usage chunk',
  )
  negative(
    () =>
      validateChatSse(
        chatNoUsageFixture,
        'chat-completions-text-no-usage-copy.sse',
        validateSchema,
        catalog.codes,
      ),
    'include_usage business and finish chunks must contain usage: null',
  )

  const chatFunctionFixtureName = 'chat-completions-function-call.sse'
  const chatFunctionFixture = await readFile(
    path.join(contractPaths.fixtures, chatFunctionFixtureName),
    'utf8',
  )
  negative(
    () =>
      validateChatSse(
        chatFunctionFixture.replace(
          '"arguments":"{\\"city\\":\\"上海\\"}"',
          '"arguments":"not-json"',
        ),
        chatFunctionFixtureName,
        validateSchema,
        catalog.codes,
      ),
    'arguments is not valid JSON',
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
  const responseErrorClass = generatedClassBlock(generatedCSharp, 'ResponseErrorEvent')
  const poolResponseErrorClass = generatedClassBlock(generatedCSharp, 'PoolResponseErrorEvent')
  const responseCreatedClass = generatedClassBlock(generatedCSharp, 'ResponseCreatedEventResponse')
  const responseCompletedClass = generatedClassBlock(
    generatedCSharp,
    'ResponseCompletedEventResponse',
  )
  invariant(
    responseErrorClass.includes('public required string? Code { get; init; }') &&
      poolResponseErrorClass.includes('public required string Code { get; init; }') &&
      !poolResponseErrorClass.includes('public required string? Code { get; init; }'),
    'C# allOf property intersections must remove null when a same-name overlay requires a non-null string.',
  )
  invariant(
    responseCreatedClass.includes('public required JsonElement? Usage { get; init; }') &&
      responseCompletedClass.includes('public required OpenAIUsage Usage { get; init; }'),
    'C# allOf property intersections must preserve explicit null and retain the concrete non-null usage type.',
  )

  const nullableIntersectionOpenApi = structuredClone(openApi)
  nullableIntersectionOpenApi.components.schemas.CSharpNullableIntersectionProbe = {
    allOf: [
      {
        type: 'object',
        required: ['value'],
        properties: { value: { type: ['string', 'null'] } },
      },
      {
        type: 'object',
        properties: {
          value: { anyOf: [{ type: 'string' }, { type: 'null' }] },
        },
      },
    ],
  }
  const nullableIntersectionClass = generatedClassBlock(
    generateCSharp(nullableIntersectionOpenApi, openApiSource, errorCatalogSource),
    'CSharpNullableIntersectionProbe',
  )
  invariant(
    nullableIntersectionClass.includes('public required string? Value { get; init; }'),
    'C# allOf property intersections must preserve null when every same-name schema permits null.',
  )
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

  const generatedErrors = generateTypeScriptErrors(catalog, openApiSource, errorCatalogSource)
  invariant(
    !generatedErrors.includes('"**1**"') && !generatedErrors.includes('retryable: "是"'),
    'Generated TypeScript contracts must not expose Markdown catalog presentation values.',
  )

  return { negativeCases, deterministicGenerators: 2 }
}
