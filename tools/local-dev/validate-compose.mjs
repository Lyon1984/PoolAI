import { existsSync, lstatSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "../..");
const composeFile = path.join(repositoryRoot, "deploy/compose/compose.yaml");
const toolchain = path.join(scriptDirectory, "run-with-toolchain.sh");
const versionLocks = JSON.parse(
  readFileSync(path.join(repositoryRoot, "eng/versions.json"), "utf8"),
);
const secretDirectory = path.resolve(
  process.env.POOLAI_SECRET_DIR ?? path.join(repositoryRoot, ".tools/compose/secrets"),
);

const errors = [];

function check(condition, message) {
  if (!condition) {
    errors.push(message);
  }
}

function lockedContainer(name) {
  const image = versionLocks.containers?.[name];
  const digest = versionLocks.containerDigests?.[name];

  if (typeof image !== "string" || image.length === 0) {
    throw new Error(`eng/versions.json is missing containers.${name}.`);
  }
  if (typeof digest !== "string" || !/^sha256:[0-9a-f]{64}$/.test(digest)) {
    throw new Error(`eng/versions.json is missing a valid containerDigests.${name} lock.`);
  }

  return `${image}@${digest}`;
}

function runCompose(environment) {
  return spawnSync(
    toolchain,
    ["docker", "compose", "--file", composeFile, "config", "--format", "json"],
    {
      cwd: repositoryRoot,
      env: environment,
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
    },
  );
}

const missingSecretEnvironment = { ...process.env };
delete missingSecretEnvironment.POOLAI_SECRET_DIR;
const missingSecretResult = runCompose(missingSecretEnvironment);
check(
  missingSecretResult.status !== 0,
  "Compose must reject configuration when POOLAI_SECRET_DIR is absent.",
);

const resolvedResult = runCompose({
  ...process.env,
  POOLAI_SECRET_DIR: secretDirectory,
});

if (resolvedResult.status !== 0) {
  process.stderr.write(resolvedResult.stderr);
  process.exit(resolvedResult.status ?? 1);
}

let configuration;
try {
  configuration = JSON.parse(resolvedResult.stdout);
} catch (error) {
  process.stderr.write(`Compose did not return valid JSON: ${error.message}\n`);
  process.exit(1);
}

const expectedServices = [
  "api",
  "migrator",
  "mock-smtp",
  "mock-upstream",
  "postgres",
  "redis",
  "worker",
];
const actualServices = Object.keys(configuration.services ?? {}).sort();
check(
  JSON.stringify(actualServices) === JSON.stringify(expectedServices),
  `Expected exactly seven services (${expectedServices.join(", ")}); found ${actualServices.join(", ")}.`,
);

const services = configuration.services ?? {};
for (const [serviceName, service] of Object.entries(configuration.services ?? {})) {
  check(!("container_name" in service), `${serviceName} must not set container_name.`);
  check(service.privileged !== true, `${serviceName} must not run privileged.`);

  for (const publishedPort of service.ports ?? []) {
    check(
      publishedPort.host_ip === "127.0.0.1",
      `${serviceName} published port ${publishedPort.target} must bind only 127.0.0.1.`,
    );
  }
}

const expectedPublishedTargets = new Map([
  ["api", [8080]],
  ["migrator", []],
  ["mock-smtp", [8025]],
  ["mock-upstream", [4010]],
  ["postgres", []],
  ["redis", []],
  ["worker", []],
]);
for (const [serviceName, expectedTargets] of expectedPublishedTargets) {
  const actualTargets = (services[serviceName]?.ports ?? [])
    .map((port) => port.target)
    .sort((left, right) => left - right);
  check(
    JSON.stringify(actualTargets) === JSON.stringify(expectedTargets),
    `${serviceName} must publish exactly these container ports: ${expectedTargets.join(", ") || "none"}.`,
  );
}

check((services.postgres?.ports ?? []).length === 0, "PostgreSQL must not publish a host port.");
check((services.redis?.ports ?? []).length === 0, "Redis must not publish a host port.");
check(
  (services["mock-smtp"]?.ports ?? []).every((item) => item.target === 8025),
  "Mock SMTP may publish only the Mailpit UI; SMTP port 1025 must remain internal.",
);
check(
  (services["mock-upstream"]?.ports ?? []).every(
    (item) => item.target === 4010 && item.protocol === "tcp",
  ),
  "Mock upstream may publish only its loopback HTTP port; SNTP UDP must remain internal.",
);
check(
  new Set(services["mock-upstream"]?.expose ?? []).has("4123/udp"),
  "Mock upstream must expose SNTP v4 on the internal 4123/udp test port.",
);

