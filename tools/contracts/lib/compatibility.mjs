import { execFile } from 'node:child_process'
import { readFileSync, realpathSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { promisify, TextDecoder } from 'node:util'

import { withReadOnlyRepositoryFile } from '../../../eng/policies/repository-file.mjs'
import { ContractFailure, invariant, repoRoot, sha256, stableJson, YAML } from './context.mjs'

const execFileAsync = promisify(execFile)
const HTTP_METHODS = new Set(['delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'trace'])
const ERROR_TABLE_ROW =
  /^\|\s*`([a-z][a-z0-9_]*)`\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|/u
const DOCUMENTATION_KEYS = new Set([
  'description',
  'example',
  'examples',
  'externalDocs',
  'summary',
])
const COMPATIBILITY_RESET_RELATIVE_PATH = 'docs/contracts/compatibility-resets-v1.json'
const EXACT_RESET_APPROVAL_ISSUE = 'https://github.com/Lyon1984/PoolAI/issues/44'
const EXACT_RESET_ID = 'm1-e1-pre-release-mailbox-and-list-query'
const RESET_REGISTRY_KEYS = ['resets', 'schemaVersion']
const RESET_KEYS = [
  'adr',
  'allowedFailures',
  'approvalIssue',
  'baseOpenApiSha256',
  'baseRef',
  'headOpenApiSha256',
  'id',
  'scope',
  'status',
]

function escapePointerSegment(value) {
  return value.replaceAll('~', '~0').replaceAll('/', '~1')
}

function sameValue(left, right) {
  return stableJson(left) === stableJson(right)
}

function withoutDocumentation(value) {
  if (Array.isArray(value)) {
    return value.map(withoutDocumentation)
  }
  if (value === null || typeof value !== 'object') {
    return value
  }

  return Object.fromEntries(
    Object.entries(value)
      .filter(([key]) => !DOCUMENTATION_KEYS.has(key))
      .map(([key, item]) => [key, withoutDocumentation(item)]),
  )
}

function sameSemantics(left, right) {
  return sameValue(withoutDocumentation(left), withoutDocumentation(right))
}

function requireExactKeys(value, keys, label) {
  invariant(
    value !== null && typeof value === 'object' && !Array.isArray(value),
    `${label} must be an object.`,
  )
  const actual = Object.keys(value).sort()
  const expected = [...keys].sort()
  invariant(
    sameValue(actual, expected),
    `${label} must contain exactly these keys: ${expected.join(', ')}.`,
  )
}

export function parseCompatibilityResetRegistry(source) {
  invariant(typeof source === 'string', 'Compatibility reset registry source is required.')
  let json
  try {
    json = JSON.parse(source)
  } catch (error) {
    throw new ContractFailure(`Compatibility reset registry is not valid JSON: ${error.message}`)
  }
  const document = YAML.parseDocument(source, {
    prettyErrors: true,
    strict: true,
    uniqueKeys: true,
  })
  invariant(
    document.errors.length === 0,
    `Compatibility reset registry is invalid: ${document.errors
      .map((error) => error.message)
      .join('; ')}`,
  )
  invariant(
    sameValue(json, document.toJS({ maxAliasCount: 0 })),
    'Compatibility reset registry JSON and strict parser results differ.',
  )
  requireExactKeys(json, RESET_REGISTRY_KEYS, 'Compatibility reset registry')
  invariant(json.schemaVersion === 1, 'Compatibility reset registry must use schemaVersion 1.')
  invariant(Array.isArray(json.resets), 'Compatibility reset registry resets must be an array.')
  invariant(
    json.resets.length === 1,
    'Compatibility reset registry schemaVersion 1 must contain exactly one accepted reset.',
  )

  const ids = new Set()
  const baseRefs = new Set()
  for (const [index, reset] of json.resets.entries()) {
    const label = `Compatibility reset registry resets[${index}]`
    requireExactKeys(reset, RESET_KEYS, label)
    invariant(
      typeof reset.id === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(reset.id),
      `${label}.id must be lower kebab-case.`,
    )
    invariant(reset.id === EXACT_RESET_ID, `${label}.id must be ${EXACT_RESET_ID}.`)
    invariant(!ids.has(reset.id), `${label}.id duplicates ${reset.id}.`)
    ids.add(reset.id)
    invariant(reset.status === 'accepted', `${label}.status must be accepted.`)
    invariant(
      reset.scope === 'pre-external-release-v1',
      `${label}.scope must be pre-external-release-v1.`,
    )
    invariant(
      typeof reset.baseRef === 'string' && /^[0-9a-f]{40}$/u.test(reset.baseRef),
      `${label}.baseRef must be an exact lowercase 40-character Git SHA.`,
    )
    invariant(!baseRefs.has(reset.baseRef), `${label}.baseRef duplicates ${reset.baseRef}.`)
    baseRefs.add(reset.baseRef)
    for (const key of ['baseOpenApiSha256', 'headOpenApiSha256']) {
      invariant(
        typeof reset[key] === 'string' && /^[0-9a-f]{64}$/u.test(reset[key]),
        `${label}.${key} must be an exact lowercase SHA-256 digest.`,
      )
    }
    const adrName = typeof reset.adr === 'string' ? path.basename(reset.adr) : ''
    invariant(
      reset.adr === `docs/architecture/adr/${adrName}` &&
        /^[0-9]{4}-[a-z0-9]+(?:-[a-z0-9]+)*[.]md$/u.test(adrName),
      `${label}.adr must name one repository ADR.`,
    )
    const issueParts = typeof reset.approvalIssue === 'string'
      ? reset.approvalIssue.split('/')
      : []
    invariant(
      issueParts.length === 7 && issueParts[0] === 'https:' && issueParts[1] === '' &&
        issueParts[2] === 'github.com' && /^[A-Za-z0-9_.-]+$/u.test(issueParts[3]) &&
        /^[A-Za-z0-9_.-]+$/u.test(issueParts[4]) && issueParts[5] === 'issues' &&
        /^[0-9]+$/u.test(issueParts[6]),
      `${label}.approvalIssue must be one exact GitHub Issue URL.`,
    )
    invariant(
      reset.approvalIssue === EXACT_RESET_APPROVAL_ISSUE,
      `${label}.approvalIssue must be ${EXACT_RESET_APPROVAL_ISSUE}.`,
    )
    invariant(
      Array.isArray(reset.allowedFailures) && reset.allowedFailures.length > 0,
      `${label}.allowedFailures must be a non-empty array.`,
    )
    const sortedFailures = [...reset.allowedFailures].sort()
    invariant(
      sameValue(reset.allowedFailures, sortedFailures),
      `${label}.allowedFailures must be sorted.`,
    )
    invariant(
      new Set(reset.allowedFailures).size === reset.allowedFailures.length,
      `${label}.allowedFailures must not contain duplicates.`,
    )
    for (const [failureIndex, failure] of reset.allowedFailures.entries()) {
      invariant(
        typeof failure === 'string' && failure.startsWith('#/') &&
          failure.includes(': ') && !failure.includes('\n') && !failure.includes('\r'),
        `${label}.allowedFailures[${failureIndex}] must be one exact OpenAPI diagnostic.`,
      )
      invariant(
        !failure.includes('*') && !failure.includes('?'),
        `${label}.allowedFailures[${failureIndex}] must not contain wildcards.`,
      )
    }
  }

  return json
}

export function validateCompatibilityResetDecisionSource(reset, source) {
  invariant(typeof source === 'string', `Compatibility reset ${reset.id} ADR source is required.`)
  const requiredLines = [
    '- Status: **Accepted**',
    `- Reset ID: \`${reset.id}\``,
    `- Base Git commit: \`${reset.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${reset.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${reset.headOpenApiSha256}\``,
    `- Approval control: [Issue #44](${reset.approvalIssue})`,
  ]
  const lines = source.split(/\r?\n/u)
  for (const requiredLine of requiredLines) {
    invariant(
      lines.filter((line) => line === requiredLine).length === 1,
      `Compatibility reset ${reset.id} accepted ADR must contain exactly one line: ${requiredLine}`,
    )
  }
}

function readCompatibilityResetDecision(reset) {
  let result
  try {
    const adrRoot = `${realpathSync(path.resolve(repoRoot, 'docs/architecture/adr'))}${path.sep}`
    result = withReadOnlyRepositoryFile(repoRoot, reset.adr, (descriptor, canonical) => {
      invariant(
        canonical.startsWith(adrRoot),
        `Compatibility reset ${reset.id} ADR escaped its canonical directory.`,
      )
      return readFileSync(descriptor)
    })
  } catch (error) {
    throw new ContractFailure(
      `Compatibility reset ${reset.id} cannot safely read accepted ADR ${reset.adr}: ${error.message}`,
    )
  }
  let source
  try {
    source = new TextDecoder('utf-8', { fatal: true }).decode(result)
  } catch (error) {
    throw new ContractFailure(
      `Compatibility reset ${reset.id} accepted ADR must be valid UTF-8: ${error.message}`,
    )
  }
  validateCompatibilityResetDecisionSource(reset, source)
  return result
}

export function validateCompatibilityResetDecisions(registrySource) {
  const registry = parseCompatibilityResetRegistry(registrySource)
  const adrSources = new Map(
    registry.resets.map((reset) => [reset.id, readCompatibilityResetDecision(reset)]),
  )
  return { adrSources, registry }
}

export function validateCompatibilityResetHistory({
  baseRegistrySource,
  headRegistrySource,
}) {
  const headRegistry = parseCompatibilityResetRegistry(headRegistrySource)
  if (baseRegistrySource === undefined) {
    return { baseRegistry: undefined, headRegistry }
  }

  const baseRegistry = parseCompatibilityResetRegistry(baseRegistrySource)
  const [baseReset] = baseRegistry.resets
  invariant(
    sameValue(baseReset, headRegistry.resets[0]),
    `Compatibility reset history is immutable; sole accepted reset ${baseReset.id} changed.`,
  )
  return { baseRegistry, headRegistry }
}

export function validateCompatibilityResetAdrHistory({
  baseAdrSources,
  baseRegistry,
  headAdrSources,
}) {
  invariant(baseAdrSources instanceof Map, 'Base compatibility reset ADR sources must be a Map.')
  invariant(headAdrSources instanceof Map, 'Head compatibility reset ADR sources must be a Map.')
  for (const reset of baseRegistry.resets) {
    const baseSource = baseAdrSources.get(reset.id)
    const headSource = headAdrSources.get(reset.id)
    invariant(
      Buffer.isBuffer(baseSource) && Buffer.isBuffer(headSource) && headSource.equals(baseSource),
      `Compatibility reset history is immutable; accepted ADR ${reset.adr} changed.`,
    )
  }
}

export function resolveCompatibilityReset({
  baseOpenApiSource,
  baseRef,
  headOpenApiSource,
  registrySource,
}) {
  invariant(
    typeof baseRef === 'string' && /^[0-9a-f]{40}$/u.test(baseRef),
    'Compatibility reset baseRef must be an exact lowercase 40-character Git SHA.',
  )
  invariant(typeof baseOpenApiSource === 'string', 'Compatibility reset base OpenAPI source is required.')
  invariant(typeof headOpenApiSource === 'string', 'Compatibility reset head OpenAPI source is required.')
  const registry = parseCompatibilityResetRegistry(registrySource)
  const reset = registry.resets.find((entry) => entry.baseRef === baseRef)
  if (reset === undefined) {
    return undefined
  }

  const baseDigest = sha256(baseOpenApiSource)
  const headDigest = sha256(headOpenApiSource)
  invariant(
    baseDigest === reset.baseOpenApiSha256,
    `Compatibility reset mismatch for ${reset.id}: base OpenAPI SHA-256 is ${baseDigest}, expected ${reset.baseOpenApiSha256}.`,
  )
  invariant(
    headDigest === reset.headOpenApiSha256,
    `Compatibility reset mismatch for ${reset.id}: head OpenAPI SHA-256 is ${headDigest}, expected ${reset.headOpenApiSha256}.`,
  )
  return reset
}

function plainCell(value) {
  return value.replaceAll('**', '').replaceAll('`', '').trim()
}

function parseCatalogStatus(value, code, line) {
  const text = plainCell(value)
  if (text === 'SSE') {
    return { httpStatuses: [], stream: true }
  }

  const match = /^(\d{3})(?:\s+或\s+SSE)?$/u.exec(text)
  invariant(match, `Invalid HTTP/SSE status ${value.trim()} for ${code} at catalog line ${line}.`)
  const status = Number(match[1])
  invariant(
    status >= 400 && status <= 599,
    `Error code ${code} must use a 4xx/5xx status at catalog line ${line}.`,
  )
  return { httpStatuses: [status], stream: text.endsWith('或 SSE') }
}

function parseCatalogRetryable(value, code, line) {
  const text = plainCell(value)
  if (text === '是') {
    return true
  }
  if (text === '否') {
    return false
  }
  invariant(text === '视情况', `Invalid retryable value ${value.trim()} for ${code} at catalog line ${line}.`)
  return null
}

function parseCatalogRetryAfter(value, code, line) {
  const text = plainCell(value)
  if (text === '—') {
    return { retryAfter: 'none', retryAfterSeconds: null }
  }
  if (text === '必须') {
    return { retryAfter: 'required', retryAfterSeconds: null }
  }
  if (text === '可选') {
    return { retryAfter: 'optional', retryAfterSeconds: null }
  }

  invariant(/^\d+$/u.test(text), `Invalid Retry-After value ${value.trim()} for ${code} at catalog line ${line}.`)
  const seconds = Number(text)
  invariant(
    Number.isSafeInteger(seconds) && seconds > 0,
    `Retry-After seconds for ${code} must be a positive safe integer at catalog line ${line}.`,
  )
  return { retryAfter: 'fixed', retryAfterSeconds: seconds }
}

export function parseStableErrorSemantics(source) {
  const entries = []
  const seen = new Set()
  let inStableSection = false

  for (const [index, line] of source.split(/\r?\n/u).entries()) {
    if (line === '## 3. 稳定错误码') {
      inStableSection = true
      continue
    }
    if (inStableSection && line.startsWith('## ')) {
      break
    }
    if (!inStableSection) {
      continue
    }

    const match = ERROR_TABLE_ROW.exec(line)
    if (!match) {
      continue
    }

    const [, code, statusText, retryableText, retryAfterText, meaningText] = match
    invariant(!seen.has(code), `Duplicate stable error code ${code} at catalog line ${index + 1}.`)
    seen.add(code)
    entries.push({
      code,
      ...parseCatalogStatus(statusText, code, index + 1),
      retryable: parseCatalogRetryable(retryableText, code, index + 1),
      ...parseCatalogRetryAfter(retryAfterText, code, index + 1),
      meaning: meaningText.trim(),
    })
  }

  invariant(inStableSection, 'Error catalog is missing the stable error-code section.')
  invariant(entries.length > 0, 'Error catalog stable error-code section contains no entries.')
  return entries
}

function addFailure(failures, pointer, message) {
  failures.push(`${pointer}: ${message}`)
}

function compareReference(baseValue, headValue, pointer, failures) {
  const baseReference = baseValue?.$ref
  const headReference = headValue?.$ref
  if (baseReference !== headReference && (baseReference !== undefined || headReference !== undefined)) {
    addFailure(
      failures,
      pointer,
      `reference changed from ${baseReference ?? '<inline>'} to ${headReference ?? '<inline>'}`,
    )
    return false
  }
  return true
}

function compareType(baseSchema, headSchema, pointer, failures) {
  const normalizedType = (value) =>
    Array.isArray(value) ? value.map((item) => stableJson(item)).sort() : value
  if (!sameValue(normalizedType(baseSchema.type), normalizedType(headSchema.type))) {
    addFailure(
      failures,
      `${pointer}/type`,
      `type changed from ${stableJson(baseSchema.type)} to ${stableJson(headSchema.type)}`,
    )
  }
}

function compareEnum(baseSchema, headSchema, pointer, failures, direction) {
  const baseValues = baseSchema.enum?.map((value) => stableJson(value))
  const headValues = headSchema.enum?.map((value) => stableJson(value))

  if (direction !== 'response') {
    if (baseValues === undefined && headValues !== undefined) {
      addFailure(failures, `${pointer}/enum`, 'an enum constraint was added to accepted input')
    } else if (baseValues !== undefined && headValues !== undefined) {
      const headSet = new Set(headValues)
      for (const value of baseValues) {
        if (!headSet.has(value)) {
          addFailure(failures, `${pointer}/enum`, `existing accepted enum value ${value} was removed`)
        }
      }
    }
  }

  if (direction !== 'request') {
    if (baseValues !== undefined && headValues === undefined) {
      addFailure(failures, `${pointer}/enum`, 'the response enum guarantee was removed')
    } else if (baseValues !== undefined && headValues !== undefined) {
      const baseSet = new Set(baseValues)
      for (const value of headValues) {
        if (!baseSet.has(value)) {
          addFailure(failures, `${pointer}/enum`, `new response enum value ${value} may be emitted`)
        }
      }
    }
  }
}

function compareConst(baseSchema, headSchema, pointer, failures, direction) {
  const baseValue = baseSchema.const
  const headValue = headSchema.const

  if (direction !== 'response' && baseValue === undefined && headValue !== undefined) {
    addFailure(failures, `${pointer}/const`, 'a const constraint was added to accepted input')
  }
  if (direction !== 'request' && baseValue !== undefined && headValue === undefined) {
    addFailure(failures, `${pointer}/const`, 'the response const guarantee was removed')
  }
  if (baseValue !== undefined && headValue !== undefined && !sameValue(baseValue, headValue)) {
    addFailure(
      failures,
      `${pointer}/const`,
      `const changed from ${stableJson(baseValue)} to ${stableJson(headValue)}`,
    )
  }
}

function compareBounds(baseSchema, headSchema, pointer, failures, direction) {
  for (const keyword of ['minimum', 'minItems', 'minLength', 'minProperties']) {
    const baseValue = baseSchema[keyword]
    const headValue = headSchema[keyword]
    if (
      direction !== 'response' &&
      headValue !== undefined &&
      (baseValue === undefined || headValue > baseValue)
    ) {
      addFailure(
        failures,
        `${pointer}/${keyword}`,
        `${keyword} tightened from ${baseValue ?? '<none>'} to ${headValue}`,
      )
    }
    if (
      direction !== 'request' &&
      baseValue !== undefined &&
      (headValue === undefined || headValue < baseValue)
    ) {
      addFailure(
        failures,
        `${pointer}/${keyword}`,
        `${keyword} response guarantee relaxed from ${baseValue} to ${headValue ?? '<none>'}`,
      )
    }
  }

  for (const keyword of ['maximum', 'maxItems', 'maxLength']) {
    const baseValue = baseSchema[keyword]
    const headValue = headSchema[keyword]
    if (
      direction !== 'response' &&
      headValue !== undefined &&
      (baseValue === undefined || headValue < baseValue)
    ) {
      addFailure(
        failures,
        `${pointer}/${keyword}`,
        `${keyword} tightened from ${baseValue ?? '<none>'} to ${headValue}`,
      )
    }
    if (
      direction !== 'request' &&
      baseValue !== undefined &&
      (headValue === undefined || headValue > baseValue)
    ) {
      addFailure(
        failures,
        `${pointer}/${keyword}`,
        `${keyword} response guarantee relaxed from ${baseValue} to ${headValue ?? '<none>'}`,
      )
    }
  }
}

function comparePatternAndFormat(baseSchema, headSchema, pointer, failures, direction) {
  for (const keyword of ['format', 'pattern']) {
    const baseValue = baseSchema[keyword]
    const headValue = headSchema[keyword]
    const requestIncompatible =
      direction !== 'response' && headValue !== undefined && headValue !== baseValue
    const responseIncompatible =
      direction !== 'request' && baseValue !== undefined && headValue !== baseValue
    if (requestIncompatible || responseIncompatible) {
      addFailure(
        failures,
        `${pointer}/${keyword}`,
        `${keyword} changed from ${baseValue ?? '<none>'} to ${headValue}`,
      )
    }
  }
}

function compareRequired(baseSchema, headSchema, pointer, failures, direction) {
  const baseRequired = new Set(baseSchema.required ?? [])
  const headRequired = new Set(headSchema.required ?? [])

  if (direction !== 'response') {
    for (const property of headRequired) {
      if (!baseRequired.has(property)) {
        addFailure(failures, `${pointer}/required`, `property ${property} became required`)
      }
    }
  }
  if (direction !== 'request') {
    for (const property of baseRequired) {
      if (!headRequired.has(property)) {
        addFailure(
          failures,
          `${pointer}/required`,
          `property ${property} is no longer required in responses`,
        )
      }
    }
  }
}

function compareProperties(baseSchema, headSchema, pointer, failures, direction) {
  const headProperties = headSchema.properties ?? {}
  for (const [name, baseProperty] of Object.entries(baseSchema.properties ?? {})) {
    const propertyPointer = `${pointer}/properties/${escapePointerSegment(name)}`
    if (!Object.hasOwn(headProperties, name)) {
      addFailure(failures, propertyPointer, 'existing property was removed or renamed')
      continue
    }
    compareSchema(baseProperty, headProperties[name], propertyPointer, failures, direction)
  }
}

function compareAdditionalProperties(baseSchema, headSchema, pointer, failures, direction) {
  const baseValue = baseSchema.additionalProperties ?? true
  const headValue = headSchema.additionalProperties ?? true
  if (sameValue(baseValue, headValue)) {
    return
  }

  if (direction === 'both') {
    addFailure(
      failures,
      `${pointer}/additionalProperties`,
      'shared-component additionalProperties semantics changed',
    )
    return
  }

  if (direction === 'request') {
    if (headValue === true || baseValue === false) {
      return
    }
    if (baseValue === true) {
      addFailure(failures, `${pointer}/additionalProperties`, 'additional properties became constrained')
      return
    }
    if (headValue === false) {
      addFailure(failures, `${pointer}/additionalProperties`, 'additional properties are no longer accepted')
      return
    }
    compareSchema(baseValue, headValue, `${pointer}/additionalProperties`, failures, direction)
    return
  }

  if (headValue === false || baseValue === true) {
    return
  }
  if (baseValue === false || headValue === true) {
    addFailure(
      failures,
      `${pointer}/additionalProperties`,
      'responses may now contain additional properties',
    )
    return
  }
  compareSchema(baseValue, headValue, `${pointer}/additionalProperties`, failures, direction)
}

function compareItems(baseSchema, headSchema, pointer, failures, direction) {
  if (baseSchema.items === undefined) {
    if (headSchema.items !== undefined && direction !== 'response') {
      addFailure(failures, `${pointer}/items`, 'an item constraint was added')
    }
    return
  }
  if (headSchema.items === undefined) {
    if (direction !== 'request') {
      addFailure(failures, `${pointer}/items`, 'the response item schema was removed')
    }
    return
  }
  compareSchema(baseSchema.items, headSchema.items, `${pointer}/items`, failures, direction)
}

function compatibleAlternative(baseAlternative, headAlternative, pointer, direction) {
  const candidateFailures = []
  compareSchema(baseAlternative, headAlternative, pointer, candidateFailures, direction)
  return candidateFailures.length === 0
}

function matchAlternatives(
  baseAlternatives,
  headAlternatives,
  pointer,
  failures,
  sourceDirection,
  schemaDirection,
) {
  const source = sourceDirection === 'base-to-head' ? baseAlternatives : headAlternatives
  const candidates = sourceDirection === 'base-to-head' ? headAlternatives : baseAlternatives
  const used = new Set()

  for (const [index, alternative] of source.entries()) {
    let matchIndex = candidates.findIndex(
      (candidate, candidateIndex) =>
        !used.has(candidateIndex) &&
        (sourceDirection === 'base-to-head'
          ? compatibleAlternative(
              alternative,
              candidate,
              `${pointer}/${index}`,
              schemaDirection,
            )
          : compatibleAlternative(
              candidate,
              alternative,
              `${pointer}/${index}`,
              schemaDirection,
            )),
    )
    if (matchIndex === -1) {
      matchIndex = candidates.findIndex(
        (candidate, candidateIndex) =>
          !used.has(candidateIndex) && sameValue(alternative, candidate),
      )
    }
    if (matchIndex === -1) {
      addFailure(failures, `${pointer}/${index}`, 'existing schema alternative was removed or tightened')
      continue
    }
    used.add(matchIndex)
  }
}

function compareComposition(baseSchema, headSchema, pointer, failures, direction) {
  for (const keyword of ['anyOf', 'oneOf']) {
    const baseAlternatives = baseSchema[keyword]
    const headAlternatives = headSchema[keyword]
    if (baseAlternatives === undefined) {
      if (headAlternatives !== undefined && direction !== 'response') {
        addFailure(failures, `${pointer}/${keyword}`, `${keyword} constraints were added`)
      }
      continue
    }
    if (headAlternatives === undefined) {
      if (direction !== 'request') {
        addFailure(failures, `${pointer}/${keyword}`, `${keyword} response constraints were removed`)
      }
      continue
    }

    if (direction === 'request') {
      matchAlternatives(
        baseAlternatives,
        headAlternatives,
        `${pointer}/${keyword}`,
        failures,
        'base-to-head',
        direction,
      )
    } else if (direction === 'response') {
      matchAlternatives(
        baseAlternatives,
        headAlternatives,
        `${pointer}/${keyword}`,
        failures,
        'head-to-base',
        direction,
      )
    } else {
      if (baseAlternatives.length !== headAlternatives.length) {
        addFailure(
          failures,
          `${pointer}/${keyword}`,
          `shared-component ${keyword} alternatives changed`,
        )
      } else {
        matchAlternatives(
          baseAlternatives,
          headAlternatives,
          `${pointer}/${keyword}`,
          failures,
          'base-to-head',
          direction,
        )
      }
    }
  }

  const baseAllOf = baseSchema.allOf
  const headAllOf = headSchema.allOf
  if (baseAllOf === undefined) {
    if (headAllOf !== undefined && direction !== 'response') {
      addFailure(failures, `${pointer}/allOf`, 'allOf constraints were added')
    }
  } else if (headAllOf === undefined) {
    if (direction !== 'request') {
      addFailure(failures, `${pointer}/allOf`, 'allOf response constraints were removed')
    }
  } else if (direction === 'request') {
    if (headAllOf.length > baseAllOf.length) {
      addFailure(failures, `${pointer}/allOf`, 'allOf constraints were added')
    } else {
      matchAlternatives(
        baseAllOf,
        headAllOf,
        `${pointer}/allOf`,
        failures,
        'head-to-base',
        direction,
      )
    }
  } else if (direction === 'response') {
    if (headAllOf.length < baseAllOf.length) {
      addFailure(failures, `${pointer}/allOf`, 'allOf response constraints were removed')
    } else {
      matchAlternatives(
        baseAllOf,
        headAllOf,
        `${pointer}/allOf`,
        failures,
        'base-to-head',
        direction,
      )
    }
  } else if (headAllOf.length !== baseAllOf.length) {
    addFailure(failures, `${pointer}/allOf`, 'shared-component allOf constraints changed')
  } else {
    matchAlternatives(
      baseAllOf,
      headAllOf,
      `${pointer}/allOf`,
      failures,
      'base-to-head',
      direction,
    )
  }

  for (const keyword of ['if', 'then']) {
    const baseValue = baseSchema[keyword]
    const headValue = headSchema[keyword]
    if (sameValue(baseValue, headValue)) {
      continue
    }
    if (baseValue === undefined && headValue !== undefined) {
      if (direction !== 'response') {
        addFailure(failures, `${pointer}/${keyword}`, `${keyword} constraints were added`)
      }
    } else if (baseValue !== undefined && headValue === undefined) {
      if (direction !== 'request') {
        addFailure(failures, `${pointer}/${keyword}`, `${keyword} response constraints were removed`)
      }
    } else {
      addFailure(failures, `${pointer}/${keyword}`, `${keyword} semantics changed`)
    }
  }
}

function compareSchema(baseSchema, headSchema, pointer, failures, direction = 'request') {
  if (sameValue(baseSchema, headSchema)) {
    return
  }
  if (headSchema === undefined) {
    addFailure(failures, pointer, 'existing schema was removed')
    return
  }
  if (typeof baseSchema === 'boolean' || typeof headSchema === 'boolean') {
    if (direction !== 'response' && baseSchema === true && headSchema !== true) {
      addFailure(failures, pointer, 'an unconstrained input schema became constrained')
    } else if (direction !== 'response' && baseSchema !== false && headSchema === false) {
      addFailure(failures, pointer, 'input schema now rejects every value')
    }
    if (direction !== 'request' && baseSchema === false && headSchema !== false) {
      addFailure(failures, pointer, 'response schema may now emit values')
    } else if (direction !== 'request' && baseSchema !== true && headSchema === true) {
      addFailure(failures, pointer, 'response schema became unconstrained')
    }
    return
  }

  compareReference(baseSchema, headSchema, pointer, failures)
  compareType(baseSchema, headSchema, pointer, failures)
  compareEnum(baseSchema, headSchema, pointer, failures, direction)
  compareConst(baseSchema, headSchema, pointer, failures, direction)
  compareBounds(baseSchema, headSchema, pointer, failures, direction)
  comparePatternAndFormat(baseSchema, headSchema, pointer, failures, direction)
  compareRequired(baseSchema, headSchema, pointer, failures, direction)
  compareProperties(baseSchema, headSchema, pointer, failures, direction)
  compareAdditionalProperties(baseSchema, headSchema, pointer, failures, direction)
  compareItems(baseSchema, headSchema, pointer, failures, direction)
  compareComposition(baseSchema, headSchema, pointer, failures, direction)

  for (const keyword of ['contentMediaType', 'default', 'discriminator']) {
    if (!sameValue(baseSchema[keyword], headSchema[keyword])) {
      addFailure(failures, `${pointer}/${keyword}`, `${keyword} semantics changed`)
    }
  }
  for (const keyword of ['readOnly', 'writeOnly']) {
    if ((baseSchema[keyword] === true) !== (headSchema[keyword] === true)) {
      addFailure(failures, `${pointer}/${keyword}`, `${keyword} semantics changed`)
    }
  }
}

function resolveLocalReference(document, value) {
  let current = value
  const seen = new Set()
  while (current?.$ref !== undefined) {
    const reference = current.$ref
    invariant(reference.startsWith('#/'), `Only local references can be compared: ${reference}`)
    invariant(!seen.has(reference), `Reference cycle found while comparing ${reference}.`)
    seen.add(reference)
    current = reference
      .slice(2)
      .split('/')
      .map((segment) => segment.replaceAll('~1', '/').replaceAll('~0', '~'))
      .reduce((node, segment) => node?.[segment], document)
    invariant(current !== undefined, `Broken local reference while comparing ${reference}.`)
  }
  return current
}

function parameterKey(document, parameter) {
  const resolved = resolveLocalReference(document, parameter)
  const name = resolved.in === 'header' ? resolved.name.toLowerCase() : resolved.name
  return `${resolved.in}:${name}`
}

function collectParameters(document, pathItem, operation) {
  return new Map(
    [...(pathItem.parameters ?? []), ...(operation.parameters ?? [])].map((parameter) => [
      parameterKey(document, parameter),
      parameter,
    ]),
  )
}

function compareContent(
  baseDocument,
  headDocument,
  baseContent,
  headContent,
  pointer,
  failures,
  direction,
) {
  for (const [mediaType, baseMedia] of Object.entries(baseContent ?? {})) {
    const mediaPointer = `${pointer}/${escapePointerSegment(mediaType)}`
    const headMedia = headContent?.[mediaType]
    if (headMedia === undefined) {
      addFailure(failures, mediaPointer, 'existing media type was removed')
      continue
    }
    if (
      baseMedia.schema !== undefined &&
      headMedia.schema === undefined &&
      direction !== 'request'
    ) {
      addFailure(failures, `${mediaPointer}/schema`, 'existing media schema was removed')
    } else if (
      baseMedia.schema === undefined &&
      headMedia.schema !== undefined &&
      direction !== 'response'
    ) {
      addFailure(failures, `${mediaPointer}/schema`, 'a media schema constraint was added')
    } else if (baseMedia.schema !== undefined && headMedia.schema !== undefined) {
      compareSchema(
        baseMedia.schema,
        headMedia.schema,
        `${mediaPointer}/schema`,
        failures,
        direction,
      )
    }
    if (!sameSemantics(baseMedia.encoding, headMedia.encoding)) {
      addFailure(failures, `${mediaPointer}/encoding`, 'media encoding semantics changed')
    }
  }
}

function compareParameter(
  baseDocument,
  headDocument,
  baseParameter,
  headParameter,
  pointer,
  failures,
  direction = 'request',
) {
  compareReference(baseParameter, headParameter, pointer, failures)
  const baseResolved = resolveLocalReference(baseDocument, baseParameter)
  const headResolved = resolveLocalReference(headDocument, headParameter)
  if (
    direction !== 'response' &&
    baseResolved.required !== true &&
    headResolved.required === true
  ) {
    addFailure(failures, `${pointer}/required`, 'parameter became required')
  }
  if (
    direction !== 'request' &&
    baseResolved.required === true &&
    headResolved.required !== true
  ) {
    addFailure(failures, `${pointer}/required`, 'response header is no longer guaranteed')
  }
  for (const keyword of ['allowEmptyValue', 'allowReserved', 'explode', 'style']) {
    if (!sameValue(baseResolved[keyword], headResolved[keyword])) {
      addFailure(failures, `${pointer}/${keyword}`, `${keyword} semantics changed`)
    }
  }
  if (
    baseResolved.schema !== undefined &&
    headResolved.schema === undefined &&
    direction !== 'request'
  ) {
    addFailure(failures, `${pointer}/schema`, 'existing parameter schema was removed')
  } else if (
    baseResolved.schema === undefined &&
    headResolved.schema !== undefined &&
    direction !== 'response'
  ) {
    addFailure(failures, `${pointer}/schema`, 'a parameter schema constraint was added')
  } else if (baseResolved.schema !== undefined && headResolved.schema !== undefined) {
    compareSchema(
      baseResolved.schema,
      headResolved.schema,
      `${pointer}/schema`,
      failures,
      direction,
    )
  }
  compareContent(
    baseDocument,
    headDocument,
    baseResolved.content,
    headResolved.content,
    `${pointer}/content`,
    failures,
    direction,
  )
}

function compareParameters(baseDocument, headDocument, basePathItem, headPathItem, baseOperation, headOperation, pointer, failures) {
  const baseParameters = collectParameters(baseDocument, basePathItem, baseOperation)
  const headParameters = collectParameters(headDocument, headPathItem, headOperation)

  for (const [key, baseParameter] of baseParameters) {
    const parameterPointer = `${pointer}/parameters/${escapePointerSegment(key)}`
    const headParameter = headParameters.get(key)
    if (headParameter === undefined) {
      addFailure(failures, parameterPointer, 'existing parameter was removed or renamed')
      continue
    }
    compareParameter(
      baseDocument,
      headDocument,
      baseParameter,
      headParameter,
      parameterPointer,
      failures,
    )
  }

  for (const [key, headParameter] of headParameters) {
    if (baseParameters.has(key)) {
      continue
    }
    const resolved = resolveLocalReference(headDocument, headParameter)
    if (resolved.required === true) {
      addFailure(
        failures,
        `${pointer}/parameters/${escapePointerSegment(key)}`,
        'a new required parameter was added',
      )
    }
  }
}

function compareHeaders(baseDocument, headDocument, baseHeaders, headHeaders, pointer, failures) {
  for (const [name, baseHeader] of Object.entries(baseHeaders ?? {})) {
    const headerPointer = `${pointer}/${escapePointerSegment(name.toLowerCase())}`
    const headEntry = Object.entries(headHeaders ?? {}).find(
      ([headName]) => headName.toLowerCase() === name.toLowerCase(),
    )
    if (headEntry === undefined) {
      addFailure(failures, headerPointer, 'existing response header was removed or renamed')
      continue
    }
    compareParameter(
      baseDocument,
      headDocument,
      baseHeader,
      headEntry[1],
      headerPointer,
      failures,
      'response',
    )
  }
}

function compareRequestBody(baseDocument, headDocument, baseBody, headBody, pointer, failures) {
  if (baseBody === undefined) {
    if (headBody !== undefined && resolveLocalReference(headDocument, headBody).required === true) {
      addFailure(failures, pointer, 'a new required request body was added')
    }
    return
  }
  if (headBody === undefined) {
    addFailure(failures, pointer, 'existing request body was removed')
    return
  }

  compareReference(baseBody, headBody, pointer, failures)
  const baseResolved = resolveLocalReference(baseDocument, baseBody)
  const headResolved = resolveLocalReference(headDocument, headBody)
  if (baseResolved.required !== true && headResolved.required === true) {
    addFailure(failures, `${pointer}/required`, 'request body became required')
  }
  compareContent(
    baseDocument,
    headDocument,
    baseResolved.content,
    headResolved.content,
    `${pointer}/content`,
    failures,
    'request',
  )
}

function compareResponse(baseDocument, headDocument, baseResponse, headResponse, pointer, failures) {
  compareReference(baseResponse, headResponse, pointer, failures)
  const baseResolved = resolveLocalReference(baseDocument, baseResponse)
  const headResolved = resolveLocalReference(headDocument, headResponse)
  compareHeaders(
    baseDocument,
    headDocument,
    baseResolved.headers,
    headResolved.headers,
    `${pointer}/headers`,
    failures,
  )
  compareContent(
    baseDocument,
    headDocument,
    baseResolved.content,
    headResolved.content,
    `${pointer}/content`,
    failures,
    'response',
  )
  for (const [name, baseLink] of Object.entries(baseResolved.links ?? {})) {
    const headLink = headResolved.links?.[name]
    if (headLink === undefined || !sameSemantics(baseLink, headLink)) {
      addFailure(failures, `${pointer}/links/${escapePointerSegment(name)}`, 'existing link changed')
    }
  }
}

function compareResponses(baseDocument, headDocument, baseResponses, headResponses, pointer, failures) {
  const baseEntries = baseResponses ?? {}
  const headEntries = headResponses ?? {}
  for (const [status, baseResponse] of Object.entries(baseEntries)) {
    const responsePointer = `${pointer}/${escapePointerSegment(status)}`
    const headResponse = headEntries[status]
    if (headResponse === undefined) {
      addFailure(failures, responsePointer, 'existing response status was removed or changed')
      continue
    }
    compareResponse(baseDocument, headDocument, baseResponse, headResponse, responsePointer, failures)
  }
  for (const status of Object.keys(headEntries)) {
    if (!Object.hasOwn(baseEntries, status)) {
      addFailure(
        failures,
        `${pointer}/${escapePointerSegment(status)}`,
        'new response status was added to an existing operation',
      )
    }
  }
}

function compareOperation(baseDocument, headDocument, basePathItem, headPathItem, baseOperation, headOperation, pointer, failures) {
  if (baseOperation.operationId !== headOperation.operationId) {
    addFailure(
      failures,
      `${pointer}/operationId`,
      `operationId changed from ${baseOperation.operationId} to ${headOperation.operationId}`,
    )
  }
  if (!sameValue(baseOperation.security, headOperation.security)) {
    addFailure(failures, `${pointer}/security`, 'operation security changed')
  }
  if (!sameValue(baseOperation['x-required-roles'], headOperation['x-required-roles'])) {
    addFailure(failures, `${pointer}/x-required-roles`, 'required roles changed')
  }
  if (!sameSemantics(baseOperation.servers, headOperation.servers)) {
    addFailure(failures, `${pointer}/servers`, 'operation servers changed')
  }
  if (!sameSemantics(baseOperation.callbacks, headOperation.callbacks)) {
    addFailure(failures, `${pointer}/callbacks`, 'operation callbacks changed')
  }

  compareParameters(
    baseDocument,
    headDocument,
    basePathItem,
    headPathItem,
    baseOperation,
    headOperation,
    pointer,
    failures,
  )
  compareRequestBody(
    baseDocument,
    headDocument,
    baseOperation.requestBody,
    headOperation.requestBody,
    `${pointer}/requestBody`,
    failures,
  )
  compareResponses(
    baseDocument,
    headDocument,
    baseOperation.responses,
    headOperation.responses,
    `${pointer}/responses`,
    failures,
  )
}

function comparePaths(baseOpenApi, headOpenApi, failures) {
  let operations = 0
  for (const [route, basePathItem] of Object.entries(baseOpenApi.paths ?? {})) {
    const pathPointer = `#/paths/${escapePointerSegment(route)}`
    const headPathItem = headOpenApi.paths?.[route]
    if (headPathItem === undefined) {
      addFailure(failures, pathPointer, 'existing path was removed or renamed')
      operations += Object.keys(basePathItem).filter((key) => HTTP_METHODS.has(key)).length
      continue
    }

    for (const [method, baseOperation] of Object.entries(basePathItem)) {
      if (!HTTP_METHODS.has(method)) {
        continue
      }
      operations += 1
      const pointer = `${pathPointer}/${method}`
      const headOperation = headPathItem[method]
      if (headOperation === undefined) {
        addFailure(failures, pointer, 'existing operation was removed or moved')
        continue
      }
      compareOperation(
        baseOpenApi,
        headOpenApi,
        basePathItem,
        headPathItem,
        baseOperation,
        headOperation,
        pointer,
        failures,
      )
    }
  }
  return operations
}

function compareNamedComponents(baseOpenApi, headOpenApi, kind, compare, failures) {
  const baseComponents = baseOpenApi.components?.[kind] ?? {}
  const headComponents = headOpenApi.components?.[kind] ?? {}
  for (const [name, baseComponent] of Object.entries(baseComponents)) {
    const pointer = `#/components/${kind}/${escapePointerSegment(name)}`
    const headComponent = headComponents[name]
    if (headComponent === undefined) {
      addFailure(failures, pointer, 'existing component was removed or renamed')
      continue
    }
    compare(baseComponent, headComponent, pointer, failures)
  }
  return Object.keys(baseComponents).length
}

function compareComponents(baseOpenApi, headOpenApi, failures) {
  // A named schema can be reached from both request and response graphs. Freeze it
  // conservatively in both directions until reference reachability is modeled.
  const schemas = compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'schemas',
    (base, head, pointer, componentFailures) =>
      compareSchema(base, head, pointer, componentFailures, 'both'),
    failures,
  )
  compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'parameters',
    (base, head, pointer, componentFailures) =>
      compareParameter(baseOpenApi, headOpenApi, base, head, pointer, componentFailures),
    failures,
  )
  compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'headers',
    (base, head, pointer, componentFailures) =>
      compareParameter(
        baseOpenApi,
        headOpenApi,
        base,
        head,
        pointer,
        componentFailures,
        'response',
      ),
    failures,
  )
  compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'responses',
    (base, head, pointer, componentFailures) =>
      compareResponse(baseOpenApi, headOpenApi, base, head, pointer, componentFailures),
    failures,
  )
  compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'requestBodies',
    (base, head, pointer, componentFailures) =>
      compareRequestBody(baseOpenApi, headOpenApi, base, head, pointer, componentFailures),
    failures,
  )
  compareNamedComponents(
    baseOpenApi,
    headOpenApi,
    'securitySchemes',
    (base, head, pointer, componentFailures) => {
      if (!sameSemantics(base, head)) {
        addFailure(componentFailures, pointer, 'security scheme semantics changed')
      }
    },
    failures,
  )
  return schemas
}

