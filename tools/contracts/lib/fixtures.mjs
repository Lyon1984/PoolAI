import { readFile, readdir } from 'node:fs/promises'
import path from 'node:path'

import { contractPaths, invariant } from './context.mjs'

const JSON_FIXTURE_SCHEMAS = Object.freeze({
  'gateway-upstream-usage-out-of-range.json': 'GatewayProblem',
  'responses-function-tool-followup.json': 'ResponseCreateRequest',
  'chat-completions-tool-followup.json': 'ChatCompletionRequest',
})

function parseSse(source, fixtureName) {
  const normalized = source.replaceAll('\r\n', '\n').trim()
  invariant(normalized.length > 0, `${fixtureName} is empty.`)
  return normalized.split(/\n\n+/u).map((block, index) => {
    const event = { dataLines: [] }
    for (const line of block.split('\n')) {
      if (line === '' || line.startsWith(':')) {
        continue
      }
      const separator = line.indexOf(':')
      const field = separator === -1 ? line : line.slice(0, separator)
      let value = separator === -1 ? '' : line.slice(separator + 1)
      if (value.startsWith(' ')) {
        value = value.slice(1)
      }
      if (field === 'event') {
        invariant(event.event === undefined, `${fixtureName} frame ${index} repeats event.`)
        event.event = value
      } else if (field === 'data') {
        event.dataLines.push(value)
      } else if (field === 'id' || field === 'retry') {
        event[field] = value
      } else {
        throw new Error(`${fixtureName} frame ${index} uses unsupported SSE field ${field}.`)
      }
    }
    invariant(event.dataLines.length > 0, `${fixtureName} frame ${index} has no data.`)
    return {
      ...event,
      data: event.dataLines.join('\n'),
    }
  })
}

export function validateGatewayProblemFixture(value, fixtureName, errorCodes) {
  invariant(value && typeof value === 'object', `${fixtureName} Gateway fixture must be an object.`)
  invariant(typeof value.code === 'string', `${fixtureName} outer error code is missing.`)
  invariant(errorCodes.has(value.code), `${fixtureName} uses unknown error code ${value.code}.`)
  invariant(value.error && typeof value.error === 'object', `${fixtureName} error projection is missing.`)
  invariant(
    value.error.code === value.code,
    `${fixtureName} outer code must equal error.code.`,
  )
  invariant(
    value.error.message === value.detail,
    `${fixtureName} detail must equal error.message.`,
  )
}

function validateSafeUsage(usage, fields, fixtureName, context) {
  invariant(usage && typeof usage === 'object', `${fixtureName} ${context} usage is missing.`)
  for (const field of fields) {
    invariant(
      Number.isSafeInteger(usage[field]) && usage[field] >= 0,
      `${fixtureName} ${context} ${field} must be a non-negative safe integer.`,
    )
  }
  invariant(
    usage[fields[2]] === usage[fields[0]] + usage[fields[1]],
    `${fixtureName} ${context} usage totals do not add up.`,
  )
}

