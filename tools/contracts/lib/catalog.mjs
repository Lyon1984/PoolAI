import { invariant } from './context.mjs'

const TABLE_ROW =
  /^\|\s*`([a-z][a-z0-9_]*)`\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|/u

function plainCell(value) {
  return value.replaceAll('**', '').replaceAll('`', '').trim()
}

function parseStatus(value, code, line) {
  const text = plainCell(value)
  if (text === 'SSE') {
    return { httpStatuses: [], stream: true }
  }

  const match = /^(\d{3})(?:\s+或\s+SSE)?$/u.exec(text)
  invariant(match, `Invalid HTTP/SSE status ${value.trim()} for ${code} at catalog line ${line}.`)
  const status = Number(match[1])
  invariant(
    status >= 400 && status <= 599,
    `Error code ${code} must use a 4xx/5xx status at catalog line ${line}.`,
  )
  return { httpStatuses: [status], stream: text.endsWith('或 SSE') }
}

function parseRetryable(value, code, line) {
  const text = plainCell(value)
  if (text === '是') {
    return true
  }
  if (text === '否') {
    return false
  }
  invariant(text === '视情况', `Invalid retryable value ${value.trim()} for ${code} at catalog line ${line}.`)
  return null
}

function parseRetryAfter(value, code, line) {
  const text = plainCell(value)
  if (text === '—') {
    return { retryAfter: 'none', retryAfterSeconds: null }
  }
  if (text === '必须') {
    return { retryAfter: 'required', retryAfterSeconds: null }
  }
  if (text === '可选') {
    return { retryAfter: 'optional', retryAfterSeconds: null }
  }

  invariant(/^\d+$/u.test(text), `Invalid Retry-After value ${value.trim()} for ${code} at catalog line ${line}.`)
  const seconds = Number(text)
  invariant(
    Number.isSafeInteger(seconds) && seconds > 0,
    `Retry-After seconds for ${code} must be a positive safe integer at catalog line ${line}.`,
  )
  return { retryAfter: 'fixed', retryAfterSeconds: seconds }
}

function sameStatuses(entry, statuses) {
  return JSON.stringify(entry.httpStatuses) === JSON.stringify(statuses)
}

function validateFrozenSemantics(entries) {
  const byCode = new Map(entries.map((entry) => [entry.code, entry]))
  const requireSemantics = (
    code,
    { httpStatuses, retryable, retryAfter, retryAfterSeconds = null, stream = false },
  ) => {
    const entry = byCode.get(code)
    invariant(entry, `Error catalog must define ${code}.`)
    invariant(
      sameStatuses(entry, httpStatuses) &&
        entry.stream === stream &&
        entry.retryable === retryable &&
        entry.retryAfter === retryAfter &&
        entry.retryAfterSeconds === retryAfterSeconds,
      `Error catalog semantics changed for ${code}.`,
    )
  }

  for (const code of ['gateway_overloaded', 'group_quota_reserved']) {
    requireSemantics(code, {
      httpStatuses: [429],
      retryable: true,
      retryAfter: 'fixed',
      retryAfterSeconds: 1,
    })
  }
  requireSemantics('dependency_unavailable', {
    httpStatuses: [503],
    retryable: true,
    retryAfter: 'fixed',
    retryAfterSeconds: 1,
  })
  for (const code of ['group_quota_exhausted', 'group_quota_insufficient']) {
    requireSemantics(code, {
      httpStatuses: [429],
      retryable: false,
      retryAfter: 'none',
    })
  }
  requireSemantics('upstream_stream_error', {
    httpStatuses: [502],
    stream: true,
    retryable: false,
    retryAfter: 'none',
  })
  requireSemantics('upstream_stream_idle_timeout', {
    httpStatuses: [],
    stream: true,
    retryable: false,
    retryAfter: 'none',
  })
  requireSemantics('upstream_usage_out_of_range', {
    httpStatuses: [502],
    stream: true,
    retryable: false,
    retryAfter: 'none',
  })
}

export function parseErrorCatalog(source) {
  const entries = []
  const seen = new Set()
  let inStableErrorSection = false

  for (const [index, line] of source.split(/\r?\n/u).entries()) {
    if (line === '## 3. 稳定错误码') {
      inStableErrorSection = true
      continue
    }
    if (inStableErrorSection && line.startsWith('## ')) {
      break
    }
    if (!inStableErrorSection) {
      continue
    }

    const match = TABLE_ROW.exec(line)
    if (!match) {
      continue
    }

    const [, code, statusText, retryableText, retryAfterText, meaningText] = match
    invariant(!seen.has(code), `Duplicate error code ${code} at catalog line ${index + 1}.`)
    seen.add(code)

    const status = parseStatus(statusText, code, index + 1)
    const retryable = parseRetryable(retryableText, code, index + 1)
    const retryAfter = parseRetryAfter(retryAfterText, code, index + 1)
    invariant(
      retryAfter.retryAfter === 'none' || retryable !== false,
      `Non-retryable error ${code} cannot declare Retry-After at catalog line ${index + 1}.`,
    )
    invariant(
      !status.stream || (retryable === false && retryAfter.retryAfter === 'none'),
      `SSE terminal error ${code} must be non-retryable and omit Retry-After at catalog line ${index + 1}.`,
    )

    entries.push({
      code,
      ...status,
      retryable,
      ...retryAfter,
      meaning: meaningText.trim(),
    })
  }

  invariant(inStableErrorSection, 'Error catalog is missing the stable error-code section.')
  invariant(entries.length >= 66, `Expected at least 66 stable error codes; found ${entries.length}.`)
  invariant(seen.has('internal_error'), 'Error catalog must define internal_error.')
  invariant(seen.has('gateway_overloaded'), 'Error catalog must define gateway_overloaded.')
  invariant(
    seen.has('upstream_usage_out_of_range'),
    'Error catalog must define upstream_usage_out_of_range.',
  )
  for (const phase of [
    'prepared',
    'dispatched_no_downstream_headers',
    'downstream_headers_committed',
    'business_output_started',
  ]) {
    invariant(!seen.has(phase), `Attempt phase ${phase} must not be generated as an error code.`)
  }

  validateFrozenSemantics(entries)

  return { entries, codes: seen }
}
