import assert from "node:assert/strict";
import { test } from "node:test";
import { CatalogTrace, validateTraceConfig } from "../src/tracing.js";

const secret = "PRIVATE-user@example.test-Bearer-secret-reasoning";
const config = validateTraceConfig({ LANGSMITH_API_KEY: "test-key-not-real" });
const startModel = { type: "node", name: "model", phase: "started" } as const;
const endModel = { type: "node", name: "model", phase: "completed", inputTokens: 100, outputTokens: 20 } as const;

function recorder() {
  let tick = Date.now();
  const trace = new CatalogTrace(() => tick += 10);
  trace.observe(startModel); trace.observe(endModel);
  trace.observe({ type: "node", name: "tools", phase: "started" });
  trace.observe({ type: "call", name: "search_products", arguments: { search: secret, authorization: secret } });
  trace.observe({ type: "result", name: "search_products", isError: false, data: { content: secret, reasoning: secret } });
  trace.observe({ type: "node", name: "tools", phase: "completed" });
  trace.observe(startModel); trace.observe(endModel);
  return trace;
}

test("the real SDK exports only allowlisted metadata, correct parent spans and token counts", async () => {
  const saved = process.env.LANGCHAIN_PRIVATE_TEST;
  process.env.LANGCHAIN_PRIVATE_TEST = secret;
  try {
    const trace = recorder(); trace.finish("completed");
    const requests: { url: string; init: RequestInit }[] = [];
    const result = await trace.upload(config, async (input, init) => {
      requests.push({ url: String(input), init: init! });
      return new Response("{}", { status: 200 });
    });
    assert.equal(result.ok, true);
    assert.equal(requests.length, 1);
    assert.equal(requests[0]!.url, "https://api.smith.langchain.com/runs/batch");
    assert.equal(requests[0]!.init.redirect, "error");
    const body = await new Response(requests[0]!.init.body).text();
    assert.ok(!body.includes(secret)); assert.ok(!body.includes(config.apiKey));
    assert.ok(!body.includes("runtime")); assert.ok(!body.includes("LANGCHAIN_PRIVATE_TEST"));
    const runs = JSON.parse(body).post;
    assert.deepEqual(runs.map((r: { name: string }) => r.name), ["catalog_agent", "model", "tools", "search_products", "model"]);
    const [root, model, tools, call, last] = runs;
    assert.equal(root.id, result.traceId);
    for (const run of runs) {
      assert.deepEqual(run.inputs, {});
      assert.equal(run.trace_id, root.id);
      assert.equal(run.session_name, "commercepilot-learning");
      assert.equal(run.outputs.status, "completed");
      assert.ok(run.end_time >= run.start_time);
    }
    assert.equal(model.parent_run_id, root.id);
    assert.equal(tools.parent_run_id, root.id);
    assert.equal(call.parent_run_id, tools.id);
    assert.equal(last.parent_run_id, root.id);
    assert.ok(call.dotted_order.startsWith(tools.dotted_order + "."));
    assert.equal(model.run_type, "llm"); assert.equal(call.run_type, "tool");
    assert.deepEqual(model.outputs.usage_metadata, { input_tokens: 100, output_tokens: 20, total_tokens: 120 });
    assert.equal(model.extra.metadata.ls_provider, "ollama");
  } finally {
    if (saved === undefined) delete process.env.LANGCHAIN_PRIVATE_TEST;
    else process.env.LANGCHAIN_PRIVATE_TEST = saved;
  }
});

test("failed and cancelled executions close pending spans without error payloads", async () => {
  for (const status of ["failed", "cancelled"] as const) {
    const trace = new CatalogTrace(); trace.observe(startModel); trace.finish(status);
    trace.observe({ type: "call", name: secret, arguments: { secret } });
    const result = await trace.upload(config, async (_input, init) => {
      const body = await new Response(init?.body).text(); assert.ok(!body.includes(secret));
      const runs = JSON.parse(body).post;
      assert.equal(runs.length, 2);
      assert.ok(runs.every((r: { end_time: unknown; error: unknown; outputs: { status: string } }) => r.end_time && r.error && r.outputs.status === status));
      return new Response("{}", { status: 200 });
    });
    assert.equal(result.ok, true);
  }
});

