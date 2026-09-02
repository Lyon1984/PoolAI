import { spawnSync } from 'node:child_process'
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'

import {
  ContractFailure,
  createFatalUtf8Artifact,
  inspectFatalUtf8Artifact,
  invariant,
  readCanonicalRepositoryUtf8Artifact,
  sha256,
} from './context.mjs'
import { validateContractCompatibility } from './compatibility.mjs'
import {
  parseCompatibilityWindowRegistry,
  requireAcceptedCompatibilityWindow,
  resolveCompatibilityWindow,
  validateCompatibilityWindowAdrHistory,
  validateCompatibilityWindowDecisionSource,
  validateCompatibilityWindowHistory,
} from './compatibility-windows.mjs'

function expectFailure(action, expectedMessage) {
  try {
    action()
  } catch (error) {
    invariant(error instanceof ContractFailure, `Expected ContractFailure, received ${error.name}.`)
    invariant(
      error.message.includes(expectedMessage),
      `Compatibility-window self-test expected "${expectedMessage}", received "${error.message}".`,
    )
    return
  }
  throw new Error(`Compatibility-window self-test did not reject: ${expectedMessage}`)
}

function makeWindow({ status = 'proposed' } = {}) {
  const baseOpenApiSource = 'compatibility-window-base\n'
  const headOpenApiSource = 'compatibility-window-head\n'
  return {
    window: {
      id: 'compatibility-window-self-test',
      status,
      scope: 'openapi-v1-compatibility-window',
      baseRef: 'c'.repeat(40),
      baseOpenApiSha256: sha256(baseOpenApiSource),
      headOpenApiSha256: sha256(headOpenApiSource),
      adr: 'docs/architecture/adr/9998-compatibility-window-self-test.md',
      approvalControl: 'https://github.com/Lyon1984/PoolAI/issues/44',
      approvalEvidence: status === 'accepted'
        ? 'https://github.com/Lyon1984/PoolAI/issues/44#issuecomment-123456789'
        : null,
      allowedFailures: [
        '#/paths/~1compatibility-window/get/responses/400: new response status was added to an existing operation',
      ],
    },
    baseOpenApiSource,
    headOpenApiSource,
  }
}

function registrySource(...windows) {
  return JSON.stringify({ schemaVersion: 1, windows }, null, 2)
}

function registrySourceV2(...windows) {
  return JSON.stringify({ schemaVersion: 2, windows }, null, 2)
}

function makeDigestWindow({ status = 'proposed', withErrorFailure = true } = {}) {
  const state = makeWindow({ status })
  const baseErrorCatalogBytes = Buffer.from('base error catalog\n')
  const headErrorCatalogBytes = Buffer.from('head error catalog\n')
  const window = {
    ...state.window,
    baseErrorCatalogSha256: sha256(baseErrorCatalogBytes),
    headErrorCatalogSha256: sha256(headErrorCatalogBytes),
    allowedFailures: withErrorFailure
      ? [
          state.window.allowedFailures[0],
          'error-catalog:compatibility_window_self_test: existing status, stream, retry, or meaning semantics changed',
        ].sort()
      : state.window.allowedFailures,
  }
  return {
    ...state,
    baseErrorCatalogArtifact: createFatalUtf8Artifact(
      baseErrorCatalogBytes,
      'Self-test base error catalog',
    ),
    headErrorCatalogArtifact: createFatalUtf8Artifact(
      headErrorCatalogBytes,
      'Self-test head error catalog',
    ),
    window,
  }
}

function adrSource(window) {
  const status = window.status === 'accepted' ? 'Accepted' : 'Proposed'
  const evidence = window.status === 'accepted'
    ? `- Approval evidence: [Issue approval comment](${window.approvalEvidence})`
    : '- Approval evidence: **Pending explicit approval**'
  const machineLines = [
    `- Status: **${status}**`,
    `- Compatibility window ID: \`${window.id}\``,
    `- Base Git commit: \`${window.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${window.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${window.headOpenApiSha256}\``,
    ...(window.baseErrorCatalogSha256 === undefined
      ? []
      : [
          `- Base error-catalog SHA-256: \`${window.baseErrorCatalogSha256}\``,
          `- Target error-catalog SHA-256: \`${window.headErrorCatalogSha256}\``,
        ]),
    `- Approval control: [Issue #44](${window.approvalControl})`,
    evidence,
    ...window.allowedFailures.map((failure) => `- Allowed diagnostic: \`${failure}\``),
  ]
  return window.baseErrorCatalogSha256 === undefined
    ? machineLines.join('\n')
    : [
        '# ADR 9998: Compatibility window self-test',
        '',
        ...machineLines,
        '',
        '## Context',
        '',
        'Self-test decision body.',
      ].join('\n')
}

