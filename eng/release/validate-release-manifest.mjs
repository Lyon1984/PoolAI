import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const manifestPath = resolve(repoRoot, "docs/release-manifest-v1.json");
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));

if (manifest.manifest_version !== 1) {
  throw new Error("release manifest version must be 1");
}

const sources = [
  [manifest.public_api.source, manifest.public_api.sha256],
  ...manifest.postgres.migrations.map((migration) => [migration.source, migration.sha256]),
  [manifest.redis.contract_source, manifest.redis.contract_sha256],
  ...manifest.redis.scripts.map((script) => [script.source, script.body_sha256]),
];

for (const [source, expectedSha256] of sources) {
  const path = resolveRepositorySource(source);
  const bytes = await readFile(path);
  const actualSha256 = createHash("sha256").update(bytes).digest("hex");
  if (actualSha256 !== expectedSha256) {
    throw new Error(`release manifest checksum drift: ${source}`);
  }
}

process.stdout.write("Release manifest validation passed.\n");

function resolveRepositorySource(source) {
  if (typeof source !== "string" || source.length === 0 || source.includes("\\")) {
    throw new Error("release manifest contains an invalid source path");
  }

  const path = resolve(repoRoot, source);
  if (path !== repoRoot && !path.startsWith(`${repoRoot}${sep}`)) {
    throw new Error("release manifest source escapes the repository");
  }

  return path;
}
