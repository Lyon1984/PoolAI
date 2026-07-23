import { addFormats, Ajv2020, ContractFailure, invariant, stableJson } from './context.mjs'

const HTTP_METHODS = new Set([
  'get',
  'put',
  'post',
  'delete',
  'options',
  'head',
  'patch',
  'trace',
])

const ANONYMOUS_CONTROL_OPERATIONS = new Set([
  'login',
  'refreshSession',
  'verifyLoginTotp',
  'requestPasswordReset',
  'resetPassword',
])

const IDEMPOTENCY_EXEMPT_OPERATIONS = new Set([
  ...ANONYMOUS_CONTROL_OPERATIONS,
  'logout',
])

const VERSIONED_ACTION_OPERATIONS = new Set([
  'changeMyPassword',
  'confirmMyTotpSetup',
  'disableMyTotp',
  'adminAdjustGroupQuota',
  'adminResetGroupQuota',
])

const ETAG_EXEMPT_IDEMPOTENT_OPERATIONS = new Set([
  'adminRequestUserPasswordReset',
  'beginMyTotpSetup',
])

const STRICT_ADMIN_LIST_QUERY_OPERATIONS = new Set([
  'adminListGroups',
  'adminListSubscriptionTemplates',
  'adminListSubscriptions',
])

export const API_KEY_TEXT_PATTERN = String.raw`^(?=[\s\S]*[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000])[^\u0000-\u001F\u007F-\u009F\u2028\u2029\uD800-\uDFFF]+$`

const API_KEY_NAME_INPUTS = [
  ['AdminUserApiKeyCreateRequest', 'name'],
  ['AdminUserApiKeyUpdateRequest', 'name'],
  ['ApiKeyCreateRequest', 'name'],
  ['ApiKeyUpdateRequest', 'name'],
]

const API_KEY_REASON_INPUTS = [
  ['AdminUserApiKeyCreateRequest', 'reason'],
  ['AdminUserApiKeyUpdateRequest', 'reason'],
  ['ApiKeyRotateRequest', 'reason'],
]

const API_KEY_REVOKE_OPERATIONS = [
  ['/api/v1/admin/users/{userId}/api-keys/{apiKeyId}', 'delete', 'adminRevokeUserApiKey'],
  ['/api/v1/me/api-keys/{apiKeyId}', 'delete', 'revokeMyApiKey'],
]

const BODY_ERROR_RESPONSE_BINDINGS = {
  control: {
    '413': {
      baseSchema: 'ControlPlaneProblem',
      code: 'payload_too_large',
      mediaType: 'application/problem+json',
      responseComponent: 'PayloadTooLarge',
    },
    '415': {
      baseSchema: 'ControlPlaneProblem',
      code: 'unsupported_media_type',
      mediaType: 'application/problem+json',
      responseComponent: 'UnsupportedMediaType',
    },
  },
  gateway: {
    '413': {
      baseSchema: 'GatewayProblem',
      code: 'payload_too_large',
      mediaType: 'application/json',
      responseComponent: 'GatewayPayloadTooLarge',
    },
    '415': {
      baseSchema: 'GatewayProblem',
      code: 'unsupported_media_type',
      mediaType: 'application/json',
      responseComponent: 'GatewayUnsupportedMediaType',
    },
  },
}

const SUPPORTED_SCHEMA_KEYWORDS = new Set([
  '$ref',
  'additionalProperties',
  'allOf',
  'anyOf',
  'const',
  'contentMediaType',
  'default',
  'description',
  'discriminator',
  'enum',
  'example',
  'format',
  'if',
  'items',
  'maxItems',
  'maxLength',
  'maximum',
  'minItems',
  'minLength',
  'minProperties',
  'minimum',
  'oneOf',
  'pattern',
  'properties',
  'readOnly',
  'required',
  'then',
  'type',
  'uniqueItems',
  'writeOnly',
])

const SCHEMA_CHILD_ARRAYS = ['allOf', 'anyOf', 'oneOf']
const SCHEMA_CHILDREN = ['if', 'then', 'items', 'additionalProperties']

export function decodeJsonPointer(pointer) {
  invariant(pointer.startsWith('#/'), `Only local JSON Pointer references are supported: ${pointer}`)
  return pointer
    .slice(2)
    .split('/')
    .map((segment) => segment.replaceAll('~1', '/').replaceAll('~0', '~'))
}

