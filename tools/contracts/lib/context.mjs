import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

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
  errorCatalog: path.join(repoRoot, 'docs/contracts/error-catalog.md'),
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

export async function loadContractSources() {
  const [openApiSource, errorCatalogSource] = await Promise.all([
    readFile(contractPaths.openApi, 'utf8'),
    readFile(contractPaths.errorCatalog, 'utf8'),
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
    errorCatalogSource,
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
