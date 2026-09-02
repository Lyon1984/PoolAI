import { readFileSync, realpathSync } from 'node:fs'
import path from 'node:path'
import { TextDecoder } from 'node:util'

import { withReadOnlyRepositoryFile } from '../../../eng/policies/repository-file.mjs'
import {
  ContractFailure,
  inspectFatalUtf8Artifact,
  invariant,
  repoRoot,
  sha256,
  stableJson,
  YAML,
} from './context.mjs'

const WINDOW_REGISTRY_KEYS = ['schemaVersion', 'windows']
const WINDOW_V1_KEYS = [
  'adr',
  'allowedFailures',
  'approvalControl',
  'approvalEvidence',
  'baseOpenApiSha256',
  'baseRef',
  'headOpenApiSha256',
  'id',
  'scope',
  'status',
]
const WINDOW_V2_DIGEST_KEYS = [
  ...WINDOW_V1_KEYS,
  'baseErrorCatalogSha256',
  'headErrorCatalogSha256',
]
const WINDOW_SCOPE = 'openapi-v1-compatibility-window'
const EXACT_APPROVAL_CONTROL = 'https://github.com/Lyon1984/PoolAI/issues/44'
const MACHINE_LINE_CONTROL = /[\p{Cc}\p{Zl}\p{Zp}]/u
const ERROR_CATALOG_SELECTOR = /^error-catalog:([a-z][a-z0-9_]{0,127})$/u
const RESERVED_MARKER_KEYS = [
  'status',
  'compatibilitywindowid',
  'basegitcommit',
  'baseopenapisha256',
  'targetopenapisha256',
  'baseerrorcatalogsha256',
  'targeterrorcatalogsha256',
  'approvalcontrol',
  'approvalevidence',
  'alloweddiagnostic',
]