function compareErrorCatalog(baseSource, headSource, failures) {
  const baseEntries = parseStableErrorSemantics(baseSource)
  const headEntries = parseStableErrorSemantics(headSource)
  const headByCode = new Map(headEntries.map((entry) => [entry.code, entry]))

  for (const baseEntry of baseEntries) {
    const pointer = `error-catalog:${baseEntry.code}`
    const headEntry = headByCode.get(baseEntry.code)
    if (headEntry === undefined) {
      addFailure(failures, pointer, 'existing stable error code was removed or renamed')
      continue
    }
    if (!sameValue(baseEntry, headEntry)) {
      addFailure(failures, pointer, 'existing status, stream, retry, or meaning semantics changed')
    }
  }
  return baseEntries.length
}

function compareSseFixtures(baseFixtures, headFixtures, failures) {
  invariant(baseFixtures instanceof Map, 'Base SSE fixtures must be provided as a Map.')
  invariant(headFixtures instanceof Map, 'Head SSE fixtures must be provided as a Map.')

  for (const [fixturePath, baseContent] of baseFixtures) {
    invariant(typeof fixturePath === 'string', 'SSE fixture paths must be strings.')
    invariant(
      typeof baseContent === 'string' || Buffer.isBuffer(baseContent),
      `Base SSE fixture ${fixturePath} must contain exact string or Buffer content.`,
    )
    const headContent = headFixtures.get(fixturePath)
    if (headContent === undefined) {
      addFailure(failures, `sse-fixture:${fixturePath}`, 'existing SSE fixture was removed')
      continue
    }
    invariant(
      typeof headContent === 'string' || Buffer.isBuffer(headContent),
      `Head SSE fixture ${fixturePath} must contain exact string or Buffer content.`,
    )
    const baseBytes = Buffer.isBuffer(baseContent) ? baseContent : Buffer.from(baseContent)
    const headBytes = Buffer.isBuffer(headContent) ? headContent : Buffer.from(headContent)
    if (!baseBytes.equals(headBytes)) {
      addFailure(failures, `sse-fixture:${fixturePath}`, 'existing SSE fixture content changed')
    }
  }

  return baseFixtures.size
}

