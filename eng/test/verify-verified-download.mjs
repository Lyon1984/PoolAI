import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'

import versions from '../versions.json' with { type: 'json' }
import {
  downloadVerifiedReleaseArtifact,
  validateVerifiedReleaseLock,
  verifiedReleaseArtifactNames,
  verifiedReleaseArtifactUrl,
} from '../ci/verified-download.mjs'

const productionLocks = new Map([
  ['dockerBuildxLinuxX64', versions.ciTooling.dockerBuildxLinuxX64],
  ['dockerComposeLinuxX64', versions.ciTooling.dockerComposeLinuxX64],
  ['syftLinuxX64', versions.securityTooling.syft],
])

assert.deepEqual([...productionLocks.keys()], verifiedReleaseArtifactNames)
for (const [artifactName, lock] of productionLocks) {
  validateVerifiedReleaseLock(artifactName, lock)
  const url = verifiedReleaseArtifactUrl(artifactName, lock.version)
  assert.equal(url.protocol, 'https:')
  assert.equal(url.origin, 'https://github.com')
  assert.ok(url.pathname.includes(lock.version))
}

const payload = Buffer.from('verified release bytes\n', 'utf8')
const payloadSha256 = createHash('sha256').update(payload).digest('hex')
const testLock = Object.freeze({ version: '1.2.3', sha256: payloadSha256 })

const responseFor = (body, {
  chunks = [body],
  contentLength = String(body.length),
  ok = true,
  streamState = {},
  status = 200,
  url = 'https://release-assets.githubusercontent.com/github-production-release-asset/test',
} = {}) => {
  let chunkIndex = 0
  return {
    body: {
      getReader: () => ({
        cancel: async () => { streamState.cancelled = true },
        read: async () => {
          streamState.reads = (streamState.reads ?? 0) + 1
          if (chunkIndex >= chunks.length) {
            return { done: true, value: undefined }
          }
          const value = Uint8Array.from(chunks[chunkIndex])
          chunkIndex += 1
          return { done: false, value }
        },
        releaseLock: () => { streamState.released = true },
      }),
    },
    headers: {
      get: (name) => name.toLowerCase() === 'content-length' ? contentLength : null,
    },
    ok,
    status,
    url,
  }
}

const requests = []
const successful = await downloadVerifiedReleaseArtifact(
  'dockerBuildxLinuxX64',
  testLock,
  {
    fetchImplementation: async (url, options) => {
      requests.push({ options, url: url.href })
      return responseFor(payload)
    },
  },
)
assert.deepEqual(successful.bytes, payload)
assert.equal(successful.version, testLock.version)
assert.equal(requests.length, 1)
assert.equal(requests[0].options.redirect, 'follow')
assert.equal(
  requests[0].url,
  'https://github.com/docker/buildx/releases/download/v1.2.3/buildx-v1.2.3.linux-amd64',
)

for (const invalidVersion of ['../../escape', '1.2.3?query', '1.2.3\nnext', 'v1.2.3']) {
  let fetchCalls = 0
  await assert.rejects(
    downloadVerifiedReleaseArtifact(
      'dockerBuildxLinuxX64',
      { version: invalidVersion, sha256: payloadSha256 },
      { fetchImplementation: async () => { fetchCalls += 1 } },
    ),
    /invalid exact version/u,
  )
  assert.equal(fetchCalls, 0)
}

let invalidDigestFetchCalls = 0
await assert.rejects(
  downloadVerifiedReleaseArtifact(
    'dockerBuildxLinuxX64',
    { version: '1.2.3', sha256: 'not-a-digest' },
    { fetchImplementation: async () => { invalidDigestFetchCalls += 1 } },
  ),
  /invalid SHA-256/u,
)
assert.equal(invalidDigestFetchCalls, 0)

let postVerificationWrites = 0
await assert.rejects(
  (async () => {
    await downloadVerifiedReleaseArtifact(
      'dockerBuildxLinuxX64',
      { version: '1.2.3', sha256: '0'.repeat(64) },
      { fetchImplementation: async () => responseFor(payload) },
    )
    postVerificationWrites += 1
  })(),
  /checksum mismatch/u,
)
assert.equal(postVerificationWrites, 0)

await assert.rejects(
  downloadVerifiedReleaseArtifact(
    'dockerBuildxLinuxX64',
    testLock,
    { fetchImplementation: async () => responseFor(payload, { ok: false, status: 503 }) },
  ),
  /HTTP 503/u,
)

await assert.rejects(
  downloadVerifiedReleaseArtifact(
    'dockerBuildxLinuxX64',
    testLock,
    {
      fetchImplementation: async () => responseFor(
        payload,
        { url: 'https://example.test/untrusted-redirect' },
      ),
    },
  ),
  /left the trusted GitHub release origins/u,
)

const boundedStreamState = {}
await assert.rejects(
  downloadVerifiedReleaseArtifact(
    'dockerBuildxLinuxX64',
    testLock,
    {
      fetchImplementation: async () => responseFor(payload, { contentLength: '4096' }),
      maximumBytes: 1024,
    },
  ),
  /exceeds the 1024-byte limit/u,
)

await assert.rejects(
  downloadVerifiedReleaseArtifact(
    'dockerBuildxLinuxX64',
    testLock,
    {
      fetchImplementation: async () => responseFor(
        Buffer.alloc(32),
        {
          chunks: [Buffer.alloc(8), Buffer.alloc(8), Buffer.alloc(1024)],
          contentLength: null,
          streamState: boundedStreamState,
        },
      ),
      maximumBytes: 10,
    },
  ),
  /exceeds the 10-byte limit/u,
)
assert.equal(boundedStreamState.reads, 2)
assert.equal(boundedStreamState.cancelled, true)
assert.equal(boundedStreamState.released, true)

console.log(
  `Verified release download policy valid for ${productionLocks.size} locked artifacts; `
    + 'invalid locks, redirects, streamed size limits, HTTP failures, and checksum mismatches fail closed.',
)