export function resolveLocalReference(document, reference) {
  let value = document
  for (const segment of decodeJsonPointer(reference)) {
    invariant(
      value !== null && typeof value === 'object' && segment in value,
      `Broken local reference: ${reference}`,
    )
    value = value[segment]
  }
  return value
}

function walkEveryNode(value, visit, pointer = '#', ancestors = new Set()) {
  if (value === null || typeof value !== 'object') {
    return
  }
  invariant(!ancestors.has(value), `In-memory contract object cycle found at ${pointer}.`)
  const nextAncestors = new Set(ancestors).add(value)
  visit(value, pointer)
  if (Array.isArray(value)) {
    value.forEach((item, index) =>
      walkEveryNode(item, visit, `${pointer}/${index}`, nextAncestors),
    )
    return
  }
  for (const [key, item] of Object.entries(value)) {
    walkEveryNode(item, visit, `${pointer}/${escapePointerSegment(key)}`, nextAncestors)
  }
}

function escapePointerSegment(value) {
  return value.replaceAll('~', '~0').replaceAll('/', '~1')
}

function validateReferences(openApi) {
  let referenceCount = 0
  walkEveryNode(openApi, (node, pointer) => {
    if (!('$ref' in node)) {
      return
    }
    invariant(typeof node.$ref === 'string', `$ref must be a string at ${pointer}.`)
    invariant(node.$ref.startsWith('#/'), `External $ref is forbidden at ${pointer}: ${node.$ref}`)
    resolveLocalReference(openApi, node.$ref)
    referenceCount += 1
  })
  invariant(referenceCount > 0, 'OpenAPI contains no local references.')
  return referenceCount
}

function walkSchema(schema, pointer, visit) {
  if (schema === true || schema === false) {
    return
  }
  invariant(
    schema !== null && typeof schema === 'object' && !Array.isArray(schema),
    `Schema must be an object or boolean at ${pointer}.`,
  )
  visit(schema, pointer)

  for (const keyword of Object.keys(schema)) {
    invariant(
      SUPPORTED_SCHEMA_KEYWORDS.has(keyword),
      `Unsupported schema keyword ${keyword} at ${pointer}; update the validator and generators explicitly.`,
    )
  }

  for (const keyword of SCHEMA_CHILD_ARRAYS) {
    const children = schema[keyword]
    if (children === undefined) {
      continue
    }
    invariant(Array.isArray(children) && children.length > 0, `${keyword} must be non-empty at ${pointer}.`)
    children.forEach((child, index) => walkSchema(child, `${pointer}/${keyword}/${index}`, visit))
  }

  for (const keyword of SCHEMA_CHILDREN) {
    const child = schema[keyword]
    if (child === undefined || typeof child === 'boolean') {
      continue
    }
    walkSchema(child, `${pointer}/${keyword}`, visit)
  }

  if (schema.properties !== undefined) {
    invariant(
      schema.properties !== null &&
        typeof schema.properties === 'object' &&
        !Array.isArray(schema.properties),
      `properties must be an object at ${pointer}.`,
    )
    for (const [name, child] of Object.entries(schema.properties)) {
      walkSchema(child, `${pointer}/properties/${escapePointerSegment(name)}`, visit)
    }
  }
}

function validateSupportedSchemas(openApi) {
  const schemas = openApi.components?.schemas
  invariant(schemas && typeof schemas === 'object', 'OpenAPI components.schemas is required.')
  let count = 0
  for (const [name, schema] of Object.entries(schemas)) {
    walkSchema(schema, `#/components/schemas/${name}`, () => {
      count += 1
    })
  }
  invariant(count >= 100, `Expected a substantial frozen schema set; found ${count} schema nodes.`)
  return count
}

function dereferenceResponse(openApi, response) {
  if (response?.$ref) {
    return resolveLocalReference(openApi, response.$ref)
  }
  return response
}

function dereferenceParameter(openApi, parameter) {
  if (parameter?.$ref) {
    return resolveLocalReference(openApi, parameter.$ref)
  }
  return parameter
}

function parameterKey(parameter) {
  const name = parameter.in === 'header' ? parameter.name.toLowerCase() : parameter.name
  return `${parameter.in}:${name}`
}