export function validateContractCompatibility({
  allowedFailures = [],
  baseErrorCatalogSource,
  baseOpenApi,
  baseSseFixtures = new Map(),
  headErrorCatalogSource,
  headOpenApi,
  headSseFixtures = new Map(),
}) {
  const failures = []
  invariant(baseOpenApi && typeof baseOpenApi === 'object', 'Base OpenAPI document is required.')
  invariant(headOpenApi && typeof headOpenApi === 'object', 'Head OpenAPI document is required.')
  invariant(typeof baseErrorCatalogSource === 'string', 'Base error catalog source is required.')
  invariant(typeof headErrorCatalogSource === 'string', 'Head error catalog source is required.')
  invariant(Array.isArray(allowedFailures), 'Compatibility reset allowed failures must be an array.')
  invariant(
    allowedFailures.every((failure) => typeof failure === 'string'),
    'Compatibility reset allowed failures must contain only strings.',
  )
  invariant(
    new Set(allowedFailures).size === allowedFailures.length,
    'Compatibility reset allowed failures must not contain duplicates.',
  )

  if (baseOpenApi.openapi !== headOpenApi.openapi) {
    addFailure(failures, '#/openapi', 'OpenAPI dialect/version changed')
  }
  if (baseOpenApi.info?.version !== headOpenApi.info?.version) {
    addFailure(failures, '#/info/version', 'public API version changed in place')
  }
  if (!sameSemantics(baseOpenApi.servers, headOpenApi.servers)) {
    addFailure(failures, '#/servers', 'top-level servers changed')
  }
  if (!sameValue(baseOpenApi.security, headOpenApi.security)) {
    addFailure(failures, '#/security', 'top-level security changed')
  }

  const operations = comparePaths(baseOpenApi, headOpenApi, failures)
  const schemas = compareComponents(baseOpenApi, headOpenApi, failures)
  const errorCodes = compareErrorCatalog(baseErrorCatalogSource, headErrorCatalogSource, failures)
  const sseFixtures = compareSseFixtures(baseSseFixtures, headSseFixtures, failures)

  if (allowedFailures.length > 0) {
    const actual = [...failures].sort()
    const expected = [...allowedFailures].sort()
    if (!sameValue(actual, expected)) {
      const actualSet = new Set(actual)
      const expectedSet = new Set(expected)
      const unexpected = actual.filter((failure) => !expectedSet.has(failure))
      const unused = expected.filter((failure) => !actualSet.has(failure))
      const details = [
        ...unexpected.map((failure) => `- unexpected: ${failure}`),
        ...unused.map((failure) => `- registered but absent: ${failure}`),
      ]
      throw new ContractFailure(
        `Compatibility reset mismatch: expected exactly ${expected.length} breaking diagnostics, received ${actual.length}.\n${details.join('\n')}`,
      )
    }
  } else if (failures.length > 0) {
    throw new ContractFailure(
      `Breaking contract changes detected (${failures.length}):\n${failures.map((failure) => `- ${failure}`).join('\n')}`,
    )
  }

  return {
    errorCodes,
    operations,
    schemas,
    sseFixtures,
    waivedFailures: allowedFailures.length,
  }
}

