import { invariant } from './context.mjs'

const TABLE_ROW =
  /^\|\s*`([a-z][a-z0-9_]*)`\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|/u

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

    const [, code, statusText, retryableText, retryAfterText] = match
    invariant(!seen.has(code), `Duplicate error code ${code} at catalog line ${index + 1}.`)
    seen.add(code)

    entries.push({
      code,
      status: statusText.trim(),
      retryable: retryableText.trim(),
      retryAfter: retryAfterText.trim(),
    })
  }

  invariant(inStableErrorSection, 'Error catalog is missing the stable error-code section.')
  invariant(entries.length === 66, `Expected 66 stable error codes; found ${entries.length}.`)
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

  return { entries, codes: seen }
}
