import { readFile, readdir } from 'node:fs/promises'
import path from 'node:path'

import { contractPaths, invariant } from './context.mjs'

const JSON_FIXTURE_SCHEMAS = Object.freeze({
  'control-plane-validation-error.json': 'ControlPlaneProblem',
  'gateway-upstream-usage-out-of-range.json': 'GatewayProblem',
  'responses-function-tool-followup.json': 'ResponseCreateRequest',
  'chat-completions-tool-followup.json': 'ChatCompletionRequest',
})

function resolveErrorCodes(errorCatalog) {
  if (errorCatalog instanceof Set) {
    return errorCatalog
  }
  if (errorCatalog?.codes instanceof Set) {
    return errorCatalog.codes
  }
  if (Array.isArray(errorCatalog?.entries)) {
    return new Set(errorCatalog.entries.map(({ code }) => code))
  }
  throw new Error('Fixture validation requires an error-code Set or parsed error catalog.')
}

function validateKnownErrorCode(code, fixtureName, errorCatalog) {
  invariant(typeof code === 'string', `${fixtureName} error code is missing.`)
  invariant(resolveErrorCodes(errorCatalog).has(code), `${fixtureName} uses unknown error code ${code}.`)
}

function validateProblemCatalogSemantics(value, fixtureName, errorCatalog) {
  validateKnownErrorCode(value.code, fixtureName, errorCatalog)
  if (!Array.isArray(errorCatalog?.entries)) {
    return
  }

  const entry = errorCatalog.entries.find(({ code }) => code === value.code)
  invariant(entry, `${fixtureName} uses unknown error code ${value.code}.`)
  invariant(
    Array.isArray(entry.httpStatuses) && entry.httpStatuses.includes(value.status),
    `${fixtureName} status does not match catalog code ${value.code}.`,
  )
  if (typeof entry.retryable === 'boolean') {
    invariant(
      value.retryable === entry.retryable,
      `${fixtureName} retryable does not match catalog code ${value.code}.`,
    )
  }

  const hasRetryAfter = Object.hasOwn(value, 'retry_after_seconds')
  if (entry.retryAfter === 'fixed') {
    invariant(
      value.retry_after_seconds === entry.retryAfterSeconds,
      `${fixtureName} retry_after_seconds must equal ${entry.retryAfterSeconds} for ${value.code}.`,
    )
  } else if (entry.retryAfter === 'required') {
    invariant(
      Number.isSafeInteger(value.retry_after_seconds) && value.retry_after_seconds > 0,
      `${fixtureName} retry_after_seconds is required for ${value.code}.`,
    )
  } else if (entry.retryAfter === 'optional') {
    invariant(
      !hasRetryAfter ||
        (Number.isSafeInteger(value.retry_after_seconds) && value.retry_after_seconds > 0),
      `${fixtureName} retry_after_seconds must be a positive safe integer for ${value.code}.`,
    )
  } else {
    invariant(
      entry.retryAfter === 'none' && !hasRetryAfter,
      `${fixtureName} retry_after_seconds is forbidden for ${value.code}.`,
    )
  }
}

function validateJsonObject(serialized, fixtureName, context) {
  let value
  try {
    value = JSON.parse(serialized)
  } catch (error) {
    throw new Error(`${fixtureName} ${context} is not valid JSON.`, { cause: error })
  }
  invariant(
    value !== null && typeof value === 'object' && !Array.isArray(value),
    `${fixtureName} ${context} must encode a JSON object.`,
  )
}

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
  validateProblemCatalogSemantics(value, fixtureName, errorCodes)
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