function parseOpenApiSource(source, label) {
  const document = YAML.parseDocument(source, {
    prettyErrors: true,
    strict: true,
    uniqueKeys: true,
  })
  invariant(
    document.errors.length === 0,
    `${label} OpenAPI YAML is invalid: ${document.errors.map((error) => error.message).join('; ')}`,
  )
  return document.toJS({ maxAliasCount: 0 })
}

export function validateHeadOpenApiSource({ headOpenApi, headOpenApiSource }) {
  invariant(typeof headOpenApiSource === 'string', 'Head OpenAPI source is required.')
  const parsedHeadOpenApi = parseOpenApiSource(headOpenApiSource, 'Head')
  invariant(
    sameValue(parsedHeadOpenApi, headOpenApi),
    'Head OpenAPI source differs from the validated head OpenAPI document.',
  )
  return parsedHeadOpenApi
}

async function readBaseBlob(baseRef, relativePath) {
  try {
    const result = await execFileAsync('git', ['show', `${baseRef}:${relativePath}`], {
      cwd: repoRoot,
      encoding: 'utf8',
      maxBuffer: 8 * 1024 * 1024,
    })
    return result.stdout
  } catch {
    throw new ContractFailure(
      `Unable to read ${relativePath} from CONTRACT_DIFF_BASE ${baseRef}; ensure the exact base commit was fetched.`,
    )
  }
}