function collectParameters(openApi, parameters, scope) {
  const result = new Map()
  for (const parameterOrReference of parameters ?? []) {
    const parameter = dereferenceParameter(openApi, parameterOrReference)
    invariant(
      parameter && typeof parameter.name === 'string' && typeof parameter.in === 'string',
      `${scope} contains an invalid parameter.`,
    )
    const key = parameterKey(parameter)
    invariant(!result.has(key), `${scope} repeats parameter ${parameter.in}:${parameter.name}.`)
    result.set(key, parameter)
  }
  return result
}

function getHeaderParameter(parameters, name) {
  return parameters.get(`header:${name.toLowerCase()}`)
}

function successfulResponses(openApi, operation) {
  return Object.entries(operation.responses)
    .filter(([status]) => /^2[0-9][0-9]$/u.test(status))
    .map(([status, response]) => [status, dereferenceResponse(openApi, response)])
}

function responseHasHeader(response, headerName) {
  return Object.keys(response?.headers ?? {}).some(
    (name) => name.toLowerCase() === headerName.toLowerCase(),
  )
}

function schemaReferences(openApi, schema, componentName, visited = new Set()) {
  if (!schema || typeof schema !== 'object') {
    return false
  }
  if (schema.$ref) {
    if (schema.$ref === `#/components/schemas/${componentName}`) {
      return true
    }
    if (visited.has(schema.$ref)) {
      return false
    }
    return schemaReferences(
      openApi,
      resolveLocalReference(openApi, schema.$ref),
      componentName,
      new Set(visited).add(schema.$ref),
    )
  }
  return ['allOf', 'anyOf', 'oneOf'].some((keyword) =>
    (schema[keyword] ?? []).some((candidate) =>
      schemaReferences(openApi, candidate, componentName, visited),
    ),
  )
}

function validateBodyErrorComponents(openApi) {
  for (const bindings of Object.values(BODY_ERROR_RESPONSE_BINDINGS)) {
    for (const [status, binding] of Object.entries(bindings)) {
      const response = openApi.components?.responses?.[binding.responseComponent]
      const expectedSchemaRef = `#/components/schemas/${binding.baseSchema}`
      invariant(
        response?.content?.[binding.mediaType]?.schema?.$ref === expectedSchemaRef,
        `${binding.responseComponent} must reference ${expectedSchemaRef}.`,
      )
      invariant(
        response?.['x-http-status'] === Number(status),
        `${binding.responseComponent} must bind x-http-status ${status}.`,
      )
      invariant(
        response?.['x-error-code'] === binding.code,
        `${binding.responseComponent} must bind x-error-code ${binding.code}.`,
      )
    }
  }
}

function validateApiKeyTextSchema(schema, maxLength, pointer) {
  invariant(
    schema?.type === 'string' &&
      schema.minLength === 1 &&
      schema.maxLength === maxLength &&
      schema.pattern === API_KEY_TEXT_PATTERN,
    `${pointer} must use the exact API Key Unicode scalar text contract (1..${maxLength}).`,
  )
}

