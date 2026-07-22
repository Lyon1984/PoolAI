import { ContractFailure, invariant, sha256 } from './context.mjs'
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

function adrSource(window) {
  const status = window.status === 'accepted' ? 'Accepted' : 'Proposed'
  const evidence = window.status === 'accepted'
    ? `- Approval evidence: [Issue approval comment](${window.approvalEvidence})`
    : '- Approval evidence: **Pending explicit approval**'
  return [
    `- Status: **${status}**`,
    `- Compatibility window ID: \`${window.id}\``,
    `- Base Git commit: \`${window.baseRef}\``,
    `- Base OpenAPI SHA-256: \`${window.baseOpenApiSha256}\``,
    `- Target OpenAPI SHA-256: \`${window.headOpenApiSha256}\``,
    `- Approval control: [Issue #44](${window.approvalControl})`,
    evidence,
    ...window.allowedFailures.map((failure) => `- Allowed diagnostic: \`${failure}\``),
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

  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: proposedSource,
    headRegistrySource: proposedSource,
  }))
  checked(() => validateCompatibilityWindowHistory({
    baseRegistrySource: proposedSource,
    headRegistrySource: acceptedSource,
  }))

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
