import { chmod, mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { homedir, platform, arch } from 'node:os'
import { resolve } from 'node:path'

import { downloadVerifiedReleaseArtifact } from './verified-download.mjs'

const root = resolve(import.meta.dirname, '..', '..')
const versions = JSON.parse(await readFile(resolve(root, 'eng', 'versions.json'), 'utf8'))

if (platform() !== 'linux' || arch() !== 'x64') {
  throw new Error(`The CI Docker plugin installer supports linux/x64 only; found ${platform()}/${arch()}.`)
}

const dockerConfigDirectory = process.env.DOCKER_CONFIG?.trim() || resolve(homedir(), '.docker')
const pluginDirectory = resolve(dockerConfigDirectory, 'cli-plugins')
await mkdir(pluginDirectory, { recursive: true })

await installPlugin(
  'dockerBuildxLinuxX64',
  versions.ciTooling.dockerBuildxLinuxX64,
  'docker-buildx',
)
await installPlugin(
  'dockerComposeLinuxX64',
  versions.ciTooling.dockerComposeLinuxX64,
  'docker-compose',
)

async function installPlugin(artifactName, lock, filename) {
  const { bytes, label, version } = await downloadVerifiedReleaseArtifact(artifactName, lock)

  const target = resolve(pluginDirectory, filename)
  const temporary = `${target}.tmp-${process.pid}`
  await writeFile(temporary, bytes, { mode: 0o755 })
  await chmod(temporary, 0o755)
  await rm(target, { force: true })
  await rename(temporary, target)
  console.log(`Installed ${label} ${version} with verified SHA-256.`)
}