function validateApiKeyTextInputs(openApi) {
  for (const [schemaName, propertyName] of API_KEY_NAME_INPUTS) {
    validateApiKeyTextSchema(
      openApi.components?.schemas?.[schemaName]?.properties?.[propertyName],
      100,
      `#/components/schemas/${schemaName}/properties/${propertyName}`,
    )
  }

  for (const [schemaName, propertyName] of API_KEY_REASON_INPUTS) {
    validateApiKeyTextSchema(
      openApi.components?.schemas?.[schemaName]?.properties?.[propertyName],
      500,
      `#/components/schemas/${schemaName}/properties/${propertyName}`,
    )
  }

  const sharedChangeReason = openApi.components?.parameters?.XChangeReason
  invariant(
    sharedChangeReason?.schema?.pattern === String.raw`.*\S.*`,
    '#/components/parameters/XChangeReason must remain on its existing shared contract.',
  )

  const apiKeyChangeReason = openApi.components?.parameters?.ApiKeyChangeReason
  invariant(
    apiKeyChangeReason?.name === 'X-Change-Reason' &&
      apiKeyChangeReason.in === 'header' &&
      apiKeyChangeReason.required === true,
    '#/components/parameters/ApiKeyChangeReason must be the required X-Change-Reason header.',
  )
  validateApiKeyTextSchema(
    apiKeyChangeReason?.schema,
    500,
    '#/components/parameters/ApiKeyChangeReason/schema',
  )

  const expectedReference = '#/components/parameters/ApiKeyChangeReason'
  for (const [route, method, operationId] of API_KEY_REVOKE_OPERATIONS) {
    const operation = openApi.paths?.[route]?.[method]
    invariant(
      operation?.operationId === operationId,
      `${method.toUpperCase()} ${route} must remain operation ${operationId}.`,
    )
    const references = (operation.parameters ?? [])
      .map((parameter) => parameter?.$ref)
      .filter((reference) => reference === expectedReference)
    invariant(
      references.length === 1,
      `${operationId} must reference ${expectedReference} exactly once.`,
    )
    invariant(
      !(operation.parameters ?? []).some(
        (parameter) => parameter?.$ref === '#/components/parameters/XChangeReason',
      ),
      `${operationId} must not tighten the shared XChangeReason parameter.`,
    )
  }
}

function validateErrorProjection(openApi, operationId, path, status, response, expectedMediaType) {
  const media = response?.content?.[expectedMediaType]
  invariant(media?.schema, `${operationId} ${status} has no ${expectedMediaType} error schema.`)
  const expectedSchema = path.startsWith('/api/v1/') ? 'ControlPlaneProblem' : 'GatewayProblem'
  invariant(
    schemaReferences(openApi, media.schema, expectedSchema),
    `${operationId} ${status} must project ${expectedSchema}.`,
  )
  const forbiddenSchema = expectedSchema === 'ControlPlaneProblem' ? 'GatewayProblem' : 'ControlPlaneProblem'
  invariant(
    !schemaReferences(openApi, media.schema, forbiddenSchema),
    `${operationId} ${status} mixes forbidden ${forbiddenSchema} into its error projection.`,
  )
}

function usesOnlySecurityScheme(security, schemeName) {
  return (
    Array.isArray(security) &&
    security.length === 1 &&
    security[0] &&
    typeof security[0] === 'object' &&
    !Array.isArray(security[0]) &&
    Object.keys(security[0]).length === 1 &&
    Object.hasOwn(security[0], schemeName) &&
    Array.isArray(security[0][schemeName]) &&
    security[0][schemeName].length === 0
  )
}

