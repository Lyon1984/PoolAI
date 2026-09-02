import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { TextDecoder } from 'node:util'

import { withReadOnlyRepositoryFile } from '../../../eng/policies/repository-file.mjs'

const requireFromFrontend = createRequire(
  new URL('../../../frontend/package.json', import.meta.url),
)

function loadFrontendDependency(name) {
  try {
    return requireFromFrontend(name)
  } catch (error) {
    throw new Error(
      `Contract tooling dependency ${name} is unavailable. Run pnpm --dir frontend install --frozen-lockfile first.`,
      { cause: error },
    )
  }
}

export const YAML = loadFrontendDependency('yaml')
export const Ajv2020 = loadFrontendDependency('ajv/dist/2020').default
export const addFormats = loadFrontendDependency('ajv-formats').default

export const repoRoot = fileURLToPath(new URL('../../..', import.meta.url))
export const contractPaths = Object.freeze({
  openApi: path.join(repoRoot, 'docs/contracts/openapi-v1.yaml'),
  compatibilityResets: path.join(
    repoRoot,
    'docs/contracts/compatibility-resets-v1.json',
  ),
  compatibilityWindows: path.join(
    repoRoot,
    'docs/contracts/compatibility-windows-v1.json',
  ),
  errorCatalog: path.join(repoRoot, 'docs/contracts/error-catalog.md'),
  groupQuotaEvents: path.join(repoRoot, 'docs/contracts/group-quota-events-v1.json'),
  fixtures: path.join(repoRoot, 'docs/contracts/fixtures'),
  database: path.join(repoRoot, 'docs/database'),
  generatedTypeScript: path.join(
    repoRoot,
    'frontend/src/api/generated/openapi-v1.ts',
  ),
  generatedTypeScriptErrors: path.join(
    repoRoot,
    'frontend/src/api/generated/error-codes-v1.ts',
  ),
  generatedCSharp: path.join(
    repoRoot,
    'src/PoolAI.Contracts/Generated/OpenApiV1.g.cs',
  ),
  generatedCSharpErrors: path.join(
    repoRoot,
    'src/PoolAI.Contracts/Generated/ErrorCodesV1.g.cs',
  ),
})

export class ContractFailure extends Error {
  constructor(message) {
    super(message)
    this.name = 'ContractFailure'
  }
}

export function invariant(condition, message) {
  if (!condition) {
    throw new ContractFailure(message)
  }
}

const utf8ArtifactBytes = new WeakMap()
const trustedHeadErrorCatalogArtifacts = new WeakSet()

export function createFatalUtf8Artifact(value, label) {
  invariant(Buffer.isBuffer(value), `${label} must be bytes.`)
  const bytes = Buffer.from(value)
  const digest = sha256(bytes)
  let source
  try {
    source = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch (error) {
    throw new ContractFailure(`${label} must be valid UTF-8: ${error.message}`)
  }

  const artifact = Object.freeze({
    byteLength: bytes.length,
    digest,
    source,
  })
  utf8ArtifactBytes.set(artifact, bytes)
  return artifact
}

export function inspectFatalUtf8Artifact(value, label) {
  const bytes = value !== null && typeof value === 'object'
    ? utf8ArtifactBytes.get(value)
    : undefined
  invariant(bytes !== undefined, `${label} must be one fatal UTF-8 source artifact.`)
  return {
    bytes: Buffer.from(bytes),
    byteLength: value.byteLength,
    digest: value.digest,
    source: value.source,
  }
}

export function readCanonicalRepositoryUtf8Artifact(root, relativePath, label) {
  let bytes
  try {
    bytes = withReadOnlyRepositoryFile(
      root,
      relativePath,
      (descriptor) => readFileSync(descriptor),
    )
  } catch (error) {
    throw new ContractFailure(`${label} cannot be safely read: ${error.message}`)
  }
  return createFatalUtf8Artifact(bytes, label)
}

export function loadHeadErrorCatalogArtifact() {
  const artifact = readCanonicalRepositoryUtf8Artifact(
    repoRoot,
    'docs/contracts/error-catalog.md',
    'Head error catalog',
  )
  trustedHeadErrorCatalogArtifacts.add(artifact)
  return artifact
}

export function requireTrustedHeadErrorCatalogArtifact(value) {
  invariant(
    value !== null && typeof value === 'object' && trustedHeadErrorCatalogArtifacts.has(value),
    'Head error catalog must come from the canonical repository source loader.',
  )
  return inspectFatalUtf8Artifact(value, 'Head error catalog')
}

export async function loadContractSources() {
  const errorCatalogArtifact = loadHeadErrorCatalogArtifact()
  const [
    openApiSource,
    compatibilityResetSource,
    compatibilityWindowSource,
  ] = await Promise.all([
    readFile(contractPaths.openApi, 'utf8'),
    readFile(contractPaths.compatibilityResets, 'utf8'),
    readFile(contractPaths.compatibilityWindows, 'utf8'),
  ])

  const document = YAML.parseDocument(openApiSource, {
    prettyErrors: true,
    strict: true,
    uniqueKeys: true,
  })
  invariant(
    document.errors.length === 0,
    `OpenAPI YAML is invalid: ${document.errors.map((error) => error.message).join('; ')}`,
  )

  return {
    openApi: document.toJS({ maxAliasCount: 0 }),
    openApiSource,
    errorCatalogArtifact,
    errorCatalogSource: errorCatalogArtifact.source,
    compatibilityResetSource,
    compatibilityWindowSource,
  }
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex')
}

export function stableJson(value) {
  if (Array.isArray(value)) {
    return `[${value.map(stableJson).join(',')}]`
  }

  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableJson(value[key])}`)
      .join(',')}}`
  }

  return JSON.stringify(value)
}