async function readOptionalBaseBlob(baseRef, relativePath) {
  let result
  try {
    result = await execFileAsync(
      'git',
      ['ls-tree', '-z', '--name-only', baseRef, '--', relativePath],
      {
        cwd: repoRoot,
        encoding: 'utf8',
        maxBuffer: 8 * 1024 * 1024,
      },
    )
  } catch {
    throw new ContractFailure(
      `Unable to inspect ${relativePath} at CONTRACT_DIFF_BASE ${baseRef}; ensure the exact base commit was fetched.`,
    )
  }

  const entries = result.stdout.split('\0').filter((entry) => entry.length > 0)
  invariant(
    entries.length <= 1 && (entries.length === 0 || entries[0] === relativePath),
    `Git returned an invalid inventory for ${relativePath} at CONTRACT_DIFF_BASE ${baseRef}.`,
  )
  return entries.length === 0 ? undefined : readBaseBlob(baseRef, relativePath)
}

async function listBaseSseFixturePaths(baseRef) {
  let result
  try {
    result = await execFileAsync(
      'git',
      ['ls-tree', '-rz', '--name-only', baseRef, '--', 'docs/contracts/fixtures'],
      {
        cwd: repoRoot,
        encoding: 'buffer',
        maxBuffer: 8 * 1024 * 1024,
      },
    )
  } catch {
    throw new ContractFailure(
      `Unable to enumerate SSE fixtures from CONTRACT_DIFF_BASE ${baseRef}; ensure the exact base commit was fetched.`,
    )
  }

  invariant(Buffer.isBuffer(result.stdout), 'Git SSE fixture inventory must be returned as bytes.')
  const fixturePaths = result.stdout
    .toString('utf8')
    .split('\0')
    .filter((relativePath) => relativePath.endsWith('.sse'))
    .sort()
  for (const relativePath of fixturePaths) {
    invariant(
      relativePath.startsWith('docs/contracts/fixtures/') &&
        !relativePath.split('/').includes('..'),
      `Invalid SSE fixture path returned by Git: ${relativePath}`,
    )
  }
  return fixturePaths
}