function validateOperations(openApi) {
  const operationIds = new Set()
  let operationCount = 0

  for (const [path, pathItem] of Object.entries(openApi.paths ?? {})) {
    invariant(path.startsWith('/'), `OpenAPI path must start with /: ${path}`)
    const pathParameters = collectParameters(openApi, pathItem.parameters, `Path item ${path}`)
    const templateParameters = [...path.matchAll(/\{([^{}]+)\}/gu)].map((match) => match[1])
    invariant(
      new Set(templateParameters).size === templateParameters.length,
      `Path template ${path} repeats a template parameter.`,
    )
    for (const [method, operation] of Object.entries(pathItem)) {
      if (!HTTP_METHODS.has(method)) {
        continue
      }
      operationCount += 1
      invariant(
        typeof operation.operationId === 'string' && operation.operationId.length > 0,
        `${method.toUpperCase()} ${path} is missing operationId.`,
      )
      invariant(
        !operationIds.has(operation.operationId),
        `Duplicate operationId ${operation.operationId}.`,
      )
      operationIds.add(operation.operationId)
      invariant(
        operation.responses && Object.keys(operation.responses).length > 0,
        `${operation.operationId} has no responses.`,
      )

      if (path.startsWith('/v1/')) {
        invariant(
          usesOnlySecurityScheme(operation.security, 'PoolApiKey'),
          `${operation.operationId} must declare only PoolApiKey security.`,
        )
      } else if (ANONYMOUS_CONTROL_OPERATIONS.has(operation.operationId)) {
        invariant(
          Array.isArray(operation.security) && operation.security.length === 0,
          `${operation.operationId} must explicitly declare security: [].`,
        )
      } else if (path.startsWith('/api/v1/')) {
        invariant(
          usesOnlySecurityScheme(operation.security, 'UserJwt'),
          `${operation.operationId} must declare only UserJwt security.`,
        )
      }

      if (operation['x-required-roles'] !== undefined) {
        const roles = operation['x-required-roles']
        invariant(Array.isArray(roles) && roles.length > 0, `${operation.operationId} roles must be non-empty.`)
        invariant(
          roles.every((role) => ['user', 'auditor', 'operator', 'admin'].includes(role)),
          `${operation.operationId} declares an unknown required role.`,
        )
        invariant(new Set(roles).size === roles.length, `${operation.operationId} repeats a required role.`)
      }

      if (path.startsWith('/api/v1/') && !ANONYMOUS_CONTROL_OPERATIONS.has(operation.operationId)) {
        invariant(
          Array.isArray(operation['x-required-roles']) && operation['x-required-roles'].length > 0,
          `${operation.operationId} must declare x-required-roles.`,
        )
      }

      const operationParameters = collectParameters(
        openApi,
        operation.parameters,
        `Operation ${operation.operationId}`,
      )
      const effectiveParameters = new Map(pathParameters)
      for (const [key, parameter] of operationParameters) {
        effectiveParameters.set(key, parameter)
      }
      const actualPathParameters = [...effectiveParameters.values()].filter(
        (parameter) => parameter.in === 'path',
      )
      invariant(
        actualPathParameters.every((parameter) => parameter.required === true),
        `${operation.operationId} path parameters must set required=true.`,
      )
      const actualPathNames = actualPathParameters.map((parameter) => parameter.name).sort()
      invariant(
        JSON.stringify(actualPathNames) === JSON.stringify([...templateParameters].sort()),
        `${operation.operationId} path parameters must exactly match template ${path}.`,
      )

      const isControlMutation =
        path.startsWith('/api/v1/') && ['post', 'put', 'patch', 'delete'].includes(method)
      const requiresIdempotency =
        isControlMutation && !IDEMPOTENCY_EXEMPT_OPERATIONS.has(operation.operationId)
      if (requiresIdempotency) {
        const idempotency = getHeaderParameter(effectiveParameters, 'Idempotency-Key')
        invariant(
          idempotency?.required === true && idempotency.in === 'header',
          `${operation.operationId} must require Idempotency-Key.`,
        )
        invariant(
          Object.hasOwn(operation.responses, '409'),
          `${operation.operationId} must declare 409 idempotency_conflict.`,
        )
      }

      if (operation.requestBody !== undefined) {
        invariant(
          Object.hasOwn(operation.responses, '400'),
          `${operation.operationId} with a request body must declare 400 invalid_request.`,
        )
        invariant(
          Object.hasOwn(operation.responses, '413'),
          `${operation.operationId} with a request body must declare 413 payload_too_large.`,
        )
        invariant(
          Object.hasOwn(operation.responses, '415'),
          `${operation.operationId} with a request body must declare 415 unsupported_media_type.`,
        )
        const scope = path.startsWith('/v1/') ? 'gateway' : 'control'
        for (const [status, binding] of Object.entries(BODY_ERROR_RESPONSE_BINDINGS[scope])) {
          const expectedReference = `#/components/responses/${binding.responseComponent}`
          invariant(
            operation.responses[status]?.$ref === expectedReference,
            `${operation.operationId} ${status} must reference ${expectedReference} for ${binding.code}.`,
          )
        }
      }

      if (STRICT_ADMIN_LIST_QUERY_OPERATIONS.has(operation.operationId)) {
        invariant(
          operation.responses['400']?.$ref === '#/components/responses/BadRequest',
          `${operation.operationId} must declare 400 through #/components/responses/BadRequest for invalid query input.`,
        )
      }

      const requiresIfMatch =
        path.startsWith('/api/v1/') &&
        (['patch', 'delete'].includes(method) || VERSIONED_ACTION_OPERATIONS.has(operation.operationId))
      if (requiresIfMatch) {
        const ifMatch = getHeaderParameter(effectiveParameters, 'If-Match')
        invariant(
          ifMatch?.required === true && ifMatch.in === 'header',
          `${operation.operationId} must require If-Match.`,
        )
      }

      const successes = successfulResponses(openApi, operation)
      invariant(successes.length > 0, `${operation.operationId} has no explicit 2xx response.`)
      if (
        requiresIfMatch ||
        (requiresIdempotency && !ETAG_EXEMPT_IDEMPOTENT_OPERATIONS.has(operation.operationId))
      ) {
        invariant(
          successes.every(([, response]) => responseHasHeader(response, 'ETag')),
          `${operation.operationId} successful responses must include ETag.`,
        )
      }

      for (const [status, responseOrReference] of Object.entries(operation.responses)) {
        if (status === 'default' || Number(status) >= 400) {
          const response = dereferenceResponse(openApi, responseOrReference)
          const mediaTypes = Object.keys(response?.content ?? {})
          const expectedMediaType = path.startsWith('/api/v1/')
            ? 'application/problem+json'
            : 'application/json'
          invariant(
            mediaTypes.length === 1 && mediaTypes[0] === expectedMediaType,
            `${operation.operationId} ${status} must use only ${expectedMediaType}; found ${mediaTypes.join(', ') || 'none'}.`,
          )
          validateErrorProjection(
            openApi,
            operation.operationId,
            path,
            status,
            response,
            expectedMediaType,
          )
        }
      }
    }
  }

  invariant(operationCount >= 60, `Expected at least 60 operations; found ${operationCount}.`)
  return operationCount
}

