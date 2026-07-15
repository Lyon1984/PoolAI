import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { chmod, mkdir, rename, rm, writeFile } from 'node:fs/promises'
import { arch, platform } from 'node:os'
import { resolve } from 'node:path'

import versions from '../versions.json' with { type: 'json' }

if (platform() !== 'linux' || arch() !== 'x64') {
  throw new Error(`The CI Syft installer supports linux/x64 only; found ${platform()}/${arch()}.`)
}

const lock = versions.securityTooling.syft
const url = `https://github.com/anchore/syft/releases/download/v${lock.version}/syft_${lock.version}_linux_amd64.tar.gz`
const response = await fetch(url, { redirect: 'follow' })
if (!response.ok) {
  throw new Error(`Syft download failed with HTTP ${response.status}.`)
}

const archive = Buffer.from(await response.arrayBuffer())
const actualSha256 = createHash('sha256').update(archive).digest('hex')
if (actualSha256 !== lock.sha256) {
  throw new Error(`Syft checksum mismatch: expected ${lock.sha256}, found ${actualSha256}.`)
}

const binDirectory = process.env.POOLAI_CI_BIN?.trim()
  ? resolve(process.env.POOLAI_CI_BIN)
  : resolve(import.meta.dirname, '..', '..', '.tools', 'ci', 'bin')
const target = resolve(binDirectory, 'syft')
const temporaryDirectory = resolve(binDirectory, `.syft-install-${process.pid}`)
const archivePath = resolve(temporaryDirectory, 'syft.tar.gz')

await mkdir(temporaryDirectory, { recursive: true, mode: 0o700 })
try {
  await writeFile(archivePath, archive, { mode: 0o600 })
  execFileSync('tar', ['-xzf', archivePath, '-C', temporaryDirectory, 'syft'], {
    stdio: 'inherit',
  })
  await chmod(resolve(temporaryDirectory, 'syft'), 0o755)
  await rm(target, { force: true })
  await rename(resolve(temporaryDirectory, 'syft'), target)
} finally {
  await rm(temporaryDirectory, { recursive: true, force: true })
}

console.log(`Installed Syft ${lock.version} with verified SHA-256.`)
