import assert from "node:assert/strict";
import dgram from "node:dgram";
import http from "node:http";

const host = process.env.MOCK_UPSTREAM_HOST ?? "0.0.0.0";
const port = Number.parseInt(process.env.MOCK_UPSTREAM_PORT ?? "4010", 10);
const ntpHost = process.env.MOCK_NTP_HOST ?? "0.0.0.0";
const ntpPort = Number.parseInt(process.env.MOCK_NTP_PORT ?? "4123", 10);
const environment = process.env.MOCK_UPSTREAM_ENVIRONMENT ?? "";
const maxBodyBytes = 1024 * 1024;
const createdAt = 1_700_000_000;
const ntpEpochOffsetSeconds = 2_208_988_800n;
const ntpFractionScale = 4_294_967_296n;

for (const [name, value] of [["MOCK_UPSTREAM_PORT", port], ["MOCK_NTP_PORT", ntpPort]]) {
  if (!Number.isInteger(value) || value < 1 || value > 65_535) {
    throw new Error(`${name} must be an integer from 1 through 65535.`);
  }
}

const ntpControl = {
  mode: "normal",
  offsetMilliseconds: 0,
};

const scenarios = new Set([
  "success",
  "http-401",
  "http-403",
  "http-429",
  "http-500",
  "stream-error",
  "disconnect-after-event",
  "usage-out-of-range",
]);

function writeNtpTimestamp(buffer, offset, unixMilliseconds) {
  const wholeSeconds = Math.floor(unixMilliseconds / 1000);
  const remainderMilliseconds = unixMilliseconds - (wholeSeconds * 1000);
  const seconds = BigInt(wholeSeconds) + ntpEpochOffsetSeconds;
  const fraction = (BigInt(Math.floor(remainderMilliseconds * 1_000_000)) * ntpFractionScale)
    / 1_000_000_000n;

  buffer.writeUInt32BE(Number(seconds & 0xffff_ffffn), offset);
  buffer.writeUInt32BE(Number(fraction & 0xffff_ffffn), offset + 4);
}

function readNtpTimestamp(buffer, offset) {
  const seconds = BigInt(buffer.readUInt32BE(offset));
  const fraction = BigInt(buffer.readUInt32BE(offset + 4));
  return Number(seconds - ntpEpochOffsetSeconds) * 1000
    + (Number(fraction) * 1000 / Number(ntpFractionScale));
}

function createSntpResponse(request, receivedAt, sentAt, offsetMilliseconds) {
  if (request.length < 48) {
    return null;
  }

  const version = (request[0] >> 3) & 0x07;
  const mode = request[0] & 0x07;
  const requestTransmitTimestamp = request.subarray(40, 48);
  if (
    version < 3
    || mode !== 3
    || requestTransmitTimestamp.every((value) => value === 0)
  ) {
    return null;
  }

  const response = Buffer.alloc(48);
  response[0] = (4 << 3) | 4; // leap=0, version=4, server mode=4
  response[1] = 1; // local deterministic reference clock
  response[2] = request[2];
  response.writeInt8(-20, 3);
  response.writeUInt32BE(0, 4); // root delay
  response.writeUInt32BE(66, 8); // approximately one millisecond dispersion
  response.write("LOCL", 12, 4, "ascii");

  const adjustedReceiveTime = receivedAt + offsetMilliseconds;
  const adjustedTransmitTime = sentAt + offsetMilliseconds;
  writeNtpTimestamp(response, 16, adjustedReceiveTime - 1000);
  requestTransmitTimestamp.copy(response, 24);
  writeNtpTimestamp(response, 32, adjustedReceiveTime);
  writeNtpTimestamp(response, 40, adjustedTransmitTime);
  return response;
}