const expectedNtpEnvironment = {
  Health__Ntp__Server: "mock-upstream",
  Health__Ntp__Port: "4123",
  Health__Ntp__TimeoutMilliseconds: "750",
};
for (const host of ["api", "worker"]) {
  const hostEnvironment = services[host]?.environment ?? {};
  for (const [key, expectedValue] of Object.entries(expectedNtpEnvironment)) {
    check(
      hostEnvironment[key] === expectedValue,
      `${host} must set ${key}=${expectedValue} for the real local SNTP gate.`,
    );
  }
  check(
    Number(hostEnvironment.Health__Ntp__TimeoutMilliseconds)
      < Number(hostEnvironment.Health__ReadinessTimeoutSeconds) * 1000,
    `${host} SNTP timeout must be smaller than its total readiness timeout.`,
  );
  check(
    !Object.keys(hostEnvironment).some((key) => /ntp.*(offset|max.*offset)/i.test(key)),
    `${host} must not configure or inject the fixed ±5-second NTP safety boundary.`,
  );
}

const mockEnvironment = services["mock-upstream"]?.environment ?? {};
for (const [key, expectedValue] of Object.entries({
  MOCK_UPSTREAM_ENVIRONMENT: "LocalCompose",
  MOCK_NTP_HOST: "0.0.0.0",
  MOCK_NTP_PORT: "4123",
})) {
  check(
    mockEnvironment[key] === expectedValue,
    `mock-upstream must set ${key}=${expectedValue}.`,
  );
}

const postgresVolumeTargets = new Set(
  (services.postgres?.volumes ?? []).map((volume) => volume.target),
);
check(
  postgresVolumeTargets.has("/var/lib/postgresql"),
  "PostgreSQL 18 data volume must target /var/lib/postgresql.",
);
check(
  !postgresVolumeTargets.has("/var/lib/postgresql/data"),
  "PostgreSQL 18 must not use the legacy /var/lib/postgresql/data volume target.",
);

const pinnedImages = new Map([
  ["postgres", lockedContainer("postgresql")],
  ["redis", lockedContainer("redis")],
  ["mock-smtp", lockedContainer("mailpit")],
  ["mock-upstream", lockedContainer("node")],
]);
for (const [serviceName, expectedImage] of pinnedImages) {
  check(
    services[serviceName]?.image === expectedImage,
    `${serviceName} image must equal the reviewed version lock.`,
  );
}

function dependencyCondition(serviceName, dependencyName) {
  return services[serviceName]?.depends_on?.[dependencyName]?.condition;
}

check(
  dependencyCondition("migrator", "postgres") === "service_healthy",
  "Migrator must wait for healthy PostgreSQL.",
);
check(
  Object.keys(services.migrator?.depends_on ?? {}).length === 1,
  "Migrator must depend only on PostgreSQL.",
);
check(services.migrator?.restart === "no", "Migrator must be a one-shot service with restart=no.");

for (const host of ["api", "worker"]) {
  check(
    dependencyCondition(host, "migrator") === "service_completed_successfully",
    `${host} must wait for successful one-shot Migrator completion.`,
  );
  for (const dependency of ["postgres", "redis", "mock-upstream", "mock-smtp"]) {
    check(
      dependencyCondition(host, dependency) === "service_healthy",
      `${host} must wait for healthy ${dependency}.`,
    );
  }
}

for (const dependency of ["postgres", "redis", "mock-upstream", "mock-smtp"]) {
  check(
    Array.isArray(services[dependency]?.healthcheck?.test)
      && services[dependency].healthcheck.test.length > 1,
    `${dependency} must declare an explicit Compose healthcheck.`,
  );
}

check(
  configuration.networks?.runtime?.internal === true,
  "The local runtime network must be internal.",
);
check(
  configuration.networks?.loopback?.internal !== true
    && configuration.networks?.loopback?.driver === "bridge"
    && configuration.networks?.loopback?.driver_opts?.["com.docker.network.bridge.host_binding_ipv4"] === "127.0.0.1",
  "The local loopback network must be a host-bound bridge.",
);