export function validateResponsesSse(source, fixtureName, validateSchema, errorCodes) {
  const frames = parseSse(source, fixtureName)
  let terminal = null
  const functionArguments = new Map()
  let functionDoneCount = 0

  frames.forEach((frame, index) => {
    invariant(frame.event, `${fixtureName} Responses frame ${index} must declare event.`)
    invariant(frame.data !== '[DONE]', `${fixtureName} Responses stream must not contain [DONE].`)
    let value
    try {
      value = JSON.parse(frame.data)
    } catch (error) {
      throw new Error(`${fixtureName} frame ${index} data is not JSON.`, { cause: error })
    }
    invariant(value && typeof value === 'object', `${fixtureName} frame ${index} data must be an object.`)
    invariant(frame.event === value.type, `${fixtureName} frame ${index} event must equal data.type.`)
    invariant(
      Number.isSafeInteger(value.sequence_number) && value.sequence_number === index,
      `${fixtureName} frame ${index} has non-contiguous sequence_number.`,
    )

    if (value.type === 'error') {
      validateSchema('ResponseErrorEvent', value)
      invariant(errorCodes.has(value.code), `${fixtureName} uses unknown error code ${value.code}.`)
      terminal = { code: value.code, index, type: value.type }
    } else if (value.type === 'response.completed') {
      validateSafeUsage(
        value.response?.usage,
        ['input_tokens', 'output_tokens', 'total_tokens'],
        fixtureName,
        'Responses completed',
      )
      for (const output of value.response?.output ?? []) {
        if (output?.type === 'function_call') {
          invariant(
            functionArguments.get(output.id) === output.arguments,
            `${fixtureName} completed function arguments do not match concatenated deltas.`,
          )
        }
      }
      terminal = { index, type: value.type }
    } else if (value.type === 'response.function_call_arguments.delta') {
      invariant(typeof value.item_id === 'string', `${fixtureName} function delta lacks item_id.`)
      invariant(typeof value.delta === 'string', `${fixtureName} function delta must be a string.`)
      functionArguments.set(
        value.item_id,
        `${functionArguments.get(value.item_id) ?? ''}${value.delta}`,
      )
    } else if (value.type === 'response.function_call_arguments.done') {
      invariant(
        functionArguments.get(value.item_id) === value.arguments,
        `${fixtureName} function done arguments do not match concatenated deltas.`,
      )
      functionDoneCount += 1
    } else if (value.type === 'response.output_item.done' && value.item?.type === 'function_call') {
      invariant(
        functionArguments.get(value.item.id) === value.item.arguments,
        `${fixtureName} output item arguments do not match concatenated deltas.`,
      )
    }

    invariant(
      terminal === null || terminal.index === index,
      `${fixtureName} contains data after terminal event ${terminal?.type}.`,
    )
  })

  invariant(terminal !== null, `${fixtureName} has no terminal Responses event.`)
  invariant(terminal.index === frames.length - 1, `${fixtureName} terminal event must be last.`)

  if (fixtureName.includes('usage-out-of-range')) {
    invariant(terminal.code === 'upstream_usage_out_of_range', `${fixtureName} has the wrong terminal code.`)
  } else if (fixtureName.includes('error')) {
    invariant(terminal.code === 'upstream_stream_error', `${fixtureName} has the wrong terminal code.`)
  } else {
    invariant(terminal.type === 'response.completed', `${fixtureName} must complete normally.`)
    if (fixtureName.includes('function-call')) {
      invariant(functionDoneCount > 0, `${fixtureName} must finish at least one function argument stream.`)
    } else {
      invariant(functionDoneCount === 0, `${fixtureName} unexpectedly contains function arguments.`)
    }
  }

  return frames.length
}

