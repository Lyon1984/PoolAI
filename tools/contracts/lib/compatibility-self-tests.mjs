import { ContractFailure, invariant, sha256 } from './context.mjs'
import {
  parseCompatibilityResetRegistry,
  parseStableErrorSemantics,
  resolveCompatibilityReset,
  validateCompatibilityResetAdrHistory,
  validateCompatibilityResetDecisionSource,
  validateCompatibilityResetHistory,
  validateContractCompatibility,
  validateHeadOpenApiSource,
} from './compatibility.mjs'

function expectBreakingChange(action, expectedMessage) {
  try {
    action()
  } catch (error) {
    invariant(error instanceof ContractFailure, `Expected ContractFailure, received ${error.name}.`)
    invariant(
      error.message.includes(expectedMessage),
      `Compatibility self-test expected "${expectedMessage}", received "${error.message}".`,
    )
    return
  }
  throw new Error(`Compatibility self-test did not reject: ${expectedMessage}`)
}

function firstOperation(openApi) {
  for (const [route, pathItem] of Object.entries(openApi.paths)) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (operation?.operationId !== undefined) {
        return { method, operation, route }
      }
    }
  }
  throw new Error('Compatibility self-test could not find an operation.')
}

function operationPointer({ method, route }) {
  const escapedRoute = route.replaceAll('~', '~0').replaceAll('/', '~1')
  return `#/paths/${escapedRoute}/${method}`
}

function makeResetRegistry({
  allowedFailures,
  baseOpenApiSource,
  baseRef,
  headOpenApiSource,
}) {
  return JSON.stringify(
    {
      schemaVersion: 1,
      resets: [
        {
          id: 'm1-e1-pre-release-mailbox-and-list-query',
          status: 'accepted',
          scope: 'pre-external-release-v1',
          baseRef,
          baseOpenApiSha256: sha256(baseOpenApiSource),
          headOpenApiSha256: sha256(headOpenApiSource),
          adr: 'docs/architecture/adr/9999-compatibility-self-test.md',
          approvalIssue: 'https://github.com/Lyon1984/PoolAI/issues/44',
          allowedFailures: [...allowedFailures].sort(),
        },
      ],
    },
    null,
    2,
  )
}

function firstInlineResponseSchema(openApi, predicate) {
  for (const pathItem of Object.values(openApi.paths)) {
    for (const operation of Object.values(pathItem)) {
      if (operation?.operationId === undefined) {
        continue
      }
      for (const response of Object.values(operation.responses ?? {})) {
        if (response?.$ref !== undefined) {
          continue
        }
        for (const media of Object.values(response?.content ?? {})) {
          const schema = media?.schema
          if (schema !== undefined && schema.$ref === undefined && predicate(schema)) {
            return schema
          }
        }
      }
    }
  }
  throw new Error('Compatibility self-test could not find a matching inline response schema.')
}

function withInlineRequestSchema(openApi, required) {
  const result = structuredClone(openApi)
  result.paths['/compatibility-request-direction-self-test'] = {
    post: {
      operationId: 'compatibilityRequestDirectionSelfTest',
      requestBody: {
        required: true,
        content: {
          'application/json': {
            schema: {
              type: 'object',
              additionalProperties: false,
              properties: { value: { type: 'string' } },
              ...(required ? { required: ['value'] } : {}),
            },
          },
        },
      },
      responses: { 204: { description: 'Self-test only' } },
    },
  }
  return result
}

function addStableErrorCode(source) {
  const heading = '\n## 4. Responses 与 Chat 流错误'
  invariant(source.includes(heading), 'Compatibility self-test could not find error catalog section 4.')
  return source.replace(
    heading,
    '\n| `compatibility_self_test` | 400 | 否 | — | 兼容性自测新增错误。 |\n\n## 4. Responses 与 Chat 流错误',
  )
}

function removeStableErrorCode(source, code) {
  const lines = source.split(/\r?\n/u)
  const filtered = lines.filter((line) => !line.startsWith(`| \`${code}\` |`))
  invariant(filtered.length === lines.length - 1, `Compatibility self-test could not remove ${code}.`)
  return filtered.join('\n')
}