const expectedNetworkMembership = new Map([
  ["api", ["loopback", "runtime"]],
  ["migrator", ["runtime"]],
  ["mock-smtp", ["loopback", "runtime"]],
  ["mock-upstream", ["loopback", "runtime"]],
  ["postgres", ["runtime"]],
  ["redis", ["runtime"]],
  ["worker", ["runtime"]],
]);
for (const [serviceName, expectedNetworks] of expectedNetworkMembership) {
  const actualNetworks = Object.keys(services[serviceName]?.networks ?? {}).sort();
  check(
    JSON.stringify(actualNetworks) === JSON.stringify(expectedNetworks),
    `${serviceName} must join exactly these networks: ${expectedNetworks.join(", ")}.`,
  );
}

for (const [secretName, secret] of Object.entries(configuration.secrets ?? {})) {
  const resolvedSecret = path.resolve(secret.file ?? "");
  const relative = path.relative(secretDirectory, resolvedSecret);
  check(
    relative !== "" && !relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative),
    `${secretName} must resolve below POOLAI_SECRET_DIR.`,
  );
}

if (existsSync(secretDirectory)) {
  for (const privateDirectory of [
    path.dirname(path.dirname(secretDirectory)),
    path.dirname(secretDirectory),
    secretDirectory,
  ]) {
    const metadata = lstatSync(privateDirectory);
    check(
      metadata.isDirectory() && !metadata.isSymbolicLink(),
      `${privateDirectory} must be a real private directory, not a symbolic link.`,
    );
    check(
      (metadata.mode & 0o777) === 0o700,
      `${privateDirectory} must have mode 0700.`,
    );
  }

  const mountedSecretPaths = new Set();
  for (const [secretName, secret] of Object.entries(configuration.secrets ?? {})) {
    const secretPath = path.resolve(secret.file ?? "");
    mountedSecretPaths.add(secretPath);
    check(existsSync(secretPath), `${secretName} source file must exist after preparation.`);
    if (!existsSync(secretPath)) {
      continue;
    }

    const metadata = lstatSync(secretPath);
    check(
      metadata.isFile() && !metadata.isSymbolicLink(),
      `${secretName} source must be a regular file, not a symbolic link.`,
    );
    check(
      (metadata.mode & 0o777) === 0o644,
      `${secretName} source must have mode 0644 below the protected 0700 directory chain so its non-root container can read the bind mount.`,
    );
  }

  for (const privateFileName of ["mock-smtp-ca-key.pem", "mock-smtp-ca.pem"]) {
    const privatePath = path.join(secretDirectory, privateFileName);
    check(
      !mountedSecretPaths.has(privatePath),
      `${privateFileName} is local CA material and must not be mounted into a container.`,
    );
    check(existsSync(privatePath), `${privateFileName} must exist after preparation.`);
    if (existsSync(privatePath)) {
      const metadata = lstatSync(privatePath);
      check(
        metadata.isFile() && !metadata.isSymbolicLink() && (metadata.mode & 0o777) === 0o600,
        `${privateFileName} must remain an unmounted regular file with mode 0600.`,
      );
    }
  }
}

const forbiddenEnvironmentKeys = [
  "Data__Postgres__ConnectionString",
  "Data__Redis__ConnectionString",
  "Auth__Jwt__SigningKey",
  "Auth__PasswordReset__RateLimitScopePepper",
  "Auth__TokenHash__CurrentPepper",
  "ApiKeys__CurrentPepper",
  "Idempotency__RequestHashPepper",
  "Secrets__Envelope__CurrentKey",
];
for (const host of ["api", "worker", "migrator"]) {
  for (const key of forbiddenEnvironmentKeys) {
    check(
      !(key in (services[host]?.environment ?? {})),
      `${host} must receive ${key} as a secret file, not an environment value.`,
    );
  }
}

function secretTargets(serviceName) {
  return new Set(
    (services[serviceName]?.secrets ?? []).map((item) =>
      typeof item === "string" ? item : item.target,
    ),
  );
}

const apiOnlySecretTargets = [
  "Auth__Jwt__SigningKey",
  "Auth__PasswordReset__RateLimitScopePepper",
  "Auth__TokenHash__CurrentPepper",
  "ApiKeys__CurrentPepper",
  "Idempotency__RequestHashPepper",
];
const commonRuntimeSecretTargets = [
  "Data__Postgres__ConnectionString",
  "Data__Redis__ConnectionString",
  "Secrets__Envelope__CurrentKey",
  "Secrets__Envelope__DecryptKeyRing__local-compose-v1",
  "local-compose-ca-bundle.pem",
];

