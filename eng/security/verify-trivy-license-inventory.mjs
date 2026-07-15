import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

const reportPath = resolve(process.argv[2] ?? 'artifacts/security/trivy-source.json')
const report = JSON.parse(await readFile(reportPath, 'utf8'))
const packages = (report.Results ?? []).flatMap((result) => result.Packages ?? [])

const licensedCount = (prefix) => packages.filter((entry) => {
  const purl = entry.Identifier?.PURL ?? entry.PURL ?? ''
  return purl.startsWith(prefix)
    && Array.isArray(entry.Licenses)
    && entry.Licenses.length > 0
}).length

const npmPackages = licensedCount('pkg:npm/')
const nugetPackages = licensedCount('pkg:nuget/')
const failures = []

if (npmPackages === 0) {
  failures.push('Trivy report contains no npm/pnpm packages with license evidence; install node_modules and include dev dependencies before scanning.')
}
if (nugetPackages === 0) {
  failures.push('Trivy report contains no NuGet packages with license evidence; restore into the scanned NUGET_PACKAGES directory before scanning.')
}

if (failures.length > 0) {
  throw new Error(failures.join('\n'))
}

console.log(`Trivy license inventory verified: ${npmPackages} npm/pnpm packages and ${nugetPackages} NuGet packages include license evidence.`)