export function validateControlPlaneProblemFixture(value, fixtureName, errorCodes) {
  invariant(value && typeof value === 'object', `${fixtureName} ControlPlane fixture must be an object.`)
  validateProblemCatalogSemantics(value, fixtureName, errorCodes)
  invariant(
    !Object.hasOwn(value, 'error'),
    `${fixtureName} ControlPlane fixture must not contain an OpenAI error projection.`,
  )
  if (value.code === 'validation_failed') {
    invariant(value.status === 422, `${fixtureName} validation_failed status must be 422.`)
    invariant(
      value.errors && typeof value.errors === 'object' && Object.keys(value.errors).length > 0,
      `${fixtureName} validation_failed must contain field errors.`,
    )
  }
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
  let responseIdentity = null
  let sawCreated = false
  let sawInProgress = false
  const itemsByIndex = new Map()
  const itemIndexesById = new Map()
  const textParts = new Map()
  let functionDoneCount = 0

  const requireResponseIdentity = (response, eventType, expectedStatus) => {
    invariant(response && typeof response === 'object', `${fixtureName} ${eventType} response is missing.`)
    invariant(typeof response.id === 'string', `${fixtureName} ${eventType} response id is missing.`)
    invariant(typeof response.model === 'string', `${fixtureName} ${eventType} response model is missing.`)
    invariant(
      response.status === expectedStatus,
      `${fixtureName} ${eventType} response status must be ${expectedStatus}.`,
    )
    if (responseIdentity === null) {
      responseIdentity = { id: response.id, model: response.model }
      return
    }
    invariant(
      response.id === responseIdentity.id,
      `${fixtureName} ${eventType} response id changed within the stream.`,
    )
    invariant(
      response.model === responseIdentity.model,
      `${fixtureName} ${eventType} response model changed within the stream.`,
    )
  }

  const requireItem = (itemId, outputIndex, context) => {
    const item = itemsByIndex.get(outputIndex)
    invariant(item, `${fixtureName} ${context} references an unknown output_index.`)
    invariant(item.id === itemId, `${fixtureName} ${context} item_id does not match output_index.`)
    invariant(!item.done, `${fixtureName} ${context} references a completed output item.`)
    return item
  }

  const textPartKey = (itemId, outputIndex, contentIndex) =>
    `${itemId}:${outputIndex}:${contentIndex}`

  const requireTextPart = (value, context) => {
    const item = requireItem(value.item_id, value.output_index, context)
    invariant(item.type === 'message', `${fixtureName} ${context} must reference a message item.`)
    const part = textParts.get(
      textPartKey(value.item_id, value.output_index, value.content_index),
    )
    invariant(part, `${fixtureName} ${context} references an unknown content part.`)
    return part
  }

  const validateMessageOutput = (item, output, context) => {
    invariant(output.id === item.id, `${fixtureName} ${context} message id changed.`)
    invariant(output.type === 'message', `${fixtureName} ${context} item type changed.`)
    invariant(output.status === 'completed', `${fixtureName} ${context} message must be completed.`)
    invariant(Array.isArray(output.content), `${fixtureName} ${context} message content is missing.`)
    const parts = [...textParts.values()]
      .filter((part) => part.itemId === item.id)
      .sort((left, right) => left.contentIndex - right.contentIndex)
    invariant(
      output.content.length === parts.length,
      `${fixtureName} ${context} message content count does not match streamed parts.`,
    )
    parts.forEach((part, contentIndex) => {
      invariant(part.contentDone, `${fixtureName} ${context} contains an unfinished content part.`)
      invariant(
        part.contentIndex === contentIndex,
        `${fixtureName} ${context} message content indexes are not contiguous.`,
      )
      invariant(
        output.content[contentIndex]?.type === 'output_text' &&
          output.content[contentIndex]?.text === part.text,
        `${fixtureName} ${context} message text does not match concatenated deltas.`,
      )
    })
  }

  const validateFunctionOutput = (item, output, context) => {
    invariant(output.id === item.id, `${fixtureName} ${context} function item id changed.`)
    invariant(output.type === 'function_call', `${fixtureName} ${context} item type changed.`)
    invariant(output.status === 'completed', `${fixtureName} ${context} function call must be completed.`)
    invariant(output.call_id === item.callId, `${fixtureName} ${context} function call_id changed.`)
    invariant(output.name === item.name, `${fixtureName} ${context} function name changed.`)
    invariant(item.argumentsDone, `${fixtureName} ${context} function arguments are unfinished.`)
    invariant(
      output.arguments === item.arguments,
      `${fixtureName} ${context} function arguments do not match concatenated deltas.`,
    )
  }

  frames.forEach((frame, index) => {
    invariant(
      terminal === null,
      `${fixtureName} contains data after terminal event ${terminal?.type}.`,
    )
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

    const isTerminalError = value.type === 'error'
    if (!isTerminalError && value.type !== 'response.created') {
      invariant(sawCreated, `${fixtureName} must start with response.created.`)
    }
    if (!isTerminalError && !['response.created', 'response.in_progress'].includes(value.type)) {
      invariant(sawInProgress, `${fixtureName} must emit response.in_progress before output or terminal events.`)
    }

    switch (value.type) {
      case 'response.created': {
        invariant(index === 0 && !sawCreated, `${fixtureName} must contain exactly one initial response.created event.`)
        requireResponseIdentity(value.response, value.type, 'in_progress')
        sawCreated = true
        break
      }
      case 'response.in_progress': {
        invariant(!sawInProgress, `${fixtureName} repeats response.in_progress.`)
        invariant(index === 1, `${fixtureName} response.in_progress must immediately follow response.created.`)
        requireResponseIdentity(value.response, value.type, 'in_progress')
        sawInProgress = true
        break
      }
      case 'response.output_item.added': {
        invariant(
          Number.isSafeInteger(value.output_index) && value.output_index >= 0,
          `${fixtureName} output item has an invalid output_index.`,
        )
        invariant(!itemsByIndex.has(value.output_index), `${fixtureName} repeats output_index ${value.output_index}.`)
        invariant(value.item && typeof value.item.id === 'string', `${fixtureName} output item id is missing.`)
        invariant(!itemIndexesById.has(value.item.id), `${fixtureName} repeats output item id ${value.item.id}.`)
        invariant(
          value.item.type === 'message' || value.item.type === 'function_call',
          `${fixtureName} contains an unsupported output item type.`,
        )
        invariant(value.item.status === 'in_progress', `${fixtureName} added output item must be in_progress.`)
        const item = {
          id: value.item.id,
          index: value.output_index,
          type: value.item.type,
          done: false,
        }
        if (value.item.type === 'function_call') {
          invariant(typeof value.item.call_id === 'string', `${fixtureName} function call_id is missing.`)
          invariant(typeof value.item.name === 'string', `${fixtureName} function name is missing.`)
          invariant(value.item.arguments === '', `${fixtureName} added function arguments must be empty.`)
          Object.assign(item, {
            arguments: '',
            argumentsDone: false,
            callId: value.item.call_id,
            name: value.item.name,
          })
        }
        itemsByIndex.set(value.output_index, item)
        itemIndexesById.set(value.item.id, value.output_index)
        break
      }
      case 'response.content_part.added': {
        const item = requireItem(value.item_id, value.output_index, 'content_part.added')
        invariant(item.type === 'message', `${fixtureName} content_part.added must reference a message item.`)
        invariant(
          Number.isSafeInteger(value.content_index) && value.content_index >= 0,
          `${fixtureName} content part has an invalid content_index.`,
        )
        invariant(
          value.part?.type === 'output_text' && typeof value.part.text === 'string',
          `${fixtureName} added content part must be output_text.`,
        )
        const key = textPartKey(value.item_id, value.output_index, value.content_index)
        invariant(!textParts.has(key), `${fixtureName} repeats a content part.`)
        textParts.set(key, {
          contentDone: false,
          contentIndex: value.content_index,
          deltas: '',
          itemId: value.item_id,
          outputIndex: value.output_index,
          text: value.part.text,
          textDone: false,
        })
        break
      }
      case 'response.output_text.delta': {
        const part = requireTextPart(value, 'output_text.delta')
        invariant(!part.textDone, `${fixtureName} contains a text delta after output_text.done.`)
        invariant(typeof value.delta === 'string', `${fixtureName} text delta must be a string.`)
        part.deltas += value.delta
        break
      }
      case 'response.output_text.done': {
        const part = requireTextPart(value, 'output_text.done')
        invariant(!part.textDone, `${fixtureName} repeats output_text.done.`)
        invariant(
          value.text === `${part.text}${part.deltas}`,
          `${fixtureName} output_text.done text does not match concatenated deltas.`,
        )
        part.text = value.text
        part.textDone = true
        break
      }
      case 'response.content_part.done': {
        const part = requireTextPart(value, 'content_part.done')
        invariant(part.textDone, `${fixtureName} content_part.done precedes output_text.done.`)
        invariant(!part.contentDone, `${fixtureName} repeats content_part.done.`)
        invariant(
          value.part?.type === 'output_text' && value.part.text === part.text,
          `${fixtureName} content_part.done text does not match concatenated deltas.`,
        )
        part.contentDone = true
        break
      }
      case 'response.function_call_arguments.delta': {
        const item = requireItem(value.item_id, value.output_index, 'function arguments delta')
        invariant(item.type === 'function_call', `${fixtureName} function delta must reference a function_call item.`)
        invariant(!item.argumentsDone, `${fixtureName} contains a function delta after arguments.done.`)
        invariant(typeof value.delta === 'string', `${fixtureName} function delta must be a string.`)
        item.arguments += value.delta
        break
      }
      case 'response.function_call_arguments.done': {
        const item = requireItem(value.item_id, value.output_index, 'function arguments done')
        invariant(item.type === 'function_call', `${fixtureName} function done must reference a function_call item.`)
        invariant(!item.argumentsDone, `${fixtureName} repeats function_call_arguments.done.`)
        invariant(value.name === item.name, `${fixtureName} function done name changed.`)
        invariant(
          item.arguments === value.arguments,
          `${fixtureName} function done arguments do not match concatenated deltas.`,
        )
        validateJsonObject(value.arguments, fixtureName, 'function arguments')
        item.argumentsDone = true
        functionDoneCount += 1
        break
      }
      case 'response.output_item.done': {
        const item = itemsByIndex.get(value.output_index)
        invariant(item, `${fixtureName} output_item.done references an unknown output_index.`)
        invariant(!item.done, `${fixtureName} repeats output_item.done.`)
        if (item.type === 'message') {
          validateMessageOutput(item, value.item, 'output_item.done')
        } else {
          validateFunctionOutput(item, value.item, 'output_item.done')
        }
        item.done = true
        break
      }
      case 'response.completed': {
        requireResponseIdentity(value.response, value.type, 'completed')
        validateSafeUsage(
          value.response.usage,
          ['input_tokens', 'output_tokens', 'total_tokens'],
          fixtureName,
          'Responses completed',
        )
        invariant(Array.isArray(value.response.output), `${fixtureName} completed response output is missing.`)
        invariant(
          value.response.output.length === itemsByIndex.size,
          `${fixtureName} completed output count does not match streamed items.`,
        )
        value.response.output.forEach((output, outputIndex) => {
          const item = itemsByIndex.get(outputIndex)
          invariant(item, `${fixtureName} completed output indexes are not contiguous.`)
          invariant(item.done, `${fixtureName} completed response contains an unfinished output item.`)
          if (item.type === 'message') {
            validateMessageOutput(item, output, 'completed response')
          } else {
            validateFunctionOutput(item, output, 'completed response')
          }
        })
        terminal = { index, type: value.type }
        break
      }
      case 'error': {
        validateKnownErrorCode(value.code, fixtureName, errorCodes)
        terminal = { code: value.code, index, type: value.type }
        break
      }
      default:
        invariant(false, `${fixtureName} contains unsupported Responses event ${value.type}.`)
    }

    validateSchema('ResponseStreamEvent', value)
  })

  invariant(terminal !== null, `${fixtureName} has no terminal Responses event.`)
  invariant(terminal.index === frames.length - 1, `${fixtureName} terminal event must be last.`)

  if (terminal.type !== 'error') {
    invariant(sawCreated, `${fixtureName} has no response.created event.`)
    invariant(sawInProgress, `${fixtureName} has no response.in_progress event.`)
  }

  if (fixtureName.includes('usage-out-of-range')) {
    invariant(terminal.code === 'upstream_usage_out_of_range', `${fixtureName} has the wrong terminal code.`)
  } else if (fixtureName.includes('first-byte-timeout')) {
    invariant(terminal.code === 'upstream_first_byte_timeout', `${fixtureName} has the wrong terminal code.`)
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

export function validateChatSse(
  source,
  fixtureName,
  validateSchemaOrErrorCodes,
  optionalErrorCodes,
) {
  const validateSchema =
    typeof validateSchemaOrErrorCodes === 'function' ? validateSchemaOrErrorCodes : null
  const errorCodes = validateSchema === null ? validateSchemaOrErrorCodes : optionalErrorCodes
  const frames = parseSse(source, fixtureName)
  const omitsUsageChunk = fixtureName === 'chat-completions-text-no-usage.sse'
  let phase = 'business'
  let identity = null
  let terminal = null
  let sawBusiness = false
  const choices = new Map()

  const validateIdentity = (value, context) => {
    invariant(typeof value.id === 'string', `${fixtureName} ${context} id is missing.`)
    invariant(typeof value.model === 'string', `${fixtureName} ${context} model is missing.`)
    invariant(Number.isSafeInteger(value.created), `${fixtureName} ${context} created is invalid.`)
    if (identity === null) {
      identity = { created: value.created, id: value.id, model: value.model }
      return
    }
    invariant(value.id === identity.id, `${fixtureName} Chat id changed within the stream.`)
    invariant(value.model === identity.model, `${fixtureName} Chat model changed within the stream.`)
    invariant(value.created === identity.created, `${fixtureName} Chat created changed within the stream.`)
  }

  const processToolCallDeltas = (choice, choiceState) => {
    const toolCalls = choice.delta?.tool_calls ?? []
    invariant(Array.isArray(toolCalls), `${fixtureName} Chat tool_calls delta must be an array.`)
    const indexesInFrame = new Set()
    for (const toolCall of toolCalls) {
      invariant(
        Number.isSafeInteger(toolCall.index) && toolCall.index >= 0,
        `${fixtureName} Chat tool call has an invalid index.`,
      )
      invariant(!indexesInFrame.has(toolCall.index), `${fixtureName} repeats a tool call index in one chunk.`)
      indexesInFrame.add(toolCall.index)
      let aggregate = choiceState.tools.get(toolCall.index)
      if (!aggregate) {
        invariant(typeof toolCall.id === 'string', `${fixtureName} initial tool call delta lacks id.`)
        invariant(toolCall.type === 'function', `${fixtureName} initial tool call type must be function.`)
        invariant(
          typeof toolCall.function?.name === 'string',
          `${fixtureName} initial tool call delta lacks function name.`,
        )
        invariant(
          [...choiceState.tools.values()].every(({ id }) => id !== toolCall.id),
          `${fixtureName} repeats a Chat tool call id.`,
        )
        aggregate = {
          arguments: '',
          id: toolCall.id,
          name: toolCall.function.name,
          type: toolCall.type,
        }
        choiceState.tools.set(toolCall.index, aggregate)
      } else {
        invariant(
          toolCall.id === undefined || toolCall.id === aggregate.id,
          `${fixtureName} Chat tool call id changed within the stream.`,
        )
        invariant(
          toolCall.type === undefined || toolCall.type === aggregate.type,
          `${fixtureName} Chat tool call type changed within the stream.`,
        )
        invariant(
          toolCall.function?.name === undefined || toolCall.function.name === aggregate.name,
          `${fixtureName} Chat tool call name changed within the stream.`,
        )
      }
      if (toolCall.function?.arguments !== undefined) {
        invariant(
          typeof toolCall.function.arguments === 'string',
          `${fixtureName} Chat tool call arguments delta must be a string.`,
        )
        aggregate.arguments += toolCall.function.arguments
      }
    }
  }

  frames.forEach((frame, index) => {
    invariant(frame.event === undefined, `${fixtureName} Chat frame ${index} must not declare event.`)
    invariant(terminal === null, `${fixtureName} contains data after a terminal frame.`)

    if (frame.data === '[DONE]') {
      invariant(
        phase === (omitsUsageChunk ? 'finished' : 'usage'),
        omitsUsageChunk
          ? `${fixtureName} [DONE] must immediately follow the finish chunk.`
          : `${fixtureName} [DONE] must follow the usage chunk.`,
      )
      terminal = { index, type: 'done' }
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
      validateSchema?.('ChatCompletionErrorEvent', value)
      const projection = value.error
      invariant(
        projection && typeof projection === 'object' && typeof projection.code === 'string',
        `${fixtureName} error projection is malformed.`,
      )
      validateKnownErrorCode(projection.code, fixtureName, errorCodes)
      invariant(projection.param === null, `${fixtureName} Chat error param must be null.`)
      invariant(phase === 'business', `${fixtureName} Chat error cannot follow finish or usage.`)
      terminal = { code: projection.code, index, type: 'error' }
      invariant(index === frames.length - 1, `${fixtureName} Chat error must be last.`)
      return
    }

    validateSchema?.('ChatCompletionChunk', value)
    invariant(value.object === 'chat.completion.chunk', `${fixtureName} has an unknown Chat object.`)
    invariant(Array.isArray(value.choices), `${fixtureName} Chat choices must be an array.`)
    validateIdentity(value, 'chunk')
    if (value.usage === null || value.usage === undefined) {
      invariant(
        omitsUsageChunk || (Object.hasOwn(value, 'usage') && value.usage === null),
        `${fixtureName} include_usage business and finish chunks must contain usage: null.`,
      )
      invariant(phase === 'business', `${fixtureName} contains a business chunk after finish.`)
      invariant(value.choices.length > 0, `${fixtureName} content chunk must have choices.`)
      const choiceIndexes = new Set()
      let finishedChoices = 0
      for (const choice of value.choices) {
        invariant(
          Number.isSafeInteger(choice.index) && choice.index >= 0,
          `${fixtureName} Chat choice index is invalid.`,
        )
        invariant(!choiceIndexes.has(choice.index), `${fixtureName} repeats a choice index in one chunk.`)
        choiceIndexes.add(choice.index)
        let choiceState = choices.get(choice.index)
        if (!choiceState) {
          choiceState = { tools: new Map() }
          choices.set(choice.index, choiceState)
        }
        if (choice.finish_reason === null) {
          choiceState.sawBusiness = true
          sawBusiness = true
          processToolCallDeltas(choice, choiceState)
        } else {
          finishedChoices += 1
          invariant(choiceState.sawBusiness, `${fixtureName} finish chunk precedes business output.`)
          invariant(
            (choice.delta?.tool_calls?.length ?? 0) === 0 &&
              (choice.delta?.content === undefined ||
                choice.delta.content === null ||
                choice.delta.content === ''),
            `${fixtureName} finish chunk must not contain business output.`,
          )
          if (choiceState.tools.size > 0) {
            invariant(choice.finish_reason === 'tool_calls', `${fixtureName} tool calls must finish with tool_calls.`)
            const toolIndexes = [...choiceState.tools.keys()].sort((left, right) => left - right)
            invariant(
              toolIndexes.every((toolIndex, position) => toolIndex === position),
              `${fixtureName} Chat tool call indexes must be contiguous from zero.`,
            )
            for (const [toolIndex, tool] of choiceState.tools) {
              validateJsonObject(
                tool.arguments,
                fixtureName,
                `Chat tool call ${choice.index}:${toolIndex} arguments`,
              )
            }
          } else {
            invariant(choice.finish_reason !== 'tool_calls', `${fixtureName} tool_calls finish has no tool call.`)
          }
        }
      }
      invariant(
        finishedChoices === 0 || finishedChoices === value.choices.length,
        `${fixtureName} finish chunk mixes finished and unfinished choices.`,
      )
      if (finishedChoices > 0) {
        invariant(sawBusiness, `${fixtureName} finish chunk precedes business output.`)
        invariant(
          value.choices.length === choices.size &&
            value.choices.every((choice) => choices.has(choice.index)),
          `${fixtureName} finish chunk must finish every streamed choice.`,
        )
        phase = 'finished'
      }
      return
    }

    invariant(!omitsUsageChunk, `${fixtureName} must not contain a usage chunk.`)
    invariant(phase === 'finished', `${fixtureName} usage chunk must follow the finish chunk.`)
    invariant(value.choices.length === 0, `${fixtureName} usage chunk choices must be empty.`)
    validateSafeUsage(
      value.usage,
      ['prompt_tokens', 'completion_tokens', 'total_tokens'],
      fixtureName,
      'Chat',
    )
    phase = 'usage'
  })

  invariant(terminal !== null, `${fixtureName} has no terminal Chat frame.`)
  invariant(terminal.index === frames.length - 1, `${fixtureName} terminal Chat frame must be last.`)
  if (fixtureName.includes('usage-out-of-range')) {
    invariant(terminal.code === 'upstream_usage_out_of_range', `${fixtureName} has the wrong terminal code.`)
  } else if (fixtureName.includes('error')) {
    invariant(terminal.code === 'upstream_stream_error', `${fixtureName} has the wrong terminal code.`)
  } else {
    invariant(terminal.type === 'done', `${fixtureName} normal Chat stream must end with [DONE].`)
    invariant(
      phase === (omitsUsageChunk ? 'finished' : 'usage'),
      omitsUsageChunk
        ? `${fixtureName} must finish without a usage chunk.`
        : `${fixtureName} normal Chat stream must include a usage chunk.`,
    )
  }

  return frames.length
}

export async function validateFixtures(validateSchema, catalog) {
  const names = (await readdir(contractPaths.fixtures)).sort()
  const expectedNames = [
    ...Object.keys(JSON_FIXTURE_SCHEMAS),
    'chat-completions-error.sse',
    'chat-completions-function-call.sse',
    'chat-completions-text.sse',
    'chat-completions-text-no-usage.sse',
    'chat-completions-usage-out-of-range.sse',
    'responses-stream-completed.sse',
    'responses-stream-error.sse',
    'responses-stream-first-byte-timeout.sse',
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
        validateGatewayProblemFixture(value, name, catalog)
      } else if (JSON_FIXTURE_SCHEMAS[name] === 'ControlPlaneProblem') {
        validateControlPlaneProblemFixture(value, name, catalog)
      }
      validated += 1
    } else if (name.startsWith('responses-') && name.endsWith('.sse')) {
      sseFrames += validateResponsesSse(source, name, validateSchema, catalog)
      validated += 1
    } else if (name.startsWith('chat-completions-') && name.endsWith('.sse')) {
      sseFrames += validateChatSse(source, name, validateSchema, catalog)
      validated += 1
    }
  }

  return { fixtureCount: validated + 1, sseFrames }
}
