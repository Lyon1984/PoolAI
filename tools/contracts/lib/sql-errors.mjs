import { readFile } from 'node:fs/promises'
import path from 'node:path'

import { ContractFailure, contractPaths, invariant } from './context.mjs'

const SQL_FILES = [
  '0001_baseline.sql',
  '0002_quota_functions.sql',
  '0003_runtime_permissions.sql',
  '0004_identity_m1_e1.sql',
  '0005_identity_m1_e2.sql',
]

function exposeDollarQuotedBodies(source) {
  const counts = new Map()
  for (const match of source.matchAll(/\$[A-Za-z_][A-Za-z0-9_]*\$|\$\$/gu)) {
    counts.set(match[0], (counts.get(match[0]) ?? 0) + 1)
  }
  for (const [tag, count] of counts) {
    invariant(count % 2 === 0, `Unpaired PostgreSQL dollar quote ${tag}.`)
  }
  return source.replace(/\$[A-Za-z_][A-Za-z0-9_]*\$|\$\$/gu, (tag) => ' '.repeat(tag.length))
}

export function stripSqlComments(source) {
  source = exposeDollarQuotedBodies(source)
  let result = ''
  let index = 0
  let state = 'normal'
  let blockDepth = 0

  while (index < source.length) {
    const current = source[index]
    const next = source[index + 1]

    if (state === 'line-comment') {
      if (current === '\n') {
        result += '\n'
        state = 'normal'
      } else {
        result += ' '
      }
      index += 1
      continue
    }

    if (state === 'block-comment') {
      if (current === '/' && next === '*') {
        result += '  '
        blockDepth += 1
        index += 2
      } else if (current === '*' && next === '/') {
        result += '  '
        blockDepth -= 1
        index += 2
        if (blockDepth === 0) {
          state = 'normal'
        }
      } else {
        result += current === '\n' ? '\n' : ' '
        index += 1
      }
      continue
    }

    if (state === 'single-quote') {
      result += current
      if (current === "'" && next === "'") {
        result += next
        index += 2
      } else {
        index += 1
        if (current === "'") {
          state = 'normal'
        }
      }
      continue
    }

    if (state === 'double-quote') {
      result += current
      if (current === '"' && next === '"') {
        result += next
        index += 2
      } else {
        index += 1
        if (current === '"') {
          state = 'normal'
        }
      }
      continue
    }

    if (current === '-' && next === '-') {
      result += '  '
      state = 'line-comment'
      index += 2
    } else if (current === '/' && next === '*') {
      result += '  '
      state = 'block-comment'
      blockDepth = 1
      index += 2
    } else if (current === "'") {
      result += current
      state = 'single-quote'
      index += 1
    } else if (current === '"') {
      result += current
      state = 'double-quote'
      index += 1
    } else {
      result += current
      index += 1
    }
  }

  invariant(state !== 'block-comment', 'Unclosed SQL block comment.')
  invariant(state !== 'single-quote', 'Unclosed SQL string literal.')
  invariant(state !== 'double-quote', 'Unclosed SQL quoted identifier.')
  return result
}

function readSqlString(source, start) {
  let index = start
  while (/\s/u.test(source[index] ?? '')) {
    index += 1
  }
  if (source[index] !== "'") {
    return null
  }
  index += 1
  let value = ''
  while (index < source.length) {
    if (source[index] === "'" && source[index + 1] === "'") {
      value += "'"
      index += 2
    } else if (source[index] === "'") {
      return { end: index + 1, value }
    } else {
      value += source[index]
      index += 1
    }
  }
  throw new ContractFailure('Unclosed SQL string literal while scanning P0001 codes.')
}

function dynamicExpression(source, start) {
  let index = start
  let depth = 0
  while (index < source.length) {
    const character = source[index]
    if (character === '(') {
      depth += 1
    } else if (character === ')') {
      if (depth === 0) {
        break
      }
      depth -= 1
    } else if (character === ',' && depth === 0) {
      break
    }
    index += 1
  }
  return source.slice(start, index).trim()
}

