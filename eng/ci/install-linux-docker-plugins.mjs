import { createHash } from 'node:crypto'
import { chmod, mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { homedir, platform, arch } from 'node:os'
import { resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const versions = JSON.parse(await readFile(resolve(root, 'eng', 'versions.json'), 'utf8'))

if (platform() !== 'linux' || arch() !== 'x64') {
  throw new Error(`The CI Docker plugin installer supports linux/x64 only; found ${platform()}/${arch()}.`)
}

const dockerConfigDirectory = process.env.DOCKER_CONFIG?.trim() || resolve(homedir(), '.docker')
const pluginDirectory = resolve(dockerConfigDirectory, 'cli-plugins')
await mkdir(pluginDirectory, { recursive: true })

await installPlugin(
  'Docker Buildx',
  versions.ciTooling.dockerBuildxLinuxX64,
  (version) => `https://github.com/docker/buildx/releases/download/v${version}/buildx-v${version}.linux-amd64`,
  'docker-buildx',
)
await installPlugin(
  'Docker Compose',
  versions.ciTooling.dockerComposeLinuxX64,
  (version) => `https://github.com/docker/compose/releases/download/v${version}/docker-compose-linux-x86_64`,
  'docker-compose',
)

async function installPlugin(label, lock, createUrl, filename) {
  const response = await fetch(createUrl(lock.version), { redirect: 'follow' })
  if (!response.ok) {
    throw new Error(`${label} download failed with HTTP ${response.status}.`)
  }

  const bytes = Buffer.from(await response.arrayBuffer())
  const actualSha256 = createHash('sha256').update(bytes).digest('hex')
  if (actualSha256 !== lock.sha256) {
    throw new Error(`${label} checksum mismatch: expected ${lock.sha256}, found ${actualSha256}.`)
  }

  const target = resolve(pluginDirectory, filename)
  const temporary = `${target}.tmp-${process.pid}`
  await writeFile(temporary, bytes, { mode: 0o755 })
  await chmod(temporary, 0o755)
  await rm(target, { force: true })
  await rename(temporary, target)
  console.log(`Installed ${label} ${lock.version} with verified SHA-256.`)
}
