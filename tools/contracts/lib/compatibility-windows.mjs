import { readFileSync, realpathSync } from 'node:fs'
import path from 'node:path'
import { TextDecoder } from 'node:util'

import { withReadOnlyRepositoryFile } from '../../../eng/policies/repository-file.mjs'
import { ContractFailure, invariant, repoRoot, sha256, stableJson, YAML } from './context.mjs'

const WINDOW_REGISTRY_KEYS = ['schemaVersion', 'windows']
const WINDOW_KEYS = [
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
const WINDOW_SCOPE = 'openapi-v1-compatibility-window'
const EXACT_APPROVAL_CONTROL = 'https://github.com/Lyon1984/PoolAI/issues/44'

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
  invariant(json.schemaVersion === 1, 'Compatibility window registry must use schemaVersion 1.')
  invariant(Array.isArray(json.windows), 'Compatibility window registry windows must be an array.')
  invariant(
    json.windows.length > 0,
    'Compatibility window registry schemaVersion 1 must contain at least one window.',
  )

  const ids = new Set()
  const baseRefs = new Set()
  for (const [index, window] of json.windows.entries()) {
    const label = `Compatibility window registry windows[${index}]`
    requireExactKeys(window, WINDOW_KEYS, label)
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
    for (const [failureIndex, failure] of window.allowedFailures.entries()) {
      invariant(
        typeof failure === 'string' && failure.startsWith('#/') && failure.includes(': ') &&
          !failure.includes('\n') && !failure.includes('\r'),
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

function approvalEvidenceLine(window) {
  return window.status === 'accepted'
    ? `- Approval evidence: [Issue approval comment](${window.approvalEvidence})`
    : '- Approval evidence: **Pending explicit approval**'
}

export function validateCompatibilityWindowDecisionSource(window, source) {
  invariant(typeof source === 'string', `Compatibility window ${window.id} ADR source is required.`)
  const status = window.status === 'accepted' ? 'Accepted' : 'Proposed'
  const issueNumber = window.approvalControl.split('/').at(-1)
  const requiredLines = [
    `- Status: **${status}**`,
    `- Compatibility window ID: \`${window.id}\``,
    `- Base Git commit: \`${window.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${window.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${window.headOpenApiSha256}\``,
    `- Approval control: [Issue #${issueNumber}](${window.approvalControl})`,
    approvalEvidenceLine(window),
    ...window.allowedFailures.map((failure) => `- Allowed diagnostic: \`${failure}\``),
  ]
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
    return { baseRegistry: undefined, headRegistry }
  }

  const baseRegistry = parseCompatibilityWindowRegistry(baseRegistrySource)
  const headById = new Map(headRegistry.windows.map((window) => [window.id, window]))
  for (const baseWindow of baseRegistry.windows) {
    const headWindow = headById.get(baseWindow.id)
    invariant(
      headWindow !== undefined,
      `Compatibility window history is immutable; ${baseWindow.id} was removed.`,
    )
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
  baseOpenApiSource,
  baseRef,
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