function runSntpSelfTest() {
  const clientTransmitTime = 1_700_000_000_000;
  const request = Buffer.alloc(48);
  request[0] = (4 << 3) | 3;
  request[2] = 6;
  writeNtpTimestamp(request, 40, clientTransmitTime);

  for (const injectedOffset of [0, 6000, -6000]) {
    const serverReceiveTime = clientTransmitTime + 10;
    const serverTransmitTime = clientTransmitTime + 12;
    const clientReceiveTime = clientTransmitTime + 22;
    const response = createSntpResponse(
      request,
      serverReceiveTime,
      serverTransmitTime,
      injectedOffset,
    );

    assert.ok(response);
    assert.equal(response.length, 48);
    assert.notEqual(response[0] >> 6, 3);
    assert.equal((response[0] >> 3) & 0x07, 4);
    assert.equal(response[0] & 0x07, 4);
    assert.equal(response[1], 1);
    assert.deepEqual(response.subarray(24, 32), request.subarray(40, 48));
    assert.ok(response.subarray(32, 40).some((value) => value !== 0));
    assert.ok(response.subarray(40, 48).some((value) => value !== 0));

    const originateTime = readNtpTimestamp(response, 24);
    const receiveTime = readNtpTimestamp(response, 32);
    const transmitTime = readNtpTimestamp(response, 40);
    const calculatedOffset = ((receiveTime - originateTime) + (transmitTime - clientReceiveTime)) / 2;
    assert.ok(
      Math.abs(calculatedOffset - injectedOffset) < 0.01,
      `expected ${injectedOffset}ms offset, calculated ${calculatedOffset}ms`,
    );
  }

  const invalidMode = Buffer.from(request);
  invalidMode[0] = (4 << 3) | 4;
  assert.equal(createSntpResponse(invalidMode, 0, 0, 0), null);
  assert.equal(createSntpResponse(Buffer.alloc(47), 0, 0, 0), null);
  process.stdout.write("Mock SNTP v4 packet, originate timestamp, and offset calculations passed.\n");
}

function writeJson(response, statusCode, body, extraHeaders = {}) {
  const payload = JSON.stringify(body);
  response.writeHead(statusCode, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(payload),
    "cache-control": "no-store",
    ...extraHeaders,
  });
  response.end(payload);
}

function writeError(response, statusCode, code, message, extraHeaders = {}) {
  writeJson(
    response,
    statusCode,
    {
      error: {
        message,
        type: "mock_upstream_error",
        param: null,
        code,
      },
    },
    extraHeaders,
  );
}

async function readJson(request) {
  const chunks = [];
  let length = 0;

  for await (const chunk of request) {
    length += chunk.length;
    if (length > maxBodyBytes) {
      const error = new Error("request body too large");
      error.statusCode = 413;
      throw error;
    }
    chunks.push(chunk);
  }

  if (chunks.length === 0) {
    return {};
  }

  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    const error = new Error("request body must be valid JSON");
    error.statusCode = 400;
    throw error;
  }
}

function selectedScenario(request) {
  const value = String(request.headers["x-poolai-mock-scenario"] ?? "success").toLowerCase();
  return scenarios.has(value) ? value : null;
}

function maybeWriteHttpError(response, scenario) {
  switch (scenario) {
    case "http-401":
      writeError(response, 401, "invalid_api_key", "The mock credential was rejected.");
      return true;
    case "http-403":
      writeError(response, 403, "permission_denied", "The mock credential lacks access.");
      return true;
    case "http-429":
      writeError(response, 429, "rate_limit_exceeded", "The mock upstream is rate limited.", {
        "retry-after": "2",
      });
      return true;
    case "http-500":
      writeError(response, 500, "mock_internal_error", "The mock upstream failed.");
      return true;
    default:
      return false;
  }
}

function usageFor(scenario) {
  if (scenario === "usage-out-of-range") {
    return {
      input_tokens: 9_007_199_254_740_992,
      output_tokens: 0,
      total_tokens: 9_007_199_254_740_992,
    };
  }

  return {
    input_tokens: 4,
    output_tokens: 2,
    total_tokens: 6,
  };
}

function responseObject(model, scenario) {
  return {
    id: "resp_poolai_mock_001",
    object: "response",
    created_at: createdAt,
    status: "completed",
    model,
    output: [
      {
        id: "msg_poolai_mock_001",
        type: "message",
        status: "completed",
        role: "assistant",
        content: [
          {
            type: "output_text",
            text: "PoolAI mock response",
            annotations: [],
          },
        ],
      },
    ],
    usage: usageFor(scenario),
  };
}

function chatObject(model, scenario) {
  return {
    id: "chatcmpl_poolai_mock_001",
    object: "chat.completion",
    created: createdAt,
    model,
    choices: [
      {
        index: 0,
        message: {
          role: "assistant",
          content: "PoolAI mock response",
        },
        finish_reason: "stop",
      },
    ],
    usage: usageFor(scenario),
  };
}