async function readBaseBlobBytes(baseRef, relativePath) {
  try {
    const result = await execFileAsync('git', ['show', `${baseRef}:${relativePath}`], {
      cwd: repoRoot,
      encoding: 'buffer',
      maxBuffer: 8 * 1024 * 1024,
    })
    invariant(Buffer.isBuffer(result.stdout), `Git blob ${relativePath} must be returned as bytes.`)
    return result.stdout
  } catch (error) {
    if (error instanceof ContractFailure) {
      throw error
    }
    throw new ContractFailure(
      `Unable to read ${relativePath} from CONTRACT_DIFF_BASE ${baseRef}; ensure the exact base commit was fetched.`,
    )
  }
}

async function loadSseFixturesAgainstGitBase(baseRef) {
  const fixturePaths = await listBaseSseFixturePaths(baseRef)
  const fixtureRoot = `${path.resolve(repoRoot, 'docs/contracts/fixtures')}${path.sep}`
  const pairs = await Promise.all(
    fixturePaths.map(async (relativePath) => {
      const absolutePath = path.resolve(repoRoot, relativePath)
      invariant(
        absolutePath.startsWith(fixtureRoot),
        `SSE fixture escaped the contract fixture directory: ${relativePath}`,
      )
      const baseContent = await readBaseBlobBytes(baseRef, relativePath)
      let headContent
      try {
        headContent = await readFile(absolutePath)
      } catch (error) {
        if (error?.code !== 'ENOENT') {
          throw new ContractFailure(`Unable to read head SSE fixture ${relativePath}: ${error.message}`)
        }
      }
      return [relativePath, baseContent, headContent]
    }),
  )

  return {
    baseSseFixtures: new Map(pairs.map(([relativePath, baseContent]) => [relativePath, baseContent])),
    headSseFixtures: new Map(
      pairs
        .filter(([, , headContent]) => headContent !== undefined)
        .map(([relativePath, , headContent]) => [relativePath, headContent]),
    ),
  }
}