function toJsonSchema(value) {
  if (Array.isArray(value)) {
    return value.map(toJsonSchema)
  }
  if (value === null || typeof value !== 'object') {
    return value
  }

  const result = {}
  for (const [key, child] of Object.entries(value)) {
    if (['description', 'discriminator', 'example', 'readOnly', 'writeOnly'].includes(key)) {
      continue
    }
    if (key === '$ref') {
      invariant(
        child.startsWith('#/components/schemas/'),
        `Only component schema refs can be compiled by AJV: ${child}`,
      )
      result.$ref = `#/$defs/${escapePointerSegment(child.split('/').at(-1))}`
      continue
    }
    if (key === 'properties') {
      result.properties = Object.fromEntries(
        Object.entries(child).map(([name, schema]) => [name, toJsonSchema(schema)]),
      )
      continue
    }
    result[key] = toJsonSchema(child)
  }
  return result
}

export function createSchemaValidator(openApi) {
  const schemas = openApi.components.schemas
  const definitions = Object.fromEntries(
    Object.entries(schemas).map(([name, schema]) => [name, toJsonSchema(schema)]),
  )
  const ajv = new Ajv2020({
    allErrors: true,
    allowUnionTypes: true,
    strict: true,
    // OpenAPI condition branches intentionally require properties declared on their parent object.
    strictRequired: false,
  })
  addFormats(ajv)
  ajv.addFormat('password', true)
  ajv.addFormat('int64', true)
  ajv.addFormat('double', true)

  const validators = new Map()
  const compileSchema = (schemaName) => {
    invariant(schemaName in definitions, `Unknown component schema ${schemaName}.`)
    let validate = validators.get(schemaName)
    if (!validate) {
      validate = ajv.compile({
        $schema: 'https://json-schema.org/draft/2020-12/schema',
        $ref: `#/$defs/${escapePointerSegment(schemaName)}`,
        $defs: definitions,
      })
      validators.set(schemaName, validate)
    }
    return validate
  }
  const validateSchema = (schemaName, value) => {
    const validate = compileSchema(schemaName)
    if (!validate(value)) {
      throw new ContractFailure(
        `${schemaName} validation failed: ${ajv.errorsText(validate.errors, { separator: '; ' })}`,
      )
    }
  }
  validateSchema.validateInline = (schema, value, label) => {
    const validate = ajv.compile({
      $schema: 'https://json-schema.org/draft/2020-12/schema',
      ...toJsonSchema(schema),
      $defs: definitions,
    })
    if (!validate(value)) {
      throw new ContractFailure(
        `${label} validation failed: ${ajv.errorsText(validate.errors, { separator: '; ' })}`,
      )
    }
  }
  for (const name of Object.keys(definitions)) {
    compileSchema(name)
  }
  validateSchema.compiledSchemas = validators.size
  return validateSchema
}

