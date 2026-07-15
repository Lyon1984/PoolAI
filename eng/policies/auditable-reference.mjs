import { isIP } from 'node:net'

const forbiddenHosts = new Set(['localhost', 'example.com', 'example.org', 'example.net'])
const forbiddenHostSuffixes = ['.example.com', '.example.org', '.example.net']
const reservedHostSuffixes = ['.test', '.example', '.invalid', '.localhost', '.local']
const placeholderReferenceTokens = new Set([
  'dummy',
  'example',
  'fake',
  'placeholder',
  'sample',
  'unknown',
  'unassigned',
])
const placeholderTaskPrefixes = new Set([
  'DEMO',
  'DUMMY',
  'EXAMPLE',
  'FAKE',
  'SAMPLE',
  'TASK',
  'TBD',
  'TEMP',
  'TEST',
  'TODO',
  'UNKNOWN',
])
const placeholderOwnerTokens = new Set([
  'dummy',
  'example',
  'fake',
  'pending',
  'placeholder',
  'sample',
  'tba',
  'tbd',
  'todo',
  'unassigned',
  'unknown',
  'unset',
])
const deeplyEncodedExampleToken = Array.from({ length: 16 }).reduce(
  (value) => encodeURIComponent(value),
  '%65%78%61%6d%70%6c%65',
)

export const isAuditableHttpsReference = (value) => {
  if (typeof value !== 'string' || value.length < 24 || value.length > 2048) {
    return false
  }
  try {
    const url = new URL(value)
    const hostname = url.hostname.toLowerCase()
    const address = hostname.startsWith('[') && hostname.endsWith(']')
      ? hostname.slice(1, -1)
      : hostname
    let decodedReference = `${hostname}${url.pathname}${url.search}${url.hash}`
    let decodingStable = false
    for (let decodePass = 0; decodePass < 16; decodePass += 1) {
      const next = decodeURIComponent(decodedReference)
      if (next === decodedReference) {
        decodingStable = true
        break
      }
      decodedReference = next
    }
    if (!decodingStable) {
      return false
    }
    const referenceTokens = decodedReference.toLowerCase().split(/[^a-z0-9]+/u).filter(Boolean)
    return url.protocol === 'https:'
      && url.username === ''
      && url.password === ''
      && !hostname.endsWith('.')
      && hostname.includes('.')
      && isIP(address) === 0
      && !reservedHostSuffixes.some((suffix) => hostname.endsWith(suffix))
      && !forbiddenHosts.has(hostname)
      && !forbiddenHostSuffixes.some((suffix) => hostname.endsWith(suffix))
      && !referenceTokens.some((token) => placeholderReferenceTokens.has(token))
      && /[A-Za-z0-9_-]{4,}/u.test(url.pathname)
  } catch {
    return false
  }
}

export const isAuditableTaskId = (value) => {
  if (isAuditableHttpsReference(value)) {
    return true
  }
  if (typeof value !== 'string') {
    return false
  }
  const match = value.match(/^([A-Z][A-Z0-9]{1,9})-[1-9]\d*$/u)
  return match !== null && !placeholderTaskPrefixes.has(match[1])
}

export const canonicalAuditableTaskId = (value) => {
  if (!isAuditableTaskId(value)) {
    return null
  }
  try {
    const url = new URL(value)
    url.hash = ''
    return url.href
  } catch {
    return value
  }
}

export const isNamedOwner = (value) => {
  if (typeof value !== 'string'
      || !/^[\p{L}\p{N}@][\p{L}\p{N} .@_-]{1,63}$/u.test(value)) {
    return false
  }
  const normalized = value.trim().toLowerCase()
    .replaceAll(/[./_-]+/gu, ' ')
    .replaceAll(/\s+/gu, ' ')
  const tokens = normalized.split(' ')
  return !tokens.some((token) => placeholderOwnerTokens.has(token))
    && !/^(?:na|n a|none|null|x|not assigned|to be assigned|owner(?: \d+)?)$/u.test(normalized)
}

export const auditableReferencePolicyCases = Object.freeze({
  invalid: Object.freeze([
    'https://fake.invalid/evidence-1234',
    'https://127.0.0.1/evidence-1234',
    'https://foo.localhost/evidence-1234',
    'https://[::1]/evidence-1234',
    'https://tasks.example.com/evidence-1234',
    'https://tasks.example.com./evidence-1234',
    'https://localhost./evidence-1234',
    'https://tasks.test./evidence-1234',
    'https://github.com/example-owner/example-repository/actions/runs/123456',
    `https://github.com/dotnet/${deeplyEncodedExampleToken}/actions/runs/123456789`,
  ]),
  valid: 'https://github.com/dotnet/runtime/actions/runs/123456789',
})

export const namedOwnerPolicyCases = Object.freeze({
  invalid: Object.freeze([
    'unknown',
    'unassigned',
    'Unknown Team',
    'Unassigned Team',
    'TBD Team',
    'TBA Team',
    'Placeholder Team',
    'Fake Owner',
    'Fake Platform Team',
    'Example-Owner',
    'Example Platform Team',
    'Sample Team',
    'Owner 12',
    'To Be Assigned',
  ]),
  valid: 'PoolAI Platform Team',
})

export const taskIdPolicyCases = Object.freeze({
  invalid: Object.freeze([
    'DEMO-1',
    'DUMMY-1',
    'EXAMPLE-1',
    'FAKE-1',
    'SAMPLE-1',
    'TASK-1',
    'TBD-1',
    'TEMP-1',
    'TEST-1',
    'TODO-1',
    'UNKNOWN-1',
  ]),
  valid: 'POOL-123',
  equivalentUrls: Object.freeze([
    'https://github.com/dotnet/runtime/issues/12345#issuecomment-1',
    'https://GITHUB.COM:443/dotnet/runtime/issues/12345',
  ]),
})