export function validateChatSse(source, fixtureName, errorCodes) {
  const frames = parseSse(source, fixtureName)
  let sawDone = false
  let terminalError = null
  let sawUsage = false

  frames.forEach((frame, index) => {
    invariant(frame.event === undefined, `${fixtureName} Chat frame ${index} must not declare event.`)
    invariant(!sawDone && terminalError === null, `${fixtureName} contains data after a terminal frame.`)

    if (frame.data === '[DONE]') {
      sawDone = true
      invariant(index === frames.length - 1, `${fixtureName} [DONE] must be last.`)
      return
    }

    let value
    try {
      value = JSON.parse(frame.data)
    } catch (error) {
      throw new Error(`${fixtureName} frame ${index} data is not JSON.`, { cause: error })
    }
    invariant(value && typeof value === 'object', `${fixtureName} frame ${index} data must be an object.`)

    if ('error' in value) {
      const projection = value.error
      invariant(
        projection && typeof projection === 'object' && typeof projection.code === 'string',
        `${fixtureName} error projection is malformed.`,
      )
      invariant(errorCodes.has(projection.code), `${fixtureName} uses unknown error code ${projection.code}.`)
      invariant(projection.param === null, `${fixtureName} Chat error param must be null.`)
      terminalError = projection.code
      invariant(index === frames.length - 1, `${fixtureName} Chat error must be last.`)
      return
    }

    invariant(value.object === 'chat.completion.chunk', `${fixtureName} has an unknown Chat object.`)
    invariant(Array.isArray(value.choices), `${fixtureName} Chat choices must be an array.`)
    if (value.usage === null) {
      invariant(value.choices.length > 0, `${fixtureName} content chunk must have choices.`)
      return
    }

    const usage = value.usage
    invariant(value.choices.length === 0, `${fixtureName} usage chunk choices must be empty.`)
    for (const field of ['prompt_tokens', 'completion_tokens', 'total_tokens']) {
      invariant(Number.isSafeInteger(usage?.[field]) && usage[field] >= 0, `${fixtureName} invalid ${field}.`)
    }
    invariant(
      usage.total_tokens === usage.prompt_tokens + usage.completion_tokens,
      `${fixtureName} usage fields do not add up.`,
    )
    invariant(!sawUsage, `${fixtureName} repeats its usage chunk.`)
    sawUsage = true
  })

  if (fixtureName.includes('usage-out-of-range')) {
    invariant(terminalError === 'upstream_usage_out_of_range', `${fixtureName} has the wrong terminal code.`)
    invariant(!sawDone, `${fixtureName} must not send [DONE] after an error.`)
  } else if (fixtureName.includes('error')) {
    invariant(terminalError === 'upstream_stream_error', `${fixtureName} has the wrong terminal code.`)
    invariant(!sawDone, `${fixtureName} must not send [DONE] after an error.`)
  } else {
    invariant(sawDone, `${fixtureName} normal Chat stream must end with [DONE].`)
    invariant(terminalError === null, `${fixtureName} normal Chat stream contains an error.`)
    invariant(sawUsage, `${fixtureName} normal Chat stream must include a usage chunk.`)
  }

  return frames.length
}

export async function validateFixtures(validateSchema, errorCodes) {
  const names = (await readdir(contractPaths.fixtures)).sort()
  const expectedNames = [
    ...Object.keys(JSON_FIXTURE_SCHEMAS),
    'chat-completions-error.sse',
    'chat-completions-function-call.sse',
    'chat-completions-text.sse',
    'chat-completions-usage-out-of-range.sse',
    'responses-stream-completed.sse',
    'responses-stream-error.sse',
    'responses-stream-function-call.sse',
    'responses-stream-usage-out-of-range.sse',
    'sql-p0001-error-map.json',
  ].sort()
  invariant(
    JSON.stringify(names) === JSON.stringify(expectedNames),
    `Fixture inventory changed. Expected ${expectedNames.join(', ')}; found ${names.join(', ')}. Update validation explicitly.`,
  )

  let validated = 0
  let sseFrames = 0
  for (const name of names) {
    const source = await readFile(path.join(contractPaths.fixtures, name), 'utf8')
    if (name in JSON_FIXTURE_SCHEMAS) {
      const value = JSON.parse(source)
      validateSchema(JSON_FIXTURE_SCHEMAS[name], value)
      if (JSON_FIXTURE_SCHEMAS[name] === 'GatewayProblem') {
        validateGatewayProblemFixture(value, name, errorCodes)
      }
      validated += 1
    } else if (name.startsWith('responses-') && name.endsWith('.sse')) {
      sseFrames += validateResponsesSse(source, name, validateSchema, errorCodes)
      validated += 1
    } else if (name.startsWith('chat-completions-') && name.endsWith('.sse')) {
      sseFrames += validateChatSse(source, name, errorCodes)
      validated += 1
    }
  }

  return { fixtureCount: validated + 1, sseFrames }
}
