import { ContractFailure, invariant } from './context.mjs'
import { parseStableErrorSemantics, validateContractCompatibility } from './compatibility.mjs'

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

export function runCompatibilitySelfTests({ errorCatalogSource, openApi }) {
  const validate = (headOpenApi, headErrorCatalogSource = errorCatalogSource) =>
    validateContractCompatibility({
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: openApi,
      headErrorCatalogSource,
      headOpenApi,
    })

  let additiveCases = 0
  let breakingCases = 0
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

  const deletedProperty = structuredClone(openApi)
  delete deletedProperty.components.schemas.ControlPlaneProblem.properties.detail
  breaking(deletedProperty, 'existing property was removed or renamed', errorCatalogSource)

  const changedType = structuredClone(openApi)
  changedType.components.schemas.ControlPlaneProblem.properties.detail.type = 'integer'
  breaking(changedType, 'type changed', errorCatalogSource)

  const tightenedConstraint = structuredClone(openApi)
  tightenedConstraint.components.schemas.AggregateTokenCount.maxLength = 77
  breaking(tightenedConstraint, 'maxLength tightened', errorCatalogSource)

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

  return { additiveCases, breakingCases }
}