export function runCompatibilityWindowSelfTests({ compatibilityWindowSource }) {
  let cases = 0
  const checked = (action) => {
    action()
    cases += 1
  }
  const rejected = (action, expectedMessage) => {
    expectFailure(action, expectedMessage)
    cases += 1
  }

  checked(() => {
    const current = parseCompatibilityWindowRegistry(compatibilityWindowSource)
    invariant(current.windows.length > 0, 'Current compatibility-window registry is empty.')
  })

  const proposedState = makeWindow()
  const proposedSource = registrySource(proposedState.window)
  checked(() => parseCompatibilityWindowRegistry(proposedSource))

  const acceptedState = makeWindow({ status: 'accepted' })
  const acceptedSource = registrySource(acceptedState.window)
  checked(() => parseCompatibilityWindowRegistry(acceptedSource))

  checked(() => parseCompatibilityWindowRegistry(registrySourceV2(acceptedState.window)))

  const digestState = makeDigestWindow()
  const digestSource = registrySourceV2(digestState.window)
  checked(() => parseCompatibilityWindowRegistry(digestSource))

  const digestOpenApiOnly = makeDigestWindow({ withErrorFailure: false })
  checked(() => parseCompatibilityWindowRegistry(registrySourceV2(digestOpenApiOnly.window)))

  const equalOptionalDigests = structuredClone(digestOpenApiOnly.window)
  equalOptionalDigests.headErrorCatalogSha256 = equalOptionalDigests.baseErrorCatalogSha256
  checked(() => parseCompatibilityWindowRegistry(registrySourceV2(equalOptionalDigests)))

  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(digestState.window)),
    'must contain exactly these keys',
  )

  const digestlessErrorFailure = structuredClone(proposedState.window)
  digestlessErrorFailure.allowedFailures = [
    'error-catalog:compatibility_window_self_test: existing status, stream, retry, or meaning semantics changed',
  ]
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(digestlessErrorFailure)),
    'requires a digest-bound schemaVersion 2 record',
  )

  const oneSidedDigest = structuredClone(proposedState.window)
  oneSidedDigest.baseErrorCatalogSha256 = 'a'.repeat(64)
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(oneSidedDigest)),
    'schemaVersion 2 OpenAPI-only or digest-bound keys',
  )

  const digestUnknownKey = structuredClone(digestState.window)
  digestUnknownKey.errorCatalogMode = 'exact'
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(digestUnknownKey)),
    'schemaVersion 2 OpenAPI-only or digest-bound keys',
  )

  const upperDigest = structuredClone(digestState.window)
  upperDigest.baseErrorCatalogSha256 = upperDigest.baseErrorCatalogSha256.toUpperCase()
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(upperDigest)),
    '.baseErrorCatalogSha256 must be an exact lowercase SHA-256 digest',
  )

  const shortDigest = structuredClone(digestState.window)
  shortDigest.headErrorCatalogSha256 = 'a'.repeat(63)
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(shortDigest)),
    '.headErrorCatalogSha256 must be an exact lowercase SHA-256 digest',
  )

  const equalRequiredDigests = structuredClone(digestState.window)
  equalRequiredDigests.headErrorCatalogSha256 = equalRequiredDigests.baseErrorCatalogSha256
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(equalRequiredDigests)),
    'must bind different base and head error-catalog digests',
  )

  for (const selector of [
    'error-catalog:Upper_case',
    'error-catalog:9invalid',
    `error-catalog:${'a'.repeat(129)}`,
    'sse-fixture:compatibility_window_self_test',
    'error-catalog:compatibility*window',
  ]) {
    const malformedSelector = structuredClone(digestState.window)
    malformedSelector.allowedFailures = [`${selector}: changed`]
    rejected(
      () => parseCompatibilityWindowRegistry(registrySourceV2(malformedSelector)),
      'must use an exact OpenAPI or error-catalog diagnostic selector',
    )
  }

  const malformedPointer = structuredClone(digestState.window)
  malformedPointer.allowedFailures = ['#/components/~2invalid: changed']
  rejected(
    () => parseCompatibilityWindowRegistry(registrySourceV2(malformedPointer)),
    'must use one exact local OpenAPI JSON Pointer',
  )

  for (const separator of ['\r', '\n', '\u0085', '\u2028', '\u2029']) {
    const injectedSeparator = structuredClone(digestState.window)
    injectedSeparator.allowedFailures = [
      `error-catalog:compatibility_window_self_test: before${separator}after`,
    ]
    rejected(
      () => parseCompatibilityWindowRegistry(registrySourceV2(injectedSeparator)),
      'must not contain control, line, or paragraph separator characters',
    )
  }

  const proposedWithEvidence = structuredClone(proposedState.window)
  proposedWithEvidence.approvalEvidence = acceptedState.window.approvalEvidence
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(proposedWithEvidence)),
    'approvalEvidence must be null while status is proposed',
  )

  const acceptedWithoutEvidence = structuredClone(acceptedState.window)
  acceptedWithoutEvidence.approvalEvidence = null
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(acceptedWithoutEvidence)),
    'must be a permanent comment URL',
  )

  const placeholderEvidence = structuredClone(acceptedState.window)
  placeholderEvidence.approvalEvidence = `${placeholderEvidence.approvalControl}#issuecomment-pending`
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(placeholderEvidence)),
    'must be a permanent comment URL',
  )

  const wildcard = structuredClone(proposedState.window)
  wildcard.allowedFailures = ['#/paths/*: compatibility-window self-test']
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(wildcard)),
    'must not contain wildcards',
  )

  const exactRegexDiagnostic = structuredClone(proposedState.window)
  exactRegexDiagnostic.allowedFailures = [
    String.raw`#/components/schemas/Probe/pattern: pattern changed from .*\S.* to ^(?=[\s\S]*value).+$`,
  ]
  checked(() => parseCompatibilityWindowRegistry(registrySource(exactRegexDiagnostic)))

  const unknownKey = structuredClone(proposedState.window)
  unknownKey.ignoreUnregisteredFailures = false
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(unknownKey)),
    'must contain exactly these keys',
  )

  const wrongApprovalControl = structuredClone(proposedState.window)
  wrongApprovalControl.approvalControl = 'https://github.com/Lyon1984/PoolAI/issues/45'
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(wrongApprovalControl)),
    '.approvalControl must be https://github.com/Lyon1984/PoolAI/issues/44',
  )

  const duplicate = structuredClone(proposedState.window)
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(proposedState.window, duplicate)),
    '.id duplicates',
  )

  const duplicateBase = structuredClone(proposedState.window)
  duplicateBase.id = 'compatibility-window-second-self-test'
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(proposedState.window, duplicateBase)),
    '.baseRef duplicates',
  )

  const unsafeAdr = structuredClone(proposedState.window)
  unsafeAdr.adr = 'docs/architecture/adr/../9998-compatibility-window-self-test.md'
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(unsafeAdr)),
    '.adr must name one repository ADR',
  )

  const unsortedFailures = structuredClone(proposedState.window)
  unsortedFailures.allowedFailures = [
    '#/paths/~1z/get/responses/400: compatibility-window self-test z',
    '#/paths/~1a/get/responses/400: compatibility-window self-test a',
  ]
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(unsortedFailures)),
    '.allowedFailures must be sorted',
  )

  const duplicateFailures = structuredClone(proposedState.window)
  duplicateFailures.allowedFailures = [
    proposedState.window.allowedFailures[0],
    proposedState.window.allowedFailures[0],
  ]
  rejected(
    () => parseCompatibilityWindowRegistry(registrySource(duplicateFailures)),
    '.allowedFailures must not contain duplicates',
  )

  checked(() => {
    const resolved = resolveCompatibilityWindow({
      baseOpenApiSource: proposedState.baseOpenApiSource,
      baseRef: proposedState.window.baseRef,
      headOpenApiSource: proposedState.headOpenApiSource,
      registrySource: proposedSource,
    })
    invariant(resolved?.id === proposedState.window.id, 'Exact compatibility window was not selected.')
  })

  rejected(
    () => resolveCompatibilityWindow({
      baseOpenApiSource: proposedState.baseOpenApiSource,
      baseRef: proposedState.window.baseRef,
      headOpenApiSource: `${proposedState.headOpenApiSource}stale`,
      registrySource: proposedSource,
    }),
    'head OpenAPI SHA-256',
  )

  rejected(
    () => resolveCompatibilityWindow({
      baseOpenApiSource: `${proposedState.baseOpenApiSource}stale`,
      baseRef: proposedState.window.baseRef,
      headOpenApiSource: proposedState.headOpenApiSource,
      registrySource: proposedSource,
    }),
    'base OpenAPI SHA-256',
  )

  checked(() => {
    const resolved = resolveCompatibilityWindow({
      baseErrorCatalogArtifact: digestState.baseErrorCatalogArtifact,
      baseOpenApiSource: digestState.baseOpenApiSource,
      baseRef: digestState.window.baseRef,
      headErrorCatalogArtifact: digestState.headErrorCatalogArtifact,
      headOpenApiSource: digestState.headOpenApiSource,
      registrySource: digestSource,
    })
    invariant(resolved?.id === digestState.window.id, 'Digest-bound window was not selected.')
  })

  const staleBaseCatalogArtifact = createFatalUtf8Artifact(
    Buffer.from('stale base error catalog\n'),
    'Stale self-test base error catalog',
  )
  rejected(
    () => resolveCompatibilityWindow({
      baseErrorCatalogArtifact: staleBaseCatalogArtifact,
      baseOpenApiSource: digestState.baseOpenApiSource,
      baseRef: digestState.window.baseRef,
      headErrorCatalogArtifact: digestState.headErrorCatalogArtifact,
      headOpenApiSource: digestState.headOpenApiSource,
      registrySource: digestSource,
    }),
    'base error-catalog SHA-256',
  )

  const staleHeadCatalogArtifact = createFatalUtf8Artifact(
    Buffer.from('stale head error catalog\n'),
    'Stale self-test head error catalog',
  )
  rejected(
    () => resolveCompatibilityWindow({
      baseErrorCatalogArtifact: digestState.baseErrorCatalogArtifact,
      baseOpenApiSource: digestState.baseOpenApiSource,
      baseRef: digestState.window.baseRef,
      headErrorCatalogArtifact: staleHeadCatalogArtifact,
      headOpenApiSource: digestState.headOpenApiSource,
      registrySource: digestSource,
    }),
    'head error-catalog SHA-256',
  )

  rejected(
    () => resolveCompatibilityWindow({
      baseErrorCatalogArtifact: {
        digest: digestState.window.baseErrorCatalogSha256,
        source: 'caller-decoded source',
      },
      baseOpenApiSource: digestState.baseOpenApiSource,
      baseRef: digestState.window.baseRef,
      headErrorCatalogArtifact: digestState.headErrorCatalogArtifact,
      headOpenApiSource: digestState.headOpenApiSource,
      registrySource: digestSource,
    }),
    'must be one fatal UTF-8 source artifact',
  )

  for (const invalidBytes of [Buffer.from([0x80]), Buffer.from([0x81])]) {
    rejected(
      () => createFatalUtf8Artifact(invalidBytes, 'Invalid self-test error catalog'),
      'must be valid UTF-8',
    )
  }

  checked(() => {
    const rawBytes = Buffer.from([0xEF, 0xBB, 0xBF, 0x61])
    const artifact = createFatalUtf8Artifact(rawBytes, 'Raw digest self-test')
    const inspected = inspectFatalUtf8Artifact(artifact, 'Raw digest self-test')
    invariant(artifact.digest === sha256(rawBytes), 'Artifact did not hash exact raw bytes.')
    invariant(
      artifact.digest !== sha256(artifact.source),
      'Artifact incorrectly hashed its fatal-decoded source.',
    )
    inspected.bytes[0] = 0
    invariant(
      inspectFatalUtf8Artifact(artifact, 'Raw digest self-test').digest === sha256(rawBytes),
      'Artifact bytes were mutable through inspection.',
    )
  })

  const repositoryRoot = mkdtempSync(path.join(tmpdir(), 'poolai-contract-catalog-'))
  const outsideRoot = mkdtempSync(path.join(tmpdir(), 'poolai-contract-catalog-outside-'))
  try {
    writeFileSync(path.join(repositoryRoot, 'catalog.md'), 'original catalog\n')
    writeFileSync(path.join(outsideRoot, 'outside.md'), 'outside catalog\n')
    const safelyLoaded = readCanonicalRepositoryUtf8Artifact(
      repositoryRoot,
      'catalog.md',
      'Self-test canonical catalog',
    )
    renameSync(
      path.join(repositoryRoot, 'catalog.md'),
      path.join(repositoryRoot, 'catalog-original.md'),
    )
    writeFileSync(path.join(repositoryRoot, 'catalog.md'), 'replacement catalog\n')
    checked(() => {
      invariant(
        safelyLoaded.source === 'original catalog\n',
        'Canonical catalog artifact was reopened after its descriptor read.',
      )
      invariant(
        readFileSync(path.join(repositoryRoot, 'catalog.md'), 'utf8') === 'replacement catalog\n',
        'Path-swap self-test did not replace the path.',
      )
    })

    symlinkSync(path.join(outsideRoot, 'outside.md'), path.join(repositoryRoot, 'catalog-link.md'))
    rejected(
      () => readCanonicalRepositoryUtf8Artifact(
        repositoryRoot,
        'catalog-link.md',
        'Self-test symlink catalog',
      ),
      'cannot be safely read',
    )

    mkdirSync(path.join(repositoryRoot, 'catalog-directory'))
    rejected(
      () => readCanonicalRepositoryUtf8Artifact(
        repositoryRoot,
        'catalog-directory',
        'Self-test directory catalog',
      ),
      'cannot be safely read',
    )

    const fifoPath = path.join(repositoryRoot, 'catalog.fifo')
    const mkfifo = spawnSync('mkfifo', [fifoPath], { encoding: 'utf8' })
    invariant(mkfifo.status === 0, `mkfifo self-test setup failed: ${mkfifo.stderr}`)
    rejected(
      () => readCanonicalRepositoryUtf8Artifact(
        repositoryRoot,
        'catalog.fifo',
        'Self-test FIFO catalog',
      ),
      'cannot be safely read',
    )

    rejected(
      () => readCanonicalRepositoryUtf8Artifact(
        repositoryRoot,
        '../outside.md',
        'Self-test escaped catalog',
      ),
      'cannot be safely read',
    )
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true })
    rmSync(outsideRoot, { force: true, recursive: true })
  }

  checked(() => {
    const unrelated = resolveCompatibilityWindow({
      baseOpenApiSource: proposedState.baseOpenApiSource,
      baseRef: 'd'.repeat(40),
      headOpenApiSource: proposedState.headOpenApiSource,
      registrySource: proposedSource,
    })
    invariant(unrelated === undefined, 'Compatibility window leaked to another Git base.')
  })

  rejected(
    () => requireAcceptedCompatibilityWindow(proposedState.window),
    'is pending approval',
  )
  checked(() => requireAcceptedCompatibilityWindow(acceptedState.window))
  rejected(
    () => requireAcceptedCompatibilityWindow(digestState.window),
    'is pending approval',
  )

  const baseContract = {
    openapi: '3.1.0',
    info: { version: '1.0.0' },
    paths: {
      '/compatibility-window': {
        get: {
          operationId: 'compatibilityWindowSelfTest',
          responses: { 200: { description: 'base' } },
        },
      },
    },
    components: {},
  }
  const headContract = structuredClone(baseContract)
  headContract.paths['/compatibility-window'].get.responses['400'] = { description: 'candidate' }
  const errorCatalogSource = [
    '## 3. 稳定错误码',
    '| code | HTTP / SSE | 可重试 | Retry-After | 含义 |',
    '|---|---:|---|---|---|',
    '| `compatibility_window_self_test` | 400 | 否 | — | 兼容窗口自测。 |',
    '',
    '## 4. Self-test boundary',
  ].join('\n')
  const exactFailure =
    '#/paths/~1compatibility-window/get/responses/400: new response status was added to an existing operation'
  checked(() => {
    const result = validateContractCompatibility({
      allowedFailures: [exactFailure],
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: baseContract,
      failureAllowanceLabel: `Compatibility window ${acceptedState.window.id}`,
      headErrorCatalogSource: errorCatalogSource,
      headOpenApi: headContract,
    })
    invariant(result.waivedFailures === 1, 'Accepted exact window did not consume one diagnostic.')
  })
  const extraDiagnostic = structuredClone(headContract)
  extraDiagnostic.paths['/compatibility-window'].get.operationId = 'changedDuringWindow'
  rejected(
    () => validateContractCompatibility({
      allowedFailures: [exactFailure],
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: baseContract,
      failureAllowanceLabel: `Compatibility window ${acceptedState.window.id}`,
      headErrorCatalogSource: errorCatalogSource,
      headOpenApi: extraDiagnostic,
    }),
    `Compatibility window ${acceptedState.window.id} mismatch`,
  )

  const changedErrorCatalogSource = errorCatalogSource.replace(
    '兼容窗口自测。',
    '兼容窗口自测语义已变化。',
  )
  checked(() => {
    const result = validateContractCompatibility({
      allowedFailures: digestState.window.allowedFailures,
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: baseContract,
      failureAllowanceLabel: `Compatibility window ${digestState.window.id}`,
      headErrorCatalogSource: changedErrorCatalogSource,
      headOpenApi: headContract,
    })
    invariant(result.waivedFailures === 2, 'Mixed exact window did not consume two diagnostics.')
  })

  rejected(
    () => validateContractCompatibility({
      allowedFailures: [digestState.window.allowedFailures[0]],
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: baseContract,
      failureAllowanceLabel: `Compatibility window ${digestState.window.id}`,
      headErrorCatalogSource: changedErrorCatalogSource,
      headOpenApi: headContract,
    }),
    `Compatibility window ${digestState.window.id} mismatch`,
  )

  rejected(
    () => validateContractCompatibility({
      allowedFailures: [
        ...digestState.window.allowedFailures,
        '#/paths/~1unused/get: registered but absent self-test',
      ],
      baseErrorCatalogSource: errorCatalogSource,
      baseOpenApi: baseContract,
      failureAllowanceLabel: `Compatibility window ${digestState.window.id}`,
      headErrorCatalogSource: changedErrorCatalogSource,
      headOpenApi: headContract,
    }),
    'registered but absent',
  )

  const proposedAdr = adrSource(proposedState.window)
  const acceptedAdr = adrSource(acceptedState.window)
  checked(() => validateCompatibilityWindowDecisionSource(proposedState.window, proposedAdr))
  checked(() => validateCompatibilityWindowDecisionSource(acceptedState.window, acceptedAdr))
  rejected(
    () => validateCompatibilityWindowDecisionSource(
      acceptedState.window,
      acceptedAdr.replace('- Status: **Accepted**', '- Status: **Proposed**'),
    ),
    'must contain exactly one line',
  )

  const digestProposedAdr = adrSource(digestState.window)
  const acceptedDigestState = makeDigestWindow({ status: 'accepted' })
  const digestAcceptedAdr = adrSource(acceptedDigestState.window)
  checked(() => validateCompatibilityWindowDecisionSource(digestState.window, digestProposedAdr))
  checked(() => validateCompatibilityWindowDecisionSource(
    acceptedDigestState.window,
    digestAcceptedAdr,
  ))
  checked(() => validateCompatibilityWindowDecisionSource(
    digestState.window,
    digestProposedAdr.replace(
      '- Status: **Proposed**',
      '- Date: 2026-09-02\n- Status: **Proposed**',
    ),
  ))

  const digestAdrMutations = [
    [
      digestProposedAdr.replace('# ADR 9998: Compatibility window self-test\n', ''),
      'must begin with one H1 title',
    ],
    [
      digestProposedAdr.replace(
        '- Status: **Proposed**',
        '# Second title\n- Status: **Proposed**',
      ),
      'must not contain a second H1',
    ],
    [
      digestProposedAdr.replace(
        '- Status: **Proposed**',
        '  ### Pseudo section\n- Status: **Proposed**',
      ),
      'must not contain an indented pseudo-heading',
    ],
    [digestProposedAdr.replace('## Context', ' ## Context'), 'must contain an exact first ##'],
    [
      digestProposedAdr.replace(
        '## Context',
        '## Context\n\n- Status: **Proposed**',
      ),
      'preamble must contain exactly one line',
    ],
    [
      digestProposedAdr.replace(
        '- Base error-catalog SHA-256:',
        '- Head error-catalog SHA-256:',
      ),
      'preamble must contain exactly one line',
    ],
    [
      digestProposedAdr.replace(
        '- Base error-catalog SHA-256:',
        '+ Base error-catalog SHA-256:',
      ),
      'preamble must contain exactly one line',
    ],
    [
      digestProposedAdr.replace(
        '- Base error-catalog SHA-256:',
        '- Base error catalog SHA256:',
      ),
      'preamble must contain exactly one line',
    ],
    [
      digestProposedAdr.replace(
        '## Context',
        '## Context\n\n- Approval evidence: **Pending explicit approval**',
      ),
      'preamble must contain exactly one line',
    ],
    [
      digestProposedAdr.replace(
        '## Context',
        '## Context\n\n- Compatibility_window-id: `shadow`',
      ),
      'invalid or misplaced reserved marker',
    ],
    [
      digestProposedAdr.replace(
        '## Context',
        '## Context\n\n- Candidate head OpenAPI checksum: `shadow`',
      ),
      'invalid or misplaced reserved marker',
    ],
    [digestProposedAdr.replace('- Status:', '- status:'), 'preamble must contain exactly one line'],
    [digestProposedAdr.replace('- Status:', ' - Status:'), 'preamble must contain exactly one line'],
    [
      digestProposedAdr.replace(
        '## Context',
        '\v- Status: **Accepted**\n\n## Context',
      ),
      'invalid or misplaced reserved marker',
    ],
    [
      digestProposedAdr.replace(
        '## Context',
        '\f- Status: **Accepted**\n\n## Context',
      ),
      'invalid or misplaced reserved marker',
    ],
  ]
  for (const [mutatedAdr, expectedMessage] of digestAdrMutations) {
    rejected(
      () => validateCompatibilityWindowDecisionSource(digestState.window, mutatedAdr),
      expectedMessage,
    )
  }

  for (const separator of ['\r', '\u0085', '\u2028', '\u2029']) {
    rejected(
      () => validateCompatibilityWindowDecisionSource(
        digestState.window,
        digestProposedAdr.replace('Self-test decision body.', `before${separator}after`),
      ),
      'must use LF line boundaries',
    )
  }

  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: proposedSource,
    headRegistrySource: proposedSource,
  }))
  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: proposedSource,
    headRegistrySource: acceptedSource,
  }))

  const appendedDigestWindow = structuredClone(digestState.window)
  appendedDigestWindow.id = 'compatibility-window-second-self-test'
  appendedDigestWindow.baseRef = 'd'.repeat(40)
  appendedDigestWindow.adr = 'docs/architecture/adr/9997-compatibility-window-second-self-test.md'
  const schema2WithAppend = registrySourceV2(proposedState.window, appendedDigestWindow)
  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: proposedSource,
    headRegistrySource: schema2WithAppend,
  }))
  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: registrySourceV2(proposedState.window, appendedDigestWindow),
    headRegistrySource: registrySourceV2(proposedState.window, appendedDigestWindow),
  }))

  rejected(
    () => validateCompatibilityWindowHistory({
      baseRegistrySource: undefined,
      headRegistrySource: registrySourceV2(proposedState.window),
    }),
    'schemaVersion 2 requires a readable',
  )

  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: undefined,
    headRegistrySource: proposedSource,
  }))

  rejected(
    () => validateCompatibilityWindowHistory({
      baseRegistrySource: registrySourceV2(proposedState.window),
      headRegistrySource: proposedSource,
    }),
    'may only remain unchanged or advance from 1 to 2',
  )

  const prefixBaseFirst = structuredClone(proposedState.window)
  const prefixBaseSecond = structuredClone(appendedDigestWindow)
  delete prefixBaseSecond.baseErrorCatalogSha256
  delete prefixBaseSecond.headErrorCatalogSha256
  prefixBaseSecond.allowedFailures = [proposedState.window.allowedFailures[0]]
  const prefixBaseSource = registrySource(prefixBaseFirst, prefixBaseSecond)
  const prefixThird = structuredClone(prefixBaseSecond)
  prefixThird.id = 'compatibility-window-third-self-test'
  prefixThird.baseRef = 'e'.repeat(40)
  prefixThird.adr = 'docs/architecture/adr/9996-compatibility-window-third-self-test.md'
  const prefixMutations = [
    registrySource(prefixThird, prefixBaseFirst, prefixBaseSecond),
    registrySource(prefixBaseFirst, prefixThird, prefixBaseSecond),
    registrySource(prefixBaseSecond, prefixBaseFirst),
    registrySource(prefixBaseFirst),
    registrySource(prefixThird, prefixBaseSecond),
  ]
  for (const headRegistrySource of prefixMutations) {
    rejected(
      () => validateCompatibilityWindowHistory({
        baseRegistrySource: prefixBaseSource,
        headRegistrySource,
      }),
      'base window IDs must be the exact head prefix',
    )
  }

  const backfilledAccepted = structuredClone(acceptedState.window)
  backfilledAccepted.baseErrorCatalogSha256 = digestState.window.baseErrorCatalogSha256
  backfilledAccepted.headErrorCatalogSha256 = digestState.window.headErrorCatalogSha256
  rejected(
    () => validateCompatibilityWindowHistory({
      baseRegistrySource: acceptedSource,
      headRegistrySource: registrySourceV2(backfilledAccepted),
    }),
    'accepted window',
  )

  const changedDuringApproval = structuredClone(acceptedState.window)
  changedDuringApproval.allowedFailures = [
    '#/paths/~1changed/get/responses/400: compatibility-window self-test changed',
  ]
  rejected(
    () => validateCompatibilityWindowHistory({
      baseRegistrySource: proposedSource,
      headRegistrySource: registrySource(changedDuringApproval),
    }),
    'may change only through the proposed-to-accepted approval transition',
  )

  const changedAccepted = structuredClone(acceptedState.window)
  changedAccepted.allowedFailures = [
    '#/paths/~1changed/get/responses/400: compatibility-window self-test changed',
  ]
  rejected(
    () => validateCompatibilityWindowHistory({
      baseRegistrySource: acceptedSource,
      headRegistrySource: registrySource(changedAccepted),
    }),
    'accepted window',
  )

  const proposedAdrBytes = Buffer.from(proposedAdr)
  const acceptedAdrBytes = Buffer.from(acceptedAdr)
  checked(() => validateCompatibilityWindowAdrHistory({
    baseAdrSources: new Map([[proposedState.window.id, proposedAdrBytes]]),
    baseRegistry: parseCompatibilityWindowRegistry(proposedSource),
    headAdrSources: new Map([[acceptedState.window.id, acceptedAdrBytes]]),
    headRegistry: parseCompatibilityWindowRegistry(acceptedSource),
  }))

  const digestAcceptedSource = registrySourceV2(acceptedDigestState.window)
  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: digestSource,
    headRegistrySource: digestAcceptedSource,
  }))
  checked(() => validateCompatibilityWindowAdrHistory({
    baseAdrSources: new Map([[digestState.window.id, Buffer.from(digestProposedAdr)]]),
    baseRegistry: parseCompatibilityWindowRegistry(digestSource),
    headAdrSources: new Map([[digestState.window.id, Buffer.from(digestAcceptedAdr)]]),
    headRegistry: parseCompatibilityWindowRegistry(digestAcceptedSource),
  }))

  rejected(
    () => validateCompatibilityWindowAdrHistory({
      baseAdrSources: new Map([[digestState.window.id, Buffer.from(digestProposedAdr)]]),
      baseRegistry: parseCompatibilityWindowRegistry(digestSource),
      headAdrSources: new Map([[
        digestState.window.id,
        Buffer.from(digestAcceptedAdr.replace('Self-test decision body.', 'Changed body.')),
      ]]),
      headRegistry: parseCompatibilityWindowRegistry(digestAcceptedSource),
    }),
    'may change only its status and approval-evidence lines',
  )

  rejected(
    () => validateCompatibilityWindowAdrHistory({
      baseAdrSources: new Map([[proposedState.window.id, proposedAdrBytes]]),
      baseRegistry: parseCompatibilityWindowRegistry(proposedSource),
      headAdrSources: new Map([[acceptedState.window.id, Buffer.from(`${acceptedAdr}\nchanged`)]]),
      headRegistry: parseCompatibilityWindowRegistry(acceptedSource),
    }),
    'may change only its status and approval-evidence lines',
  )

  checked(() => validateCompatibilityWindowAdrHistory({
    baseAdrSources: new Map([[acceptedState.window.id, acceptedAdrBytes]]),
    baseRegistry: parseCompatibilityWindowRegistry(acceptedSource),
    headAdrSources: new Map([[acceptedState.window.id, Buffer.from(acceptedAdrBytes)]]),
    headRegistry: parseCompatibilityWindowRegistry(acceptedSource),
  }))

  return { cases }
}