for (const [host, expectedTargets] of [
  ["api", [...commonRuntimeSecretTargets, ...apiOnlySecretTargets]],
  ["worker", commonRuntimeSecretTargets],
]) {
  const targets = secretTargets(host);
  for (const expectedTarget of expectedTargets) {
    check(targets.has(expectedTarget), `${host} is missing secret target ${expectedTarget}.`);
  }
}

const workerSecretTargets = secretTargets("worker");
for (const apiOnlyTarget of apiOnlySecretTargets) {
  check(
    !workerSecretTargets.has(apiOnlyTarget),
    `worker must not mount API-only secret target ${apiOnlyTarget}.`,
  );
}

const workerEnvironment = services.worker?.environment ?? {};
for (const [key, expectedValue] of Object.entries({
  Email__Smtp__Host: "mock-smtp",
  Email__Smtp__Port: "1025",
  Email__Smtp__Security: "starttls",
  Email__FromAddress: "no-reply@poolai.local",
})) {
  check(
    workerEnvironment[key] === expectedValue,
    `worker must set ${key}=${expectedValue} for SMTP delivery.`,
  );
}
check(
  secretTargets("migrator").has("Data__Postgres__ConnectionString"),
  "Migrator is missing its PostgreSQL Secret Provider input.",
);

const redisTargets = secretTargets("redis");
check(redisTargets.has("redis-password"), "Redis healthcheck password secret target is missing.");
check(redisTargets.has("redis.conf"), "Redis configuration secret target is missing.");
check(
  (services.redis?.healthcheck?.test ?? []).join(" ").includes("--user poolai"),
  "Redis healthcheck must authenticate as the dedicated poolai ACL user.",
);
const smtpTargets = secretTargets("mock-smtp");
check(smtpTargets.has("mock-smtp-cert.pem"), "Mailpit TLS certificate target is missing.");
check(smtpTargets.has("mock-smtp-key.pem"), "Mailpit TLS key target is missing.");
check(
  services["mock-smtp"]?.environment?.MP_SMTP_REQUIRE_STARTTLS === "true",
  "Mailpit must require STARTTLS.",
);

const hostRuntimeImages = new Map([
  ["PoolAI.Api.Dockerfile", lockedContainer("dotnetAspNet")],
  ["PoolAI.Worker.Dockerfile", lockedContainer("dotnetRuntime")],
  ["PoolAI.Migrator.Dockerfile", lockedContainer("dotnetRuntime")],
]);
for (const [dockerfileName, expectedRuntimeImage] of hostRuntimeImages) {
  const dockerDirectory = path.join(repositoryRoot, "deploy/docker");
  const dockerfile = readFileSync(path.join(dockerDirectory, dockerfileName), "utf8");
  check(
    !/\bdotnet\s+(restore|build|publish)\b/i.test(dockerfile),
    `${dockerfileName} must package artifacts without compiling source.`,
  );
  check(
    /artifacts\/publish\/PoolAI\./.test(dockerfile),
    `${dockerfileName} must copy a pre-published Host artifact.`,
  );
  check(
    dockerfile.includes(`ARG RUNTIME_IMAGE=${expectedRuntimeImage}`),
    `${dockerfileName} default runtime must equal the reviewed eng/versions.json lock.`,
  );

  const hostName = dockerfileName.replace(".Dockerfile", "");
  const dockerIgnore = readFileSync(
    path.join(dockerDirectory, `${dockerfileName}.dockerignore`),
    "utf8",
  );
  const expectedDockerIgnore = [
    "**",
    "!artifacts/",
    "!artifacts/publish/",
    `!artifacts/publish/${hostName}/`,
    `!artifacts/publish/${hostName}/**`,
    "!deploy/",
    "!deploy/docker/",
    `!deploy/docker/${dockerfileName}`,
    `!deploy/docker/${dockerfileName}.dockerignore`,
  ];
  check(
    JSON.stringify(dockerIgnore.trim().split(/\r?\n/)) === JSON.stringify(expectedDockerIgnore),
    `${dockerfileName}.dockerignore must allow only its Host artifact and exclude local secrets.`,
  );
}

const rootDockerIgnoreRules = readFileSync(path.join(repositoryRoot, ".dockerignore"), "utf8")
  .split(/\r?\n/)
  .map((line) => line.trim())
  .filter((line) => line.length > 0 && !line.startsWith("#"));
