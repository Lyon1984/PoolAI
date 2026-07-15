import { readdir, readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const read = async (path) => readFile(resolve(root, path), 'utf8')
const readJson = async (path) => JSON.parse(await read(path))
const failures = []
const exactVersion = /^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/
const sha256 = /^[0-9a-f]{64}$/
const gitSha = /^[0-9a-f]{40}$/

const expectEqual = (label, actual, expected) => {
  if (actual !== expected) {
    failures.push(`${label}: expected ${expected}, found ${actual}`)
  }
}

const versions = await readJson('eng/versions.json')
const globalJson = await readJson('global.json')
const toolManifest = await readJson('.config/dotnet-tools.json')
const nodeVersion = (await read('.node-version')).trim()

expectEqual('global.json SDK', globalJson.sdk.version, versions.toolchains.dotnetSdk)
expectEqual('global.json rollForward', globalJson.sdk.rollForward, 'disable')
expectEqual('.node-version', nodeVersion, versions.toolchains.node)
expectEqual('dotnet-ef', toolManifest.tools['dotnet-ef'].version, versions.toolchains.dotnetRuntime)

const packageProps = await read('Directory.Packages.props')
const packageVersionPattern = /<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"\s*\/>/g
const packageVersions = [...packageProps.matchAll(packageVersionPattern)]

if (packageVersions.length === 0) {
  failures.push('Directory.Packages.props contains no centrally managed versions')
}

for (const [, name, version] of packageVersions) {
  if (!exactVersion.test(version)) {
    failures.push(`${name} is not pinned to an exact semantic version: ${version}`)
  }
}

const frontendPackage = await readJson('frontend/package.json')
expectEqual('frontend packageManager', frontendPackage.packageManager, `pnpm@${versions.toolchains.pnpm}`)
expectEqual('frontend Node engine', frontendPackage.engines?.node, versions.toolchains.node)
expectEqual('frontend pnpm engine', frontendPackage.engines?.pnpm, versions.toolchains.pnpm)
expectEqual('Vue', frontendPackage.dependencies?.vue, versions.frontend.vue)
expectEqual('Vue Router', frontendPackage.dependencies?.['vue-router'], versions.frontend.vueRouter)
expectEqual('Pinia', frontendPackage.dependencies?.pinia, versions.frontend.pinia)
expectEqual('Vite', frontendPackage.devDependencies?.vite, versions.frontend.vite)
expectEqual('TypeScript', frontendPackage.devDependencies?.typescript, versions.frontend.typescript)
expectEqual('Playwright', frontendPackage.devDependencies?.['@playwright/test'], versions.frontend.playwright)
expectEqual('@vitest/coverage-v8', frontendPackage.devDependencies?.['@vitest/coverage-v8'], versions.frontend.vitestCoverageV8)

for (const [section, dependencies] of Object.entries({
  dependencies: frontendPackage.dependencies,
  devDependencies: frontendPackage.devDependencies,
})) {
  for (const [name, version] of Object.entries(dependencies ?? {})) {
    if (!exactVersion.test(version)) {
      failures.push(`frontend ${section}.${name} is not exact: ${version}`)
    }
  }
}

const workflowDirectory = resolve(root, '.github', 'workflows')
const workflowNames = (await readdir(workflowDirectory))
  .filter((name) => name.endsWith('.yml') || name.endsWith('.yaml'))
  .sort()
const workflows = await Promise.all(workflowNames.map(async (name) => ({
  name,
  contents: await read(`.github/workflows/${name}`),
})))
const registeredActions = Object.keys(versions.githubActions).sort((left, right) => right.length - left.length)
const usedActions = new Set()

for (const workflow of workflows) {
  for (const line of workflow.contents.split('\n')) {
    const use = line.match(/^\s*(?:-\s*)?uses:\s*['"]?([^'"\s#]+)['"]?/)
    if (use === null || use[1].startsWith('./')) {
      continue
    }

    const separator = use[1].lastIndexOf('@')
    if (separator < 1) {
      failures.push(`${workflow.name} contains an invalid action reference: ${use[1]}`)
      continue
    }

    const actionPath = use[1].slice(0, separator)
    const reference = use[1].slice(separator + 1)
    const action = registeredActions.find((candidate) => actionPath === candidate || actionPath.startsWith(`${candidate}/`))
    if (action === undefined) {
      failures.push(`${workflow.name} uses unregistered action ${actionPath}`)
      continue
    }

    const lock = versions.githubActions[action]
    usedActions.add(action)
    if (!gitSha.test(reference)) {
      failures.push(`${workflow.name} does not pin ${actionPath} to a full commit SHA: ${reference}`)
    } else if (reference !== lock.sha) {
      failures.push(`${workflow.name} pins ${actionPath} to ${reference}, expected ${lock.sha}`)
    }
  }
}

for (const [action, lock] of Object.entries(versions.githubActions)) {
  if (!exactVersion.test(lock.version)) {
    failures.push(`GitHub Action ${action} has an invalid release version: ${lock.version}`)
  }
  if (!gitSha.test(lock.sha)) {
    failures.push(`GitHub Action ${action} has an invalid commit SHA: ${lock.sha}`)
  }
  if (!usedActions.has(action)) {
    failures.push(`GitHub Action ${action} is locked but unused by .github/workflows`)
  }
}

for (const [name, image] of Object.entries(versions.containers)) {
  if (image.endsWith(':latest') || !image.includes(':')) {
    failures.push(`container ${name} is not pinned to an exact release tag: ${image}`)
  }
}

for (const [name, tool] of Object.entries(versions.localTooling)) {
  if (typeof tool === 'string') {
    if (!exactVersion.test(tool)) {
      failures.push(`local tool ${name} is not pinned to an exact version: ${tool}`)
    }
    continue
  }

  if (!exactVersion.test(tool.version)) {
    failures.push(`local tool ${name} is not pinned to an exact version: ${tool.version}`)
  }
  if (!sha256.test(tool.sha256)) {
    failures.push(`local tool ${name} has an invalid SHA-256: ${tool.sha256}`)
  }
}

for (const [name, tool] of Object.entries(versions.ciTooling ?? {})) {
  if (!exactVersion.test(tool.version)) {
    failures.push(`CI tool ${name} is not pinned to an exact version: ${tool.version}`)
  }
  if (!sha256.test(tool.sha256)) {
    failures.push(`CI tool ${name} has an invalid SHA-256: ${tool.sha256}`)
  }
}

for (const [name, tool] of Object.entries(versions.securityTooling ?? {})) {
  if (typeof tool === 'string') {
    if (!exactVersion.test(tool)) {
      failures.push(`security tool ${name} is not pinned to an exact version: ${tool}`)
    }
    continue
  }

  if (!exactVersion.test(tool.version)) {
    failures.push(`security tool ${name} is not pinned to an exact version: ${tool.version}`)
  }
  if (!sha256.test(tool.sha256)) {
    failures.push(`security tool ${name} has an invalid SHA-256: ${tool.sha256}`)
  }
}

const workflowText = workflows.map((workflow) => workflow.contents).join('\n')
for (const [name, container] of Object.entries(versions.securityContainers ?? {})) {
  if (container.image.endsWith(':latest') || !container.image.includes(':')) {
    failures.push(`security container ${name} is not pinned to an exact release tag: ${container.image}`)
  }
  if (!/^sha256:[0-9a-f]{64}$/.test(container.digest)) {
    failures.push(`security container ${name} has an invalid manifest digest: ${container.digest}`)
  }
  if (!workflowText.includes(`${container.image}@${container.digest}`)) {
    failures.push(`security container ${name} is not used by digest in .github/workflows`)
  }
}

if (!workflowText.includes(`version: v${versions.securityTooling.trivy}`)) {
  failures.push(`Trivy ${versions.securityTooling.trivy} is not explicitly selected in .github/workflows`)
}
if (!workflowText.includes('node eng/ci/install-linux-syft.mjs')
    || !workflowText.includes('$POOLAI_CI_BIN/syft')) {
  failures.push(`Syft ${versions.securityTooling.syft.version} is not installed and invoked through the checksum-verifying repository script`)
}

for (const [name, digest] of Object.entries(versions.containerDigests ?? {})) {
  if (!versions.containers[name]) {
    failures.push(`container digest ${name} has no matching tagged image`)
  }
  if (!/^sha256:[0-9a-f]{64}$/.test(digest)) {
    failures.push(`container ${name} has an invalid manifest digest: ${digest}`)
  }
}

if (failures.length > 0) {
  console.error(failures.map((failure) => `- ${failure}`).join('\n'))
  process.exitCode = 1
} else {
  console.log(`Version locks valid: ${packageVersions.length} NuGet packages, ${Object.keys(frontendPackage.dependencies ?? {}).length + Object.keys(frontendPackage.devDependencies ?? {}).length} frontend packages.`)
}