function quotedRanges(source) {
  const ranges = []
  let index = 0
  while (index < source.length) {
    const quote = source[index]
    if (quote !== "'" && quote !== '"') {
      index += 1
      continue
    }
    const start = index
    index += 1
    while (index < source.length) {
      if (source[index] === quote && source[index + 1] === quote) {
        index += 2
      } else if (source[index] === quote) {
        index += 1
        ranges.push([start, index])
        break
      } else {
        index += 1
      }
    }
  }
  return ranges
}

function isQuotedPosition(ranges, position) {
  return ranges.some(([start, end]) => position >= start && position < end)
}

export function scanSqlP0001Codes(source, sourceName = '<memory>') {
  const sql = stripSqlComments(source)
  const quoted = quotedRanges(sql)
  const codes = new Set()
  const helperDefinition = /\bCREATE\s+OR\s+REPLACE\s+FUNCTION\s+poolai_business_error\s*\(/iu.exec(sql)
  const helperBoundary = helperDefinition
    ? sql.slice(helperDefinition.index + helperDefinition[0].length).search(/\bCREATE\s+OR\s+REPLACE\s+FUNCTION\b/iu)
    : -1
  const helperEnd =
    helperDefinition && helperBoundary >= 0
      ? helperDefinition.index + helperDefinition[0].length + helperBoundary
      : sql.length
  let dynamicHelperRaiseCount = 0

  for (const match of sql.matchAll(/\bRAISE\s+EXCEPTION\s+USING\b([\s\S]*?);/giu)) {
    if (isQuotedPosition(quoted, match.index)) {
      continue
    }
    const clause = match[1]
    if (!/\bERRCODE\s*=\s*'P0001'/iu.test(clause)) {
      continue
    }
    const message = /\bMESSAGE\s*=\s*/iu.exec(clause)
    invariant(message, `${sourceName} P0001 RAISE has no MESSAGE expression.`)
    const start = message.index + message[0].length
    const literal = readSqlString(clause, start)
    if (literal) {
      invariant(
        /^[a-z][a-z0-9_]*$/u.test(literal.value),
        `${sourceName} P0001 MESSAGE is not a stable literal code: ${literal.value}`,
      )
      codes.add(literal.value)
      continue
    }

    const expression = dynamicExpression(clause, start)
    const statementIndex = match.index
    const insideHelper =
      helperDefinition &&
      statementIndex >= helperDefinition.index &&
      statementIndex < helperEnd &&
      expression === 'p_code'
    invariant(
      insideHelper,
      `${sourceName} uses dynamic P0001 MESSAGE expression ${expression || '<empty>'}.`,
    )
    dynamicHelperRaiseCount += 1
  }

  const helperCallPattern = /\bpoolai_business_error\s*\(/giu
  for (const match of sql.matchAll(helperCallPattern)) {
    if (isQuotedPosition(quoted, match.index)) {
      continue
    }
    const prefix = sql.slice(Math.max(0, match.index - 48), match.index)
    const statementStart = sql.lastIndexOf(';', match.index - 1) + 1
    const statementPrefix = sql.slice(statementStart, match.index)
    const isFunctionDdl =
      /\b(?:CREATE(?:\s+OR\s+REPLACE)?|ALTER|DROP)\s+FUNCTION\b/iu.test(statementPrefix) ||
      /\b(?:GRANT|REVOKE)\b[\s\S]*\bON\s+FUNCTION\b/iu.test(statementPrefix)
    if (/\bFUNCTION\s*$/iu.test(prefix) || isFunctionDdl) {
      continue
    }
    const argumentStart = match.index + match[0].length
    const literal = readSqlString(sql, argumentStart)
    if (!literal) {
      const expression = dynamicExpression(sql, argumentStart)
      throw new ContractFailure(
        `${sourceName} calls poolai_business_error with dynamic first argument ${expression || '<empty>'}.`,
      )
    }
    invariant(
      /^[a-z][a-z0-9_]*$/u.test(literal.value),
      `${sourceName} helper code is not stable: ${literal.value}`,
    )
    codes.add(literal.value)
  }

  if (helperDefinition) {
    invariant(
      dynamicHelperRaiseCount === 1,
      `${sourceName} poolai_business_error must contain exactly one dynamic P0001 raise.`,
    )
  } else {
    invariant(
      dynamicHelperRaiseCount === 0,
      `${sourceName} has a dynamic P0001 raise without the registered helper.`,
    )
  }
  return codes
}

export function validateSqlErrorMapping({ errorCodes, mapping, sqlSources }) {
  invariant(mapping.schema_version === 2, 'SQL error map schema_version must be 2.')
  invariant(
    JSON.stringify(mapping.lookup_key) === JSON.stringify(['operation', 'phase', 'sql_code']),
    'SQL error map lookup_key changed.',
  )
  invariant(Array.isArray(mapping.entries), 'SQL error map entries must be an array.')

  const extractedBySource = Object.fromEntries(
    SQL_FILES.map((name) => {
      invariant(typeof sqlSources[name] === 'string', `Missing SQL source ${name}.`)
      return [name, scanSqlP0001Codes(sqlSources[name], name)]
    }),
  )
  const extracted = new Set(SQL_FILES.flatMap((name) => [...extractedBySource[name]]))
  const mapped = new Set()

  for (const entry of mapping.entries) {
    invariant(SQL_FILES.includes(entry.source), `Unknown SQL error source ${entry.source}.`)
    invariant(!mapped.has(entry.sql_code), `Duplicate SQL error map entry ${entry.sql_code}.`)
    mapped.add(entry.sql_code)
    invariant(
      ['public', 'internal', 'migrator_only'].includes(entry.classification),
      `Invalid classification for ${entry.sql_code}.`,
    )
    if (entry.classification === 'migrator_only') {
      invariant(entry.public_code === null, `${entry.sql_code} migrator mapping must have null public_code.`)
    } else {
      invariant(
        typeof entry.public_code === 'string' && errorCodes.has(entry.public_code),
        `${entry.sql_code} maps to unknown public code ${entry.public_code}.`,
      )
    }
    for (const override of entry.operation_overrides ?? []) {
      invariant(
        ['prepared', 'dispatched_no_downstream_headers', 'downstream_headers_committed', 'business_output_started'].includes(
          override.phase,
        ),
        `${entry.sql_code} override uses unknown phase ${override.phase}.`,
      )
      invariant(
        typeof override.public_code === 'string' && errorCodes.has(override.public_code),
        `${entry.sql_code} override maps to unknown code ${override.public_code}.`,
      )
    }
  }

  const missing = [...extracted].filter((code) => !mapped.has(code)).sort()
  const stale = [...mapped].filter((code) => !extracted.has(code)).sort()
  const wrongSources = mapping.entries
    .filter(
      (entry) =>
        extracted.has(entry.sql_code) && !extractedBySource[entry.source].has(entry.sql_code),
    )
    .map((entry) => `${entry.sql_code} -> ${entry.source}`)
    .sort()
  invariant(missing.length === 0, `Literal P0001 codes missing from the map: ${missing.join(', ')}`)
  invariant(stale.length === 0, `Stale P0001 mappings: ${stale.join(', ')}`)
  invariant(
    wrongSources.length === 0,
    `P0001 mappings declare the wrong SQL source: ${wrongSources.join(', ')}`,
  )
  invariant(mapped.size === 94, `Expected 94 mapped P0001 codes; found ${mapped.size}.`)

  return { mappedCodes: mapped.size }
}

export async function validateSqlErrorMap(errorCodes) {
  const mapSource = await readFile(
    path.join(contractPaths.fixtures, 'sql-p0001-error-map.json'),
    'utf8',
  )
  const mapping = JSON.parse(mapSource)
  const sqlSources = Object.fromEntries(
    await Promise.all(
      SQL_FILES.map(async (name) => [
        name,
        await readFile(path.join(contractPaths.database, name), 'utf8'),
      ]),
    ),
  )
  const result = validateSqlErrorMapping({ errorCodes, mapping, sqlSources })
  return { ...result, mapping, sqlSources }
}