function beginEventStream(response) {
  response.writeHead(200, {
    "content-type": "text/event-stream; charset=utf-8",
    "cache-control": "no-store",
    connection: "keep-alive",
    "x-accel-buffering": "no",
  });
  response.flushHeaders?.();
}

function writeEvent(response, eventName, data) {
  if (eventName !== null) {
    response.write(`event: ${eventName}\n`);
  }
  response.write(`data: ${typeof data === "string" ? data : JSON.stringify(data)}\n\n`);
}

function writeResponsesStream(response, model, scenario) {
  beginEventStream(response);
  const responseValue = responseObject(model, scenario);
  const created = { ...responseValue, status: "in_progress", output: [], usage: null };

  writeEvent(response, "response.created", {
    type: "response.created",
    sequence_number: 0,
    response: created,
  });

  if (scenario === "disconnect-after-event") {
    setTimeout(() => response.destroy(), 10);
    return;
  }

  if (scenario === "stream-error") {
    writeEvent(response, "error", {
      type: "error",
      sequence_number: 1,
      code: "mock_stream_error",
      message: "The mock stream terminated with an error.",
      param: null,
    });
    response.end();
    return;
  }

  writeEvent(response, "response.output_text.delta", {
    type: "response.output_text.delta",
    sequence_number: 1,
    item_id: "msg_poolai_mock_001",
    output_index: 0,
    content_index: 0,
    delta: "PoolAI mock response",
  });
  writeEvent(response, "response.completed", {
    type: "response.completed",
    sequence_number: 2,
    response: responseValue,
  });
  response.end();
}

function writeChatStream(response, model, scenario) {
  beginEventStream(response);
  writeEvent(response, null, {
    id: "chatcmpl_poolai_mock_001",
    object: "chat.completion.chunk",
    created: createdAt,
    model,
    choices: [{ index: 0, delta: { role: "assistant" }, finish_reason: null }],
  });

  if (scenario === "disconnect-after-event") {
    setTimeout(() => response.destroy(), 10);
    return;
  }

  if (scenario === "stream-error") {
    writeEvent(response, null, {
      error: {
        message: "The mock stream terminated with an error.",
        type: "mock_upstream_error",
        param: null,
        code: "mock_stream_error",
      },
    });
    response.end();
    return;
  }

  writeEvent(response, null, {
    id: "chatcmpl_poolai_mock_001",
    object: "chat.completion.chunk",
    created: createdAt,
    model,
    choices: [
      {
        index: 0,
        delta: { content: "PoolAI mock response" },
        finish_reason: "stop",
      },
    ],
    usage: usageFor(scenario),
  });
  writeEvent(response, null, "[DONE]");
  response.end();
}

function isLoopbackTestControlRequest(request) {
  if (environment !== "LocalCompose") {
    return false;
  }

  try {
    const requestHost = new URL(`http://${request.headers.host ?? ""}`).hostname;
    return ["127.0.0.1", "localhost", "[::1]", "::1"].includes(requestHost);
  } catch {
    return false;
  }
}

async function updateNtpControl(request, response) {
  const body = await readJson(request);
  if (body.mode === "reset") {
    ntpControl.mode = "normal";
    ntpControl.offsetMilliseconds = 0;
  } else if (body.mode === "drop") {
    ntpControl.mode = "drop";
    ntpControl.offsetMilliseconds = 0;
  } else if (
    body.mode === "offset"
    && Number.isSafeInteger(body.offsetMilliseconds)
    && body.offsetMilliseconds >= -60_000
    && body.offsetMilliseconds <= 60_000
  ) {
    ntpControl.mode = "offset";
    ntpControl.offsetMilliseconds = body.offsetMilliseconds;
  } else {
    writeError(
      response,
      400,
      "invalid_ntp_control",
      "Use reset, drop, or an integer offsetMilliseconds from -60000 through 60000.",
    );
    return;
  }

  console.log(JSON.stringify({
    event: "mock_ntp_control_changed",
    mode: ntpControl.mode,
    offsetMilliseconds: ntpControl.offsetMilliseconds,
  }));
  writeJson(response, 200, {
    mode: ntpControl.mode,
    offsetMilliseconds: ntpControl.offsetMilliseconds,
  });
}

