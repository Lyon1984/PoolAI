import { createHash } from 'node:crypto'

const exactVersion = /^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/u
const sha256 = /^[0-9a-f]{64}$/u
const defaultMaximumBytes = 256 * 1024 * 1024

const releaseArtifacts = Object.freeze({
  dockerBuildxLinuxX64: Object.freeze({
    label: 'Docker Buildx',
    createUrl: (version) => new URL(
      `https://github.com/docker/buildx/releases/download/v${version}/buildx-v${version}.linux-amd64`,
    ),
  }),
  dockerComposeLinuxX64: Object.freeze({
    label: 'Docker Compose',
    createUrl: (version) => new URL(
      `https://github.com/docker/compose/releases/download/v${version}/docker-compose-linux-x86_64`,
    ),
  }),
  syftLinuxX64: Object.freeze({
    label: 'Syft',
    createUrl: (version) => new URL(
      `https://github.com/anchore/syft/releases/download/v${version}/syft_${version}_linux_amd64.tar.gz`,
    ),
  }),
})

const trustedResponseOrigins = new Set([
  'https://github.com',
  'https://objects.githubusercontent.com',
  'https://release-assets.githubusercontent.com',
])

const requireArtifact = (artifactName) => {
  const artifact = releaseArtifacts[artifactName]
  if (artifact === undefined) {
    throw new Error(`Unknown verified release artifact: ${artifactName}.`)
  }
  return artifact
}

const requireTrustedResponseUrl = (value, label) => {
  let url
  try {
    url = new URL(value)
  } catch {
    throw new Error(`${label} download returned an invalid response URL.`)
  }

  if (url.protocol !== 'https:' || !trustedResponseOrigins.has(url.origin)) {
    throw new Error(`${label} download left the trusted GitHub release origins: ${url.origin}.`)
  }
}

const readBoundedResponseBody = async (response, maximumBytes, label) => {
  if (typeof response.body?.getReader !== 'function') {
    throw new Error(`${label} download returned no readable byte stream.`)
  }

  const reader = response.body.getReader()
  const chunks = []
  let totalBytes = 0
  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) {
        break
      }
      if (!(value instanceof Uint8Array)) {
        throw new Error(`${label} download returned a non-byte stream chunk.`)
      }

      totalBytes += value.byteLength
      if (totalBytes > maximumBytes) {
        try {
          await reader.cancel()
        } catch {
          // The size violation remains authoritative if stream cancellation also fails.
        }
        throw new Error(`${label} download exceeds the ${maximumBytes}-byte limit.`)
      }
      chunks.push(Buffer.from(value))
    }
  } finally {
    reader.releaseLock?.()
  }

  return Buffer.concat(chunks, totalBytes)
}

export const verifiedReleaseArtifactNames = Object.freeze(Object.keys(releaseArtifacts))

export const validateVerifiedReleaseLock = (label, lock) => {
  if (lock === null || typeof lock !== 'object' || Array.isArray(lock)) {
    throw new Error(`${label} release lock must be an object.`)
  }
  if (typeof lock.version !== 'string' || !exactVersion.test(lock.version)) {
    throw new Error(`${label} release lock has an invalid exact version.`)
  }
  if (typeof lock.sha256 !== 'string' || !sha256.test(lock.sha256)) {
    throw new Error(`${label} release lock has an invalid SHA-256.`)
  }
  return Object.freeze({ version: lock.version, sha256: lock.sha256 })
}

export const verifiedReleaseArtifactUrl = (artifactName, version) => {
  const artifact = requireArtifact(artifactName)
  const lock = validateVerifiedReleaseLock(artifact.label, {
    version,
    sha256: '0'.repeat(64),
  })
  const url = artifact.createUrl(lock.version)
  if (url.protocol !== 'https:' || url.origin !== 'https://github.com') {
    throw new Error(`${artifact.label} release URL must use the fixed GitHub HTTPS origin.`)
  }
  return url
}

export const downloadVerifiedReleaseArtifact = async (
  artifactName,
  lock,
  {
    fetchImplementation = globalThis.fetch,
    maximumBytes = defaultMaximumBytes,
  } = {},
) => {
  const artifact = requireArtifact(artifactName)
  const verifiedLock = validateVerifiedReleaseLock(artifact.label, lock)
  const url = verifiedReleaseArtifactUrl(artifactName, verifiedLock.version)

  if (typeof fetchImplementation !== 'function') {
    throw new Error(`${artifact.label} download requires a fetch implementation.`)
  }
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes <= 0) {
    throw new Error(`${artifact.label} maximum download size must be a positive safe integer.`)
  }

  const response = await fetchImplementation(url, { redirect: 'follow' })
  if (!response?.ok) {
    throw new Error(`${artifact.label} download failed with HTTP ${response?.status ?? 'unknown'}.`)
  }
  if (typeof response.url === 'string' && response.url.length > 0) {
    requireTrustedResponseUrl(response.url, artifact.label)
  }

  const contentLength = response.headers?.get?.('content-length')
  if (typeof contentLength === 'string' && /^\d+$/u.test(contentLength)
      && Number(contentLength) > maximumBytes) {
    throw new Error(`${artifact.label} download exceeds the ${maximumBytes}-byte limit.`)
  }
  const bytes = await readBoundedResponseBody(response, maximumBytes, artifact.label)

  const actualSha256 = createHash('sha256').update(bytes).digest('hex')
  if (actualSha256 !== verifiedLock.sha256) {
    throw new Error(
      `${artifact.label} checksum mismatch: expected ${verifiedLock.sha256}, found ${actualSha256}.`,
    )
  }

  return Object.freeze({
    bytes,
    label: artifact.label,
    url: url.href,
    version: verifiedLock.version,
  })
}