export async function validateContractsAgainstGitBase({
  baseRef,
  compatibilityResetSource,
  headErrorCatalogSource,
  headOpenApi,
  headOpenApiSource,
}) {
  invariant(
    typeof baseRef === 'string' && /^[0-9a-f]{40}$/iu.test(baseRef),
    'CONTRACT_DIFF_BASE must be an exact 40-character Git commit SHA.',
  )
  const normalizedBaseRef = baseRef.toLowerCase()
  invariant(typeof headOpenApiSource === 'string', 'Head OpenAPI source is required.')
  const resetState = validateCompatibilityResetDecisions(compatibilityResetSource)
  const [
    baseOpenApiSource,
    baseErrorCatalogSource,
    baseResetRegistrySource,
    sseFixtures,
  ] = await Promise.all([
    readBaseBlob(normalizedBaseRef, 'docs/contracts/openapi-v1.yaml'),
    readBaseBlob(normalizedBaseRef, 'docs/contracts/error-catalog.md'),
    readOptionalBaseBlob(normalizedBaseRef, COMPATIBILITY_RESET_RELATIVE_PATH),
    loadSseFixturesAgainstGitBase(normalizedBaseRef),
  ])
  const resetHistory = validateCompatibilityResetHistory({
    baseRegistrySource: baseResetRegistrySource,
    headRegistrySource: compatibilityResetSource,
  })
  if (resetHistory.baseRegistry !== undefined) {
    const baseAdrSources = new Map(
      await Promise.all(
        resetHistory.baseRegistry.resets.map(async (reset) => [
          reset.id,
          await readBaseBlobBytes(normalizedBaseRef, reset.adr),
        ]),
      ),
    )
    validateCompatibilityResetAdrHistory({
      baseAdrSources,
      baseRegistry: resetHistory.baseRegistry,
      headAdrSources: resetState.adrSources,
    })
  }
  const parsedHeadOpenApi = validateHeadOpenApiSource({ headOpenApi, headOpenApiSource })
  const reset = resolveCompatibilityReset({
    baseOpenApiSource,
    baseRef: normalizedBaseRef,
    headOpenApiSource,
    registrySource: compatibilityResetSource,
  })
  const result = validateContractCompatibility({
    allowedFailures: reset?.allowedFailures,
    baseErrorCatalogSource,
    baseOpenApi: parseOpenApiSource(baseOpenApiSource, 'Base'),
    baseSseFixtures: sseFixtures.baseSseFixtures,
    headErrorCatalogSource,
    headOpenApi: parsedHeadOpenApi,
    headSseFixtures: sseFixtures.headSseFixtures,
  })
  return {
    ...result,
    baseRef: normalizedBaseRef,
    resetId: reset?.id,
  }
}