test("tool failure stays visible when the model recovers and answers", async () => {
  const trace = new CatalogTrace();
  trace.observe({ type: "node", name: "tools", phase: "started" });
  trace.observe({ type: "call", name: "get_product", arguments: { secret } });
  trace.observe({ type: "result", name: "get_product", isError: true, data: { secret } });
  trace.observe({ type: "node", name: "tools", phase: "completed" });
  trace.finish("completed");
  const result = await trace.upload(config, async (_input, init) => {
    const runs = JSON.parse(await new Response(init?.body).text()).post;
    assert.equal(runs[0].outputs.status, "completed");
    assert.equal(runs[2].outputs.status, "failed");
    assert.ok(runs[2].error);
    return new Response("{}", { status: 200 });
  });
  assert.equal(result.ok, true);
});

for (const status of [401, 403, 429, 500, 302]) {
  test(`export failure ${status} is reported safely without retries or changing the catalog result`, async () => {
    const trace = recorder(); trace.finish("completed"); let calls = 0;
    const result = await trace.upload(config, async () => { calls++; return new Response(secret, { status }); });
    assert.deepEqual(result, { ok: false }); assert.equal(calls, 1);
  });
}

test("a stalled exporter is aborted within its bounded deadline", async () => {
  const trace = recorder(); trace.finish("completed");
  const started = Date.now();
  const result = await trace.upload(config, async (_input, init) => new Promise((_resolve, reject) => {
    init!.signal!.addEventListener("abort", () => reject(init!.signal!.reason), { once: true });
    // Keep the mock alive like a pending socket; the abort listener clears it.
    const timer = setTimeout(() => reject(new Error("Deadline did not abort transport")), 8000);
    init!.signal!.addEventListener("abort", () => clearTimeout(timer), { once: true });
  }));
  assert.deepEqual(result, { ok: false }); assert.ok(Date.now() - started < 7000);
});

test("unknown names and invalid token counters are excluded and memory stays bounded", async () => {
  const trace = new CatalogTrace();
  trace.observe({ type: "call", name: secret, arguments: { secret } });
  for (let n = 0; n < 50; n++) {
    trace.observe(startModel);
    trace.observe({ ...endModel, inputTokens: -1, outputTokens: Infinity });
  }
  trace.finish("completed");
  const result = await trace.upload(config, async (_input, init) => {
    const body = await new Response(init?.body).text(); assert.ok(!body.includes(secret));
    const runs = JSON.parse(body).post;
    assert.equal(runs.length, 20);
    assert.ok(runs.every((r: { outputs: { usage_metadata?: unknown } }) => !r.outputs.usage_metadata));
    return new Response("{}", { status: 200 });
  });
  assert.equal(result.ok, true);
});

test("configuration rejects missing keys and credential-bearing or foreign endpoints without echoing values", () => {
  assert.throws(() => validateTraceConfig({}), /Set LANGSMITH_API_KEY/);
  for (const endpoint of ["http://api.smith.langchain.com", `https://${secret}@api.smith.langchain.com`, "https://example.test", "https://api.smith.langchain.com?key=secret"]) {
    assert.throws(() => validateTraceConfig({ LANGSMITH_API_KEY: secret, LANGSMITH_ENDPOINT: endpoint }), /official US or EU/);
  }
  assert.equal(validateTraceConfig({ LANGSMITH_API_KEY: "test", LANGSMITH_ENDPOINT: "https://eu.api.smith.langchain.com" }).endpoint, "https://eu.api.smith.langchain.com");
  assert.throws(() => validateTraceConfig({ LANGSMITH_API_KEY: "test", LANGSMITH_PROJECT: secret }), /short project name/);
});