check(
  JSON.stringify(rootDockerIgnoreRules) === JSON.stringify([
    "**",
    "!artifacts/",
    "!artifacts/publish/",
    "!artifacts/publish/**",
    "!deploy/",
    "!deploy/docker/",
    "!deploy/docker/*.Dockerfile",
  ]),
  "Root .dockerignore must expose only published artifacts and Host Dockerfiles to the builder.",
);

check(
  services.api?.build?.args?.RUNTIME_IMAGE === lockedContainer("dotnetAspNet"),
  "Api Compose build must equal the reviewed ASP.NET runtime lock in eng/versions.json.",
);
for (const host of ["worker", "migrator"]) {
  check(
    services[host]?.build?.args?.RUNTIME_IMAGE === lockedContainer("dotnetRuntime"),
    `${host} Compose build must equal the reviewed .NET runtime lock in eng/versions.json.`,
  );
}

const roleSql = readFileSync(path.join(repositoryRoot, "deploy/postgres/runtime-roles.sql"), "utf8");
check(
  !/\b(CREATE|ALTER|DROP)\s+(TABLE|FUNCTION|PROCEDURE|TYPE|INDEX|SCHEMA|SEQUENCE|VIEW|TRIGGER|EXTENSION|DOMAIN|POLICY)\b/i.test(roleSql),
  "PostgreSQL bootstrap may provision cluster roles/database ownership only; business DDL belongs to Migrator.",
);
check(
  roleSql.includes("\\getenv") && roleSql.includes("poolai_runtime_owner"),
  "PostgreSQL role bootstrap must source passwords from the process environment and create the NOLOGIN owner.",
);

const prepareCompose = readFileSync(path.join(scriptDirectory, "prepare-compose.sh"), "utf8");
for (const requiredAclRule of [
  "user default off",
  "user poolai on >$redis_password",
  "~poolai:r1:local-compose:*",
  "&poolai:r1:local-compose:*",
  "+@all",
  "-@admin",
  "-config",
  "-module",
  "-flushall",
  "-flushdb",
  "-keys",
  "-script|flush",
  "user=poolai,password=$redis_password",
]) {
  check(
    prepareCompose.includes(requiredAclRule),
    `Redis local ACL generator is missing reviewed rule: ${requiredAclRule}`,
  );
}

const composeUp = readFileSync(path.join(scriptDirectory, "compose-up.sh"), "utf8");
check(
  composeUp.includes("validate-compose.sh") && composeUp.includes("eng/build/publish-hosts.sh"),
  "compose-up must validate before startup and use the repository Host publish entrypoint.",
);

const mockServer = readFileSync(
  path.join(repositoryRoot, "deploy/mock-upstream/server.mjs"),
  "utf8",
);
for (const requiredSntpEvidence of [
  'from "node:dgram"',
  "requestTransmitTimestamp.copy(response, 24)",
  "writeNtpTimestamp(response, 32",
  "writeNtpTimestamp(response, 40",
  'process.argv.includes("--self-test")',
]) {
  check(
    mockServer.includes(requiredSntpEvidence),
    `Mock SNTP implementation is missing required packet evidence: ${requiredSntpEvidence}`,
  );
}
const sntpSelfTest = spawnSync(
  toolchain,
  ["node", path.join(repositoryRoot, "deploy/mock-upstream/server.mjs"), "--self-test"],
  {
    cwd: repositoryRoot,
    env: process.env,
    encoding: "utf8",
  },
);
check(
  sntpSelfTest.status === 0,
  `Mock SNTP packet self-test failed: ${String(sntpSelfTest.stderr || sntpSelfTest.stdout || sntpSelfTest.error || "unknown error").trim()}`,
);

const verifyCompose = readFileSync(path.join(scriptDirectory, "verify-compose.sh"), "utf8");
for (const requiredReadinessCase of ["6000", "-6000", '"drop"', '"reset"', "503", "200"]) {
  check(
    verifyCompose.includes(requiredReadinessCase),
    `verify-compose must exercise NTP readiness case: ${requiredReadinessCase}`,
  );
}
check(
  !/https?:\/\/(?:mock-upstream|0\.0\.0\.0)/.test(verifyCompose),
  "verify-compose may call the mock test-control endpoint only through a loopback URL.",
);

if (errors.length > 0) {
  for (const error of errors) {
    process.stderr.write(`compose validation: ${error}\n`);
  }
  process.exit(1);
}

process.stdout.write("Compose topology and deployment invariants validated without starting containers.\n");