export function runCompatibilitySelfTests({
  compatibilityResetSource,
  errorCatalogSource,
  openApi,
  openApiSource,
}) {
  const validate = (headOpenApi, headErrorCatalogSource = errorCatalogSource) =>
    validateContractCompatibility({
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: openApi,
      headErrorCatalogSource,
      headOpenApi,
    })

  let additiveCases = 0
  let breakingCases = 0
  let resetCases = 0
  const additive = (headOpenApi, headErrorCatalogSource) => {
    validate(headOpenApi, headErrorCatalogSource)
    additiveCases += 1
  }
  const breaking = (headOpenApi, expectedMessage, headErrorCatalogSource) => {
    expectBreakingChange(() => validate(headOpenApi, headErrorCatalogSource), expectedMessage)
    breakingCases += 1
  }

  const newPath = structuredClone(openApi)
  newPath.paths['/compatibility-self-test'] = {
    get: {
      operationId: 'compatibilitySelfTest',
      responses: { 204: { description: 'Self-test only' } },
    },
  }
  additive(newPath, errorCatalogSource)

  const optionalProperty = structuredClone(openApi)
  optionalProperty.components.schemas.ControlPlaneProblem.properties.compatibility_note = {
    type: 'string',
  }
  additive(optionalProperty, errorCatalogSource)

  additive(structuredClone(openApi), addStableErrorCode(errorCatalogSource))

  const deletedPath = structuredClone(openApi)
  const first = firstOperation(deletedPath)
  delete deletedPath.paths[first.route]
  breaking(deletedPath, 'existing path was removed or renamed', errorCatalogSource)

  const renamedOperation = structuredClone(openApi)
  firstOperation(renamedOperation).operation.operationId = 'renamedCompatibilityOperation'
  breaking(renamedOperation, 'operationId changed', errorCatalogSource)

  const addedResponseStatus = structuredClone(openApi)
  firstOperation(addedResponseStatus).operation.responses['418'] = {
    description: 'Compatibility self-test only',
  }
  breaking(
    addedResponseStatus,
    'new response status was added to an existing operation',
    errorCatalogSource,
  )

  const addedStatusOperation = firstOperation(addedResponseStatus)
  const addedStatusFailure =
    `${operationPointer(addedStatusOperation)}/responses/418: ` +
    'new response status was added to an existing operation'
  const resetResult = validateContractCompatibility({
    allowedFailures: [addedStatusFailure],
    baseErrorCatalogSource: errorCatalogSource,
    baseOpenApi: openApi,
    headErrorCatalogSource: errorCatalogSource,
    headOpenApi: addedResponseStatus,
  })
  invariant(resetResult.waivedFailures === 1, 'Exact compatibility reset did not consume one failure.')
  resetCases += 1

  expectBreakingChange(
    () =>
      validateContractCompatibility({
        allowedFailures: [addedStatusFailure],
        baseErrorCatalogSource: errorCatalogSource,
        baseOpenApi: openApi,
        headErrorCatalogSource: errorCatalogSource,
        headOpenApi: structuredClone(openApi),
      }),
    'Compatibility reset mismatch',
  )
  resetCases += 1

  const extraResetFailure = structuredClone(addedResponseStatus)
  firstOperation(extraResetFailure).operation.operationId = 'compatibilityResetExtraFailure'
  expectBreakingChange(
    () =>
      validateContractCompatibility({
        allowedFailures: [addedStatusFailure],
        baseErrorCatalogSource: errorCatalogSource,
        baseOpenApi: openApi,
        headErrorCatalogSource: errorCatalogSource,
        headOpenApi: extraResetFailure,
      }),
    'Compatibility reset mismatch',
  )
  resetCases += 1

  const deletedProperty = structuredClone(openApi)
  delete deletedProperty.components.schemas.ControlPlaneProblem.properties.detail
  breaking(deletedProperty, 'existing property was removed or renamed', errorCatalogSource)

  const changedType = structuredClone(openApi)
  changedType.components.schemas.ControlPlaneProblem.properties.detail.type = 'integer'
  breaking(changedType, 'type changed', errorCatalogSource)

  const tightenedConstraint = structuredClone(openApi)
  tightenedConstraint.components.schemas.AggregateTokenCount.maxLength = 77
  breaking(tightenedConstraint, 'maxLength tightened', errorCatalogSource)

  const uniqueItemsConstraint = structuredClone(openApi)
  uniqueItemsConstraint.components.schemas.ApiKeyCreateRequest.properties.allowed_cidrs.uniqueItems = true
  breaking(uniqueItemsConstraint, 'uniqueItems tightened', errorCatalogSource)

  const uniqueItemsResponseRemoval = structuredClone(openApi)
  delete uniqueItemsResponseRemoval.components.schemas.ApiKey.properties.allowed_cidrs.uniqueItems
  breaking(uniqueItemsResponseRemoval, 'uniqueItems response guarantee was removed', errorCatalogSource)

  const requiredProperty = structuredClone(openApi)
  requiredProperty.components.schemas.ControlPlaneProblem.properties.compatibility_note = {
    type: 'string',
  }
  requiredProperty.components.schemas.ControlPlaneProblem.required.push('compatibility_note')
  breaking(requiredProperty, 'became required', errorCatalogSource)

  const requiredRequestBase = withInlineRequestSchema(openApi, true)
  const optionalRequestHead = withInlineRequestSchema(openApi, false)
  validateContractCompatibility({
    baseErrorCatalogSource: errorCatalogSource,
    baseOpenApi: requiredRequestBase,
    headErrorCatalogSource: errorCatalogSource,
    headOpenApi: optionalRequestHead,
  })
  additiveCases += 1

  expectBreakingChange(
    () =>
      validateContractCompatibility({
        baseErrorCatalogSource: errorCatalogSource,
        baseOpenApi: optionalRequestHead,
        headErrorCatalogSource: errorCatalogSource,
        headOpenApi: requiredRequestBase,
      }),
    'became required',
  )
  breakingCases += 1

  const responseRequiredAddition = structuredClone(openApi)
  const responseAdditionSchema = firstInlineResponseSchema(
    responseRequiredAddition,
    (schema) => Array.isArray(schema.required) && schema.required.length > 0,
  )
  responseAdditionSchema.properties.compatibility_note = { type: 'string' }
  responseAdditionSchema.required.push('compatibility_note')
  additive(responseRequiredAddition, errorCatalogSource)

  const responseRequiredRemoval = structuredClone(openApi)
  const responseRemovalSchema = firstInlineResponseSchema(
    responseRequiredRemoval,
    (schema) => Array.isArray(schema.required) && schema.required.length > 0,
  )
  responseRemovalSchema.required = responseRemovalSchema.required.slice(1)
  breaking(responseRequiredRemoval, 'is no longer required in responses', errorCatalogSource)

  const changedContentMediaType = structuredClone(openApi)
  const contentMediaTypeSchema = firstInlineResponseSchema(
    changedContentMediaType,
    (schema) => schema.contentMediaType !== undefined,
  )
  contentMediaTypeSchema.contentMediaType = 'application/json'
  breaking(changedContentMediaType, 'contentMediaType semantics changed', errorCatalogSource)

  const [firstEntry] = parseStableErrorSemantics(errorCatalogSource)
  invariant(firstEntry, 'Compatibility self-test could not find a stable error code.')
  const firstCode = firstEntry.code
  breaking(
    structuredClone(openApi),
    'existing stable error code was removed or renamed',
    removeStableErrorCode(errorCatalogSource, firstCode),
  )

  const changedMeaning = errorCatalogSource.replace(
    new RegExp(`^(\\|\\s*\`${firstCode}\`\\s*\\|[^\\n]*?\\|\\s*)([^|]+?)(\\s*\\|)$`, 'mu'),
    '$1兼容性自测改变语义。$3',
  )
  invariant(changedMeaning !== errorCatalogSource, 'Compatibility self-test could not change error semantics.')
  breaking(
    structuredClone(openApi),
    'existing status, stream, retry, or meaning semantics changed',
    changedMeaning,
  )

  const baseSseFixtures = new Map([
    ['docs/contracts/fixtures/compatibility-self-test.sse', 'data: {"value":"base"}\n\n'],
  ])
  validateContractCompatibility({
    baseErrorCatalogSource: errorCatalogSource,
    baseOpenApi: openApi,
    baseSseFixtures,
    headErrorCatalogSource: errorCatalogSource,
    headOpenApi: structuredClone(openApi),
    headSseFixtures: new Map([
      ...baseSseFixtures,
      ['docs/contracts/fixtures/compatibility-self-test-new.sse', 'data: [DONE]\n\n'],
    ]),
  })
  additiveCases += 1

  expectBreakingChange(
    () =>
      validateContractCompatibility({
        baseErrorCatalogSource: errorCatalogSource,
        baseOpenApi: openApi,
        baseSseFixtures,
        headErrorCatalogSource: errorCatalogSource,
        headOpenApi: structuredClone(openApi),
        headSseFixtures: new Map([
          ['docs/contracts/fixtures/compatibility-self-test.sse', 'data: {"value":"changed"}\n\n'],
        ]),
      }),
    'existing SSE fixture content changed',
  )
  breakingCases += 1

  expectBreakingChange(
    () =>
      validateContractCompatibility({
        baseErrorCatalogSource: errorCatalogSource,
        baseOpenApi: openApi,
        baseSseFixtures,
        headErrorCatalogSource: errorCatalogSource,
        headOpenApi: structuredClone(openApi),
        headSseFixtures: new Map(),
      }),
    'existing SSE fixture was removed',
  )
  breakingCases += 1

  const parsedRegistry = parseCompatibilityResetRegistry(compatibilityResetSource)
  invariant(parsedRegistry.resets.length > 0, 'Compatibility reset registry self-test found no records.')
  resetCases += 1

  const resetBaseRef = 'a'.repeat(40)
  const resetBaseSource = 'base-openapi-source\n'
  const resetHeadSource = 'head-openapi-source\n'
  const resetRegistrySource = makeResetRegistry({
    allowedFailures: [addedStatusFailure],
    baseOpenApiSource: resetBaseSource,
    baseRef: resetBaseRef,
    headOpenApiSource: resetHeadSource,
  })
  const resolvedReset = resolveCompatibilityReset({
    baseOpenApiSource: resetBaseSource,
    baseRef: resetBaseRef,
    headOpenApiSource: resetHeadSource,
    registrySource: resetRegistrySource,
  })
  invariant(
    resolvedReset?.id === 'm1-e1-pre-release-mailbox-and-list-query',
    'Exact compatibility reset was not selected.',
  )
  resetCases += 1

  expectBreakingChange(
    () =>
      resolveCompatibilityReset({
        baseOpenApiSource: resetBaseSource,
        baseRef: resetBaseRef,
        headOpenApiSource: `${resetHeadSource}stale`,
        registrySource: resetRegistrySource,
      }),
    'head OpenAPI SHA-256',
  )
  resetCases += 1

  expectBreakingChange(
    () =>
      resolveCompatibilityReset({
        baseOpenApiSource: `${resetBaseSource}stale`,
        baseRef: resetBaseRef,
        headOpenApiSource: resetHeadSource,
        registrySource: resetRegistrySource,
      }),
    'base OpenAPI SHA-256',
  )
  resetCases += 1

  const unrelatedReset = resolveCompatibilityReset({
    baseOpenApiSource: resetBaseSource,
    baseRef: 'b'.repeat(40),
    headOpenApiSource: resetHeadSource,
    registrySource: resetRegistrySource,
  })
  invariant(unrelatedReset === undefined, 'A compatibility reset leaked to another Git base.')
  resetCases += 1

  const wildcardRegistry = JSON.parse(resetRegistrySource)
  wildcardRegistry.resets[0].allowedFailures = ['#/paths/*: compatibility self-test']
  expectBreakingChange(
    () => parseCompatibilityResetRegistry(JSON.stringify(wildcardRegistry)),
    'must not contain wildcards',
  )
  resetCases += 1

  const unknownKeyRegistry = JSON.parse(resetRegistrySource)
  unknownKeyRegistry.resets[0].allowFutureChanges = false
  expectBreakingChange(
    () => parseCompatibilityResetRegistry(JSON.stringify(unknownKeyRegistry)),
    'must contain exactly these keys',
  )
  resetCases += 1

  const secondResetRegistry = JSON.parse(resetRegistrySource)
  secondResetRegistry.resets.push({
    ...secondResetRegistry.resets[0],
    id: 'm1-e1-pre-release-mailbox-and-list-query-duplicate',
  })
  expectBreakingChange(
    () => parseCompatibilityResetRegistry(JSON.stringify(secondResetRegistry)),
    'must contain exactly one accepted reset',
  )
  resetCases += 1

  const wrongResetIdRegistry = JSON.parse(resetRegistrySource)
  wrongResetIdRegistry.resets[0].id = 'another-pre-release-reset'
  expectBreakingChange(
    () => parseCompatibilityResetRegistry(JSON.stringify(wrongResetIdRegistry)),
    '.id must be m1-e1-pre-release-mailbox-and-list-query',
  )
  resetCases += 1

  const wrongApprovalIssueRegistry = JSON.parse(resetRegistrySource)
  wrongApprovalIssueRegistry.resets[0].approvalIssue =
    'https://github.com/Lyon1984/PoolAI/issues/45'
  expectBreakingChange(
    () => parseCompatibilityResetRegistry(JSON.stringify(wrongApprovalIssueRegistry)),
    '.approvalIssue must be https://github.com/Lyon1984/PoolAI/issues/44',
  )
  resetCases += 1

  const selfTestRegistry = parseCompatibilityResetRegistry(resetRegistrySource)
  const [selfTestReset] = selfTestRegistry.resets
  const selfTestAdrSource = [
    '- Status: **Accepted**',
    `- Reset ID: \`${selfTestReset.id}\``,
    `- Base Git commit: \`${selfTestReset.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${selfTestReset.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${selfTestReset.headOpenApiSha256}\``,
    `- Approval control: [Issue #44](${selfTestReset.approvalIssue})`,
  ].join('\n')
  validateCompatibilityResetDecisionSource(selfTestReset, selfTestAdrSource)
  resetCases += 1

  expectBreakingChange(
    () =>
      validateCompatibilityResetDecisionSource(
        selfTestReset,
        selfTestAdrSource.replace('- Status: **Accepted**\n', ''),
      ),
    'must contain exactly one line',
  )
  resetCases += 1

  validateCompatibilityResetHistory({
    baseRegistrySource: resetRegistrySource,
    headRegistrySource: resetRegistrySource,
  })
  resetCases += 1

  const removedHistoryRegistry = JSON.parse(resetRegistrySource)
  removedHistoryRegistry.resets = []
  expectBreakingChange(
    () =>
      validateCompatibilityResetHistory({
        baseRegistrySource: resetRegistrySource,
        headRegistrySource: JSON.stringify(removedHistoryRegistry),
      }),
    'must contain exactly one accepted reset',
  )
  resetCases += 1

  const changedHistoryRegistry = JSON.parse(resetRegistrySource)
  changedHistoryRegistry.resets[0].allowedFailures = [
    '#/paths/~1changed: compatibility self-test changed',
  ]
  expectBreakingChange(
    () =>
      validateCompatibilityResetHistory({
        baseRegistrySource: resetRegistrySource,
        headRegistrySource: JSON.stringify(changedHistoryRegistry),
      }),
    'sole accepted reset m1-e1-pre-release-mailbox-and-list-query changed',
  )
  resetCases += 1

  const adrBytes = Buffer.from(selfTestAdrSource)
  validateCompatibilityResetAdrHistory({
    baseAdrSources: new Map([[selfTestReset.id, adrBytes]]),
    baseRegistry: selfTestRegistry,
    headAdrSources: new Map([[selfTestReset.id, Buffer.from(adrBytes)]]),
  })
  resetCases += 1

  expectBreakingChange(
    () =>
      validateCompatibilityResetAdrHistory({
        baseAdrSources: new Map([[selfTestReset.id, adrBytes]]),
        baseRegistry: selfTestRegistry,
        headAdrSources: new Map([[selfTestReset.id, Buffer.from(`${selfTestAdrSource}\nchanged`)]]),
      }),
    'accepted ADR',
  )
  resetCases += 1

  validateHeadOpenApiSource({ headOpenApi: openApi, headOpenApiSource: openApiSource })
  resetCases += 1

  const mismatchedHeadOpenApi = structuredClone(openApi)
  mismatchedHeadOpenApi.info.title = 'Compatibility source mismatch self-test'
  expectBreakingChange(
    () =>
      validateHeadOpenApiSource({
        headOpenApi: mismatchedHeadOpenApi,
        headOpenApiSource: openApiSource,
      }),
    'source differs from the validated head OpenAPI document',
  )
  resetCases += 1

  return { additiveCases, breakingCases, resetCases }
}