function sameValue(left, right) {
  return stableJson(left) === stableJson(right)
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

function hasExactKeys(value, keys) {
  return value !== null && typeof value === 'object' && !Array.isArray(value) &&
    sameValue(Object.keys(value).sort(), [...keys].sort())
}

function requireMachineLineString(value, label) {
  invariant(typeof value === 'string', `${label} must be a string.`)
  invariant(
    !MACHINE_LINE_CONTROL.test(value),
    `${label} must not contain control, line, or paragraph separator characters.`,
  )
}

function hasUnicodeScalar(value) {
  for (const character of value) {
    const codePoint = character.codePointAt(0)
    if (codePoint < 0xD800 || codePoint > 0xDFFF) {
      return true
    }
  }
  return false
}

export function isDigestBoundCompatibilityWindow(window) {
  return hasExactKeys(window, WINDOW_V2_DIGEST_KEYS)
}

function validateAllowedFailure(failure, label, digestBound) {
  requireMachineLineString(failure, label)
  const delimiter = failure.indexOf(': ')
  invariant(delimiter > 0, `${label} must be one exact compatibility diagnostic.`)
  const selector = failure.slice(0, delimiter)
  const diagnostic = failure.slice(delimiter + 2)
  invariant(
    diagnostic.length > 0 && hasUnicodeScalar(diagnostic),
    `${label} diagnostic must contain at least one Unicode scalar value.`,
  )

  if (selector.startsWith('#/')) {
    invariant(
      /^(?:#\/)(?:[^~]|~[01])*$/u.test(selector),
      `${label} must use one exact local OpenAPI JSON Pointer.`,
    )
    invariant(
      !selector.includes('*') && !selector.includes('?'),
      `${label} selector must not contain wildcards.`,
    )
    return 'openapi'
  }

  const errorCatalogMatch = ERROR_CATALOG_SELECTOR.exec(selector)
  invariant(
    errorCatalogMatch !== null,
    `${label} must use an exact OpenAPI or error-catalog diagnostic selector.`,
  )
  invariant(
    digestBound,
    `${label} error-catalog diagnostic requires a digest-bound schemaVersion 2 record.`,
  )
  return 'error-catalog'
}

function isExactIssueUrl(value) {
  if (typeof value !== 'string') {
    return false
  }
  const parts = value.split('/')
  return parts.length === 7 && parts[0] === 'https:' && parts[1] === '' &&
    parts[2] === 'github.com' && /^[A-Za-z0-9_.-]+$/u.test(parts[3]) &&
    /^[A-Za-z0-9_.-]+$/u.test(parts[4]) && parts[5] === 'issues' &&
    /^[1-9][0-9]*$/u.test(parts[6])
}

function isExactApprovalEvidence(value, approvalControl) {
  if (typeof value !== 'string' || !value.startsWith(`${approvalControl}#issuecomment-`)) {
    return false
  }
  return /^[1-9][0-9]*$/u.test(value.slice(`${approvalControl}#issuecomment-`.length))
}

export function parseCompatibilityWindowRegistry(source) {
  invariant(typeof source === 'string', 'Compatibility window registry source is required.')
  let json
  try {
    json = JSON.parse(source)
  } catch (error) {
    throw new ContractFailure(`Compatibility window registry is not valid JSON: ${error.message}`)
  }

  const document = YAML.parseDocument(source, {
    prettyErrors: true,
    strict: true,
    uniqueKeys: true,
  })
  invariant(
    document.errors.length === 0,
    `Compatibility window registry is invalid: ${document.errors
      .map((error) => error.message)
      .join('; ')}`,
  )
  invariant(
    sameValue(json, document.toJS({ maxAliasCount: 0 })),
    'Compatibility window registry JSON and strict parser results differ.',
  )
  requireExactKeys(json, WINDOW_REGISTRY_KEYS, 'Compatibility window registry')
  invariant(
    json.schemaVersion === 1 || json.schemaVersion === 2,
    'Compatibility window registry must use schemaVersion 1 or 2.',
  )
  invariant(Array.isArray(json.windows), 'Compatibility window registry windows must be an array.')
  invariant(
    json.windows.length > 0,
    `Compatibility window registry schemaVersion ${json.schemaVersion} must contain at least one window.`,
  )

  const ids = new Set()
  const baseRefs = new Set()
  for (const [index, window] of json.windows.entries()) {
    const label = `Compatibility window registry windows[${index}]`
    const digestBound = isDigestBoundCompatibilityWindow(window)
    if (json.schemaVersion === 1) {
      requireExactKeys(window, WINDOW_V1_KEYS, label)
    } else {
      invariant(
        hasExactKeys(window, WINDOW_V1_KEYS) || digestBound,
        `${label} must contain exactly the schemaVersion 2 OpenAPI-only or digest-bound keys.`,
      )
    }
    for (const [key, value] of Object.entries(window)) {
      if (typeof value === 'string') {
        requireMachineLineString(value, `${label}.${key}`)
      }
    }
    invariant(
      typeof window.id === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(window.id),
      `${label}.id must be lower kebab-case.`,
    )
    invariant(!ids.has(window.id), `${label}.id duplicates ${window.id}.`)
    ids.add(window.id)
    invariant(
      window.status === 'proposed' || window.status === 'accepted',
      `${label}.status must be proposed or accepted.`,
    )
    invariant(window.scope === WINDOW_SCOPE, `${label}.scope must be ${WINDOW_SCOPE}.`)
    invariant(
      typeof window.baseRef === 'string' && /^[0-9a-f]{40}$/u.test(window.baseRef),
      `${label}.baseRef must be an exact lowercase 40-character Git SHA.`,
    )
    invariant(!baseRefs.has(window.baseRef), `${label}.baseRef duplicates ${window.baseRef}.`)
    baseRefs.add(window.baseRef)
    for (const key of ['baseOpenApiSha256', 'headOpenApiSha256']) {
      invariant(
        typeof window[key] === 'string' && /^[0-9a-f]{64}$/u.test(window[key]),
        `${label}.${key} must be an exact lowercase SHA-256 digest.`,
      )
    }
    invariant(
      window.baseOpenApiSha256 !== window.headOpenApiSha256,
      `${label} must bind different base and head OpenAPI digests.`,
    )
    if (digestBound) {
      for (const key of ['baseErrorCatalogSha256', 'headErrorCatalogSha256']) {
        invariant(
          typeof window[key] === 'string' && /^[0-9a-f]{64}$/u.test(window[key]),
          `${label}.${key} must be an exact lowercase SHA-256 digest.`,
        )
      }
    }

    const adrName = typeof window.adr === 'string' ? path.basename(window.adr) : ''
    invariant(
      window.adr === `docs/architecture/adr/${adrName}` &&
        /^[0-9]{4}-[a-z0-9]+(?:-[a-z0-9]+)*[.]md$/u.test(adrName),
      `${label}.adr must name one repository ADR.`,
    )
    invariant(
      isExactIssueUrl(window.approvalControl),
      `${label}.approvalControl must be one exact GitHub Issue URL.`,
    )
    invariant(
      window.approvalControl === EXACT_APPROVAL_CONTROL,
      `${label}.approvalControl must be ${EXACT_APPROVAL_CONTROL}.`,
    )
    if (window.status === 'proposed') {
      invariant(
        window.approvalEvidence === null,
        `${label}.approvalEvidence must be null while status is proposed.`,
      )
    } else {
      invariant(
        isExactApprovalEvidence(window.approvalEvidence, window.approvalControl),
        `${label}.approvalEvidence must be a permanent comment URL under approvalControl when status is accepted.`,
      )
    }

    invariant(
      Array.isArray(window.allowedFailures) && window.allowedFailures.length > 0,
      `${label}.allowedFailures must be a non-empty array.`,
    )
    invariant(
      sameValue(window.allowedFailures, [...window.allowedFailures].sort()),
      `${label}.allowedFailures must be sorted.`,
    )
    invariant(
      new Set(window.allowedFailures).size === window.allowedFailures.length,
      `${label}.allowedFailures must not contain duplicates.`,
    )
    let errorCatalogFailures = 0
    for (const [failureIndex, failure] of window.allowedFailures.entries()) {
      const kind = validateAllowedFailure(
        failure,
        `${label}.allowedFailures[${failureIndex}]`,
        digestBound,
      )
      if (kind === 'error-catalog') {
        errorCatalogFailures += 1
      }
    }
    if (errorCatalogFailures > 0) {
      invariant(
        window.baseErrorCatalogSha256 !== window.headErrorCatalogSha256,
        `${label} with an error-catalog diagnostic must bind different base and head error-catalog digests.`,
      )
    }
  }

  return json
}

function approvalEvidenceLine(window) {
  return window.status === 'accepted'
    ? `- Approval evidence: [Issue approval comment](${window.approvalEvidence})`
    : '- Approval evidence: **Pending explicit approval**'
}

function compatibilityWindowDecisionLines(window) {
  const status = window.status === 'accepted' ? 'Accepted' : 'Proposed'
  const issueNumber = window.approvalControl.split('/').at(-1)
  return [
    `- Status: **${status}**`,
    `- Compatibility window ID: \`${window.id}\``,
    `- Base Git commit: \`${window.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${window.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${window.headOpenApiSha256}\``,
    ...(isDigestBoundCompatibilityWindow(window)
      ? [
          `- Base error-catalog SHA-256: \`${window.baseErrorCatalogSha256}\``,
          `- Target error-catalog SHA-256: \`${window.headErrorCatalogSha256}\``,
        ]
      : []),
    `- Approval control: [Issue #${issueNumber}](${window.approvalControl})`,
    approvalEvidenceLine(window),
    ...window.allowedFailures.map((failure) => `- Allowed diagnostic: \`${failure}\``),
  ]
}

function asciiCollisionKey(value) {
  return value
    .replace(/[A-Z]/gu, (character) => character.toLowerCase())
    .replace(/[\t\v\f\r _-]/gu, '')
}

function isReservedMarkerLabel(label) {
  const key = asciiCollisionKey(label)
  if (RESERVED_MARKER_KEYS.some((reserved) => key.includes(reserved))) {
    return true
  }
  const source = key.includes('openapi') || key.includes('errorcatalog')
  const direction = ['base', 'target', 'head'].some((value) => key.includes(value))
  const digest = ['digest', 'hash', 'checksum'].some((value) => key.includes(value)) ||
    /sha[0-9]+/u.test(key)
  return source && direction && digest
}

function validateDigestBoundDecisionSource(window, source, requiredLines) {
  invariant(
    !/[\r\u0085\u2028\u2029]/u.test(source),
    `Compatibility window ${window.id} digest-bound ADR must use LF line boundaries and contain no alternate line separators.`,
  )
  const lines = source.split('\n')
  invariant(
    /^# [^\n]+$/u.test(lines[0] ?? ''),
    `Compatibility window ${window.id} digest-bound ADR must begin with one H1 title.`,
  )
  const firstSection = lines.findIndex((line, index) => index > 0 && line.startsWith('## '))
  invariant(
    firstSection > 0,
    `Compatibility window ${window.id} digest-bound ADR must contain an exact first ## section boundary.`,
  )
  invariant(
    !lines.slice(1, firstSection).some((line) => line.startsWith('# ')),
    `Compatibility window ${window.id} digest-bound ADR must not contain a second H1 before its first ## section.`,
  )
  invariant(
    !lines.slice(1, firstSection).some((line) => /^[\t\v\f ]+#{1,6} /u.test(line)),
    `Compatibility window ${window.id} digest-bound ADR preamble must not contain an indented pseudo-heading.`,
  )

  const preamble = lines.slice(1, firstSection)
  const requiredSet = new Set(requiredLines)
  for (const requiredLine of requiredLines) {
    invariant(
      lines.filter((line) => line === requiredLine).length === 1 &&
        preamble.filter((line) => line === requiredLine).length === 1,
      `Compatibility window ${window.id} ${window.status} ADR preamble must contain exactly one line: ${requiredLine}`,
    )
  }

  for (const [index, line] of lines.entries()) {
    const candidate = /^[\t\v\f ]*[-+*] +([^:]+):/u.exec(line)
    if (candidate === null || !isReservedMarkerLabel(candidate[1])) {
      continue
    }
    invariant(
      requiredSet.has(line) && index > 0 && index < firstSection,
      `Compatibility window ${window.id} digest-bound ADR contains an invalid or misplaced reserved marker: ${line}`,
    )
  }
}

export function validateCompatibilityWindowDecisionSource(window, source) {
  invariant(typeof source === 'string', `Compatibility window ${window.id} ADR source is required.`)
  const requiredLines = compatibilityWindowDecisionLines(window)
  if (isDigestBoundCompatibilityWindow(window)) {
    validateDigestBoundDecisionSource(window, source, requiredLines)
    return
  }
  const lines = source.split(/\r?\n/u)
  for (const requiredLine of requiredLines) {
    invariant(
      lines.filter((line) => line === requiredLine).length === 1,
      `Compatibility window ${window.id} ${window.status} ADR must contain exactly one line: ${requiredLine}`,
    )
  }
}

function readCompatibilityWindowDecision(window) {
  let result
  try {
    const adrRoot = `${realpathSync(path.resolve(repoRoot, 'docs/architecture/adr'))}${path.sep}`
    result = withReadOnlyRepositoryFile(repoRoot, window.adr, (descriptor, canonical) => {
      invariant(
        canonical.startsWith(adrRoot),
        `Compatibility window ${window.id} ADR escaped its canonical directory.`,
      )
      return readFileSync(descriptor)
    })
  } catch (error) {
    throw new ContractFailure(
      `Compatibility window ${window.id} cannot safely read ADR ${window.adr}: ${error.message}`,
    )
  }

  let source
  try {
    source = new TextDecoder('utf-8', { fatal: true }).decode(result)
  } catch (error) {
    throw new ContractFailure(
      `Compatibility window ${window.id} ADR must be valid UTF-8: ${error.message}`,
    )
  }
  validateCompatibilityWindowDecisionSource(window, source)
  return result
}

export function validateCompatibilityWindowDecisions(registrySource) {
  const registry = parseCompatibilityWindowRegistry(registrySource)
  const adrSources = new Map(
    registry.windows.map((window) => [window.id, readCompatibilityWindowDecision(window)]),
  )
  return { adrSources, registry }
}

function normalizedApprovalTransition(window) {
  return {
    ...window,
    status: 'proposed',
    approvalEvidence: null,
  }
}

export function validateCompatibilityWindowHistory({ baseRegistrySource, headRegistrySource }) {
  const headRegistry = parseCompatibilityWindowRegistry(headRegistrySource)
  if (baseRegistrySource === undefined) {
    invariant(
      headRegistry.schemaVersion === 1,
      'Compatibility window schemaVersion 2 requires a readable schemaVersion 1 or 2 base registry.',
    )
    return { baseRegistry: undefined, headRegistry }
  }

  const baseRegistry = parseCompatibilityWindowRegistry(baseRegistrySource)
  invariant(
    baseRegistry.schemaVersion === headRegistry.schemaVersion ||
      (baseRegistry.schemaVersion === 1 && headRegistry.schemaVersion === 2),
    `Compatibility window registry schemaVersion may only remain unchanged or advance from 1 to 2; base is ${baseRegistry.schemaVersion}, head is ${headRegistry.schemaVersion}.`,
  )
  const baseIds = baseRegistry.windows.map((window) => window.id)
  const headPrefix = headRegistry.windows.slice(0, baseIds.length).map((window) => window.id)
  invariant(
    headRegistry.windows.length >= baseRegistry.windows.length && sameValue(baseIds, headPrefix),
    'Compatibility window history is immutable; base window IDs must be the exact head prefix.',
  )
  for (const [index, baseWindow] of baseRegistry.windows.entries()) {
    const headWindow = headRegistry.windows[index]
    if (baseWindow.status === 'accepted') {
      invariant(
        sameValue(baseWindow, headWindow),
        `Compatibility window history is immutable; accepted window ${baseWindow.id} changed.`,
      )
      continue
    }

    invariant(
      sameValue(baseWindow, normalizedApprovalTransition(headWindow)),
      `Compatibility window ${baseWindow.id} may change only through the proposed-to-accepted approval transition.`,
    )
  }
  return { baseRegistry, headRegistry }
}

function decodeAdrSource(value, label) {
  invariant(Buffer.isBuffer(value), `${label} must be bytes.`)
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(value)
  } catch (error) {
    throw new ContractFailure(`${label} must be valid UTF-8: ${error.message}`)
  }
}

function normalizeAcceptedAdrToProposed(window, source) {
  const acceptedStatus = '- Status: **Accepted**'
  const acceptedEvidence = approvalEvidenceLine(window)
  invariant(
    source.split(acceptedStatus).length === 2 && source.split(acceptedEvidence).length === 2,
    `Compatibility window ${window.id} accepted ADR has ambiguous approval markers.`,
  )
  return source
    .replace(acceptedStatus, '- Status: **Proposed**')
    .replace(acceptedEvidence, '- Approval evidence: **Pending explicit approval**')
}

export function validateCompatibilityWindowAdrHistory({
  baseAdrSources,
  baseRegistry,
  headAdrSources,
  headRegistry,
}) {
  invariant(baseAdrSources instanceof Map, 'Base compatibility window ADR sources must be a Map.')
  invariant(headAdrSources instanceof Map, 'Head compatibility window ADR sources must be a Map.')
  const headById = new Map(headRegistry.windows.map((window) => [window.id, window]))

  for (const baseWindow of baseRegistry.windows) {
    const headWindow = headById.get(baseWindow.id)
    const baseBytes = baseAdrSources.get(baseWindow.id)
    const headBytes = headAdrSources.get(baseWindow.id)
    invariant(
      Buffer.isBuffer(baseBytes) && Buffer.isBuffer(headBytes),
      `Compatibility window ${baseWindow.id} ADR history is missing.`,
    )
    if (baseWindow.status === 'accepted' || headWindow.status === 'proposed') {
      invariant(
        headBytes.equals(baseBytes),
        `Compatibility window history is immutable; ADR ${baseWindow.adr} changed.`,
      )
      continue
    }

    const baseSource = decodeAdrSource(baseBytes, `Base compatibility window ${baseWindow.id} ADR`)
    const headSource = decodeAdrSource(headBytes, `Head compatibility window ${baseWindow.id} ADR`)
    invariant(
      normalizeAcceptedAdrToProposed(headWindow, headSource) === baseSource,
      `Compatibility window ${baseWindow.id} ADR may change only its status and approval-evidence lines during approval.`,
    )
  }
}

export function resolveCompatibilityWindow({
  baseErrorCatalogArtifact,
  baseOpenApiSource,
  baseRef,
  headErrorCatalogArtifact,
  headOpenApiSource,
  registrySource,
}) {
  invariant(
    typeof baseRef === 'string' && /^[0-9a-f]{40}$/u.test(baseRef),
    'Compatibility window baseRef must be an exact lowercase 40-character Git SHA.',
  )
  invariant(typeof baseOpenApiSource === 'string', 'Compatibility window base OpenAPI source is required.')
  invariant(typeof headOpenApiSource === 'string', 'Compatibility window head OpenAPI source is required.')
  const registry = parseCompatibilityWindowRegistry(registrySource)
  const window = registry.windows.find((entry) => entry.baseRef === baseRef)
  if (window === undefined) {
    return undefined
  }

  const baseDigest = sha256(baseOpenApiSource)
  const headDigest = sha256(headOpenApiSource)
  invariant(
    baseDigest === window.baseOpenApiSha256,
    `Compatibility window mismatch for ${window.id}: base OpenAPI SHA-256 is ${baseDigest}, expected ${window.baseOpenApiSha256}.`,
  )
  invariant(
    headDigest === window.headOpenApiSha256,
    `Compatibility window mismatch for ${window.id}: head OpenAPI SHA-256 is ${headDigest}, expected ${window.headOpenApiSha256}.`,
  )
  if (isDigestBoundCompatibilityWindow(window)) {
    const baseErrorCatalog = inspectFatalUtf8Artifact(
      baseErrorCatalogArtifact,
      `Compatibility window ${window.id} base error catalog`,
    )
    const headErrorCatalog = inspectFatalUtf8Artifact(
      headErrorCatalogArtifact,
      `Compatibility window ${window.id} head error catalog`,
    )
    invariant(
      baseErrorCatalog.digest === window.baseErrorCatalogSha256,
      `Compatibility window mismatch for ${window.id}: base error-catalog SHA-256 is ${baseErrorCatalog.digest}, expected ${window.baseErrorCatalogSha256}.`,
    )
    invariant(
      headErrorCatalog.digest === window.headErrorCatalogSha256,
      `Compatibility window mismatch for ${window.id}: head error-catalog SHA-256 is ${headErrorCatalog.digest}, expected ${window.headErrorCatalogSha256}.`,
    )
  }
  return window
}

export function requireAcceptedCompatibilityWindow(window) {
  invariant(window !== undefined, 'Compatibility window is required.')
  if (window.status !== 'accepted') {
    throw new ContractFailure(
      `Compatibility window ${window.id} is pending approval; the exact base, target, and diagnostics match, but status is proposed and no permanent approval evidence is registered.`,
    )
  }
  return window
}