if (process.argv.includes("--self-test")) {
  runSntpSelfTest();
  process.exit(0);
}

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "mock-upstream"}`);

  if (request.method === "GET" && url.pathname === "/healthz") {
    writeJson(response, 200, {
      status: "ok",
      service: "poolai-mock-upstream",
      sntp: { version: 4, port: ntpPort },
    });
    return;
  }

  if (url.pathname === "/test-control/ntp") {
    if (request.method !== "POST" || !isLoopbackTestControlRequest(request)) {
      writeError(response, 404, "not_found", "The mock route does not exist.");
      return;
    }

    try {
      await updateNtpControl(request, response);
    } catch (error) {
      const statusCode = Number.isInteger(error.statusCode) ? error.statusCode : 500;
      writeError(
        response,
        statusCode,
        statusCode === 413 ? "request_too_large" : "invalid_request",
        statusCode === 413 ? "The request body is too large." : "The request body is invalid.",
      );
    }
    return;
  }

  if (request.method === "GET" && url.pathname === "/v1/models") {
    writeJson(response, 200, {
      object: "list",
      data: [
        {
          id: "poolai-mock-model",
          object: "model",
          created: createdAt,
          owned_by: "poolai-mock",
        },
      ],
    });
    return;
  }

  if (request.method !== "POST" || !["/v1/responses", "/v1/chat/completions"].includes(url.pathname)) {
    writeError(response, 404, "not_found", "The mock route does not exist.");
    return;
  }

  const scenario = selectedScenario(request);
  if (scenario === null) {
    writeError(response, 400, "unknown_mock_scenario", "The requested mock scenario is not registered.");
    return;
  }

  try {
    const body = await readJson(request);
    const model = typeof body.model === "string" && body.model.length > 0
      ? body.model
      : "poolai-mock-model";

    console.log(JSON.stringify({
      event: "mock_request",
      method: request.method,
      path: url.pathname,
      scenario,
    }));

    if (maybeWriteHttpError(response, scenario)) {
      return;
    }

    if (url.pathname === "/v1/responses") {
      if (body.stream === true) {
        writeResponsesStream(response, model, scenario);
      } else {
        writeJson(response, 200, responseObject(model, scenario));
      }
      return;
    }

    if (body.stream === true) {
      writeChatStream(response, model, scenario);
    } else {
      writeJson(response, 200, chatObject(model, scenario));
    }
  } catch (error) {
    const statusCode = Number.isInteger(error.statusCode) ? error.statusCode : 500;
    writeError(
      response,
      statusCode,
      statusCode === 413 ? "request_too_large" : "invalid_request",
      statusCode === 413 ? "The request body is too large." : "The request body is invalid.",
    );
  }
});

const ntpServer = dgram.createSocket("udp4");
let httpListening = false;
let ntpListening = false;
let stopping = false;

ntpServer.on("message", (request, remote) => {
  if (ntpControl.mode === "drop") {
    return;
  }

  const receivedAt = Date.now();
  const response = createSntpResponse(
    request,
    receivedAt,
    Date.now(),
    ntpControl.offsetMilliseconds,
  );
  if (response === null) {
    return;
  }

  ntpServer.send(response, remote.port, remote.address, (error) => {
    if (error) {
      console.error(JSON.stringify({
        event: "mock_sntp_send_failed",
        code: error.code ?? "unknown",
      }));
    }
  });
});

ntpServer.on("error", (error) => {
  console.error(JSON.stringify({
    event: "mock_sntp_failed",
    code: error.code ?? "unknown",
  }));
  stop(1);
});

server.on("error", (error) => {
  console.error(JSON.stringify({
    event: "mock_http_failed",
    code: error.code ?? "unknown",
  }));
  stop(1);
});

function stop(exitCode = 0) {
  if (stopping) {
    return;
  }
  stopping = true;

  const closures = [];
  if (httpListening) {
    closures.push(new Promise((resolve) => {
      server.close((error) => {
        httpListening = false;
        if (error) {
          exitCode = 1;
        }
        resolve();
      });
    }));
  }
  if (ntpListening) {
    closures.push(new Promise((resolve) => {
      ntpServer.close(() => {
        ntpListening = false;
        resolve();
      });
    }));
  }

  Promise.all(closures).then(() => {
    process.exit(exitCode);
  });
}

ntpServer.bind(ntpPort, ntpHost, () => {
  ntpListening = true;
  server.listen(port, host, () => {
    httpListening = true;
    console.log(JSON.stringify({ event: "mock_started", host, port, ntpHost, ntpPort }));
  });
});

process.on("SIGINT", () => stop());
process.on("SIGTERM", () => stop());