function examplesFromMedia(openApi, media, pointer) {
  const examples = []
  if (Object.hasOwn(media, 'example')) {
    examples.push({ pointer: `${pointer}/example`, value: media.example })
  }
  for (const [name, exampleOrReference] of Object.entries(media.examples ?? {})) {
    const example = exampleOrReference?.$ref
      ? resolveLocalReference(openApi, exampleOrReference.$ref)
      : exampleOrReference
    invariant(
      example && Object.hasOwn(example, 'value') && example.externalValue === undefined,
      `Media example ${pointer}/examples/${escapePointerSegment(name)} must contain an inline value.`,
    )
    examples.push({
      pointer: `${pointer}/examples/${escapePointerSegment(name)}/value`,
      value: example.value,
    })
  }
  return examples
}

function catalogEntry(catalog, code, pointer) {
  invariant(catalog, `Error catalog is required to validate ${pointer}.`)
  const entry = catalog.entries.find((candidate) => candidate.code === code)
  invariant(entry, `${pointer} uses unknown error code ${code}.`)
  return entry
}

function validateProblemExample({ catalog, example, mediaType, operationId, pointer, response, status }) {
  invariant(
    example && typeof example === 'object' && !Array.isArray(example),
    `${pointer} problem example must be an object.`,
  )
  const numericStatus = Number(status)
  invariant(example.status === numericStatus, `${pointer} status must equal response status ${status}.`)
  const entry = catalogEntry(catalog, example.code, pointer)
  invariant(
    entry.httpStatuses.includes(numericStatus),
    `${pointer} error code ${entry.code} does not allow HTTP ${status}.`,
  )
  if (entry.retryable !== null) {
    invariant(
      example.retryable === entry.retryable,
      `${pointer} retryable does not match error catalog code ${entry.code}.`,
    )
  }

  if (entry.retryAfter === 'fixed') {
    invariant(
      example.retry_after_seconds === entry.retryAfterSeconds,
      `${pointer} retry_after_seconds must equal ${entry.retryAfterSeconds} for ${entry.code}.`,
    )
    invariant(responseHasHeader(response, 'Retry-After'), `${operationId} ${status} must declare Retry-After.`)
  } else if (entry.retryAfter === 'required') {
    invariant(
      Number.isSafeInteger(example.retry_after_seconds) && example.retry_after_seconds > 0,
      `${pointer} must declare a positive retry_after_seconds for ${entry.code}.`,
    )
    invariant(responseHasHeader(response, 'Retry-After'), `${operationId} ${status} must declare Retry-After.`)
  } else if (entry.retryAfter === 'optional') {
    invariant(
      example.retry_after_seconds === undefined ||
        (Number.isSafeInteger(example.retry_after_seconds) && example.retry_after_seconds > 0),
      `${pointer} retry_after_seconds must be a positive integer when present.`,
    )
  } else {
    invariant(
      example.retry_after_seconds === undefined,
      `${pointer} must omit retry_after_seconds for ${entry.code}.`,
    )
  }

  if (mediaType === 'application/problem+json') {
    invariant(example.error === undefined, `${pointer} ControlPlaneProblem must not contain error.`)
  } else {
    invariant(example.error && typeof example.error === 'object', `${pointer} GatewayProblem must contain error.`)
    invariant(example.code === example.error.code, `${pointer} outer code must equal error.code.`)
    invariant(example.detail === example.error.message, `${pointer} detail must equal error.message.`)
  }
  if (entry.code === 'validation_failed') {
    invariant(
      example.errors && typeof example.errors === 'object' && Object.keys(example.errors).length > 0,
      `${pointer} validation_failed must contain field errors.`,
    )
  }
}

function validateMediaExamples(openApi, validateSchema, catalog) {
  let exampleCount = 0
  for (const [kind, containers] of [
    ['requestBodies', openApi.components?.requestBodies],
    ['responses', openApi.components?.responses],
  ]) {
    for (const [name, containerOrReference] of Object.entries(containers ?? {})) {
      const container = containerOrReference?.$ref
        ? resolveLocalReference(openApi, containerOrReference.$ref)
        : containerOrReference
      for (const [mediaType, media] of Object.entries(container?.content ?? {})) {
        for (const example of examplesFromMedia(
          openApi,
          media,
          `#/components/${kind}/${escapePointerSegment(name)}/content/${escapePointerSegment(mediaType)}`,
        )) {
          invariant(media.schema, `${example.pointer} has no media schema.`)
          validateSchema.validateInline(media.schema, example.value, example.pointer)
          if (
            kind === 'responses' &&
            example.value &&
            typeof example.value === 'object' &&
            Number.isInteger(example.value.status) &&
            typeof example.value.code === 'string'
          ) {
            validateProblemExample({
              catalog,
              example: example.value,
              mediaType,
              operationId: `components.responses.${name}`,
              pointer: example.pointer,
              response: container,
              status: String(example.value.status),
            })
          }
          exampleCount += 1
        }
      }
    }
  }

  for (const [path, pathItem] of Object.entries(openApi.paths ?? {})) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (!HTTP_METHODS.has(method)) {
        continue
      }

      const requestBody = operation.requestBody?.$ref
        ? resolveLocalReference(openApi, operation.requestBody.$ref)
        : operation.requestBody
      for (const [mediaType, media] of Object.entries(requestBody?.content ?? {})) {
        for (const example of examplesFromMedia(openApi, media, `#/paths/${escapePointerSegment(path)}/${method}/requestBody/content/${escapePointerSegment(mediaType)}`)) {
          invariant(media.schema, `${example.pointer} has no media schema.`)
          validateSchema.validateInline(media.schema, example.value, example.pointer)
          exampleCount += 1
        }
      }

      for (const [status, responseOrReference] of Object.entries(operation.responses ?? {})) {
        const response = dereferenceResponse(openApi, responseOrReference)
        for (const [mediaType, media] of Object.entries(response?.content ?? {})) {
          for (const example of examplesFromMedia(openApi, media, `#/paths/${escapePointerSegment(path)}/${method}/responses/${escapePointerSegment(status)}/content/${escapePointerSegment(mediaType)}`)) {
            invariant(media.schema, `${example.pointer} has no media schema.`)
            validateSchema.validateInline(media.schema, example.value, example.pointer)
            if ((status === 'default' || Number(status) >= 400) && status !== 'default') {
              validateProblemExample({
                catalog,
                example: example.value,
                mediaType,
                operationId: operation.operationId,
                pointer: example.pointer,
                response,
                status,
              })
            }
            exampleCount += 1
          }
        }
      }
    }
  }
  return exampleCount
}

function validateEmbeddedExamples(openApi, validateSchema, catalog) {
  let exampleCount = 0
  for (const [name, schema] of Object.entries(openApi.components.schemas)) {
    walkSchema(schema, `#/components/schemas/${name}`, (node, pointer) => {
      if (!('example' in node)) {
        return
      }
      validateSchema.validateInline(node, node.example, pointer)
      exampleCount += 1
      invariant(node.example !== undefined, `Undefined example at ${pointer}.`)
    })
  }

  // Compile every root component even when it has no embedded example.
  for (const [name, schema] of Object.entries(openApi.components.schemas)) {
    if ('example' in schema) {
      validateSchema(name, schema.example)
    }
  }
  return exampleCount + validateMediaExamples(openApi, validateSchema, catalog)
}

export function validateOpenApi(openApi, catalog = null) {
  invariant(openApi.openapi === '3.1.0', `Expected OpenAPI 3.1.0; found ${openApi.openapi}.`)
  invariant(openApi.info?.version === '1.0.0', `Expected API version 1.0.0; found ${openApi.info?.version}.`)
  if (openApi.jsonSchemaDialect !== undefined) {
    invariant(
      openApi.jsonSchemaDialect === 'https://json-schema.org/draft/2020-12/schema',
      `Unsupported jsonSchemaDialect ${openApi.jsonSchemaDialect}.`,
    )
  }

  const references = validateReferences(openApi)
  const schemaNodes = validateSupportedSchemas(openApi)
  validateBodyErrorComponents(openApi)
  validateApiKeyTextInputs(openApi)
  const operations = validateOperations(openApi)
  const validateSchema = createSchemaValidator(openApi)
  const examples = validateEmbeddedExamples(openApi, validateSchema, catalog)

  return {
    compiledSchemas: validateSchema.compiledSchemas,
    examples,
    operations,
    references,
    schemaNodes,
    validateSchema,
  }
}

export function schemasEquivalent(left, right) {
  return stableJson(left) === stableJson(right)
}
