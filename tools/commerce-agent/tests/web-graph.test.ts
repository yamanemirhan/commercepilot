import { test } from "node:test";
import assert from "node:assert/strict";
import { AIMessageChunk } from "@langchain/core/messages";
import { runWebGraph, toMessages } from "../src/web-graph.js";
import type { StreamModel, WebFrame } from "../src/web-graph.js";
import { createWebServer } from "../src/web-http.js";

const signal = AbortSignal.timeout(15_000);
const input = { messages: [{ role: "user", content: "Hello" }], tools: [{ name: "get_cart", description: "Read the bag",
  parameters: { type: "object", properties: {}, additionalProperties: false } }] };
const last = (extra = {}) => new AIMessageChunk({ content: "", response_metadata: { done: true, done_reason: "stop" },
  usage_metadata: { input_tokens: 100, output_tokens: 20, total_tokens: 120 }, ...extra });
const fake = (...chunks: AIMessageChunk[]): StreamModel => async () => (async function* () { yield* chunks; })();

test("streams public text before completion, routes answers and excludes reasoning", async () => {
  const frames: WebFrame[] = [];
  await runWebGraph(input, fake(new AIMessageChunk({ content: "Hello!", additional_kwargs: { reasoning_content: "private" } }), last()),
    signal, async frame => { frames.push(frame); });
  assert.equal(frames[0]?.type, "delta");
  assert.equal(frames.at(-1)?.type, "result");
  assert.match(JSON.stringify(frames), /answer/);
  assert.doesNotMatch(JSON.stringify(frames), /private|reasoning_content/);
});

test("valid proposals are handed to the API without executing commerce", async () => {
  const frames: WebFrame[] = [];
  await runWebGraph(input, fake(last({ tool_calls: [{ id: "call1", name: "get_cart", args: {} }] })), signal,
    async frame => { frames.push(frame); });
  assert.equal(frames.length, 1);
  assert.equal(frames[0]?.type === "result" && frames[0].route, "tools");
});

test("all proposed calls are validated before any handoff", async () => {
  for (const call of [{ id: "bad", name: "confirm_action", args: {} }, { id: "bad", name: "get_cart", args: { userId: "other" } }]) {
    const frames: WebFrame[] = [];
    await assert.rejects(runWebGraph(input, fake(last({ tool_calls: [{ id: "ok", name: "get_cart", args: {} }, call] })),
      signal, async frame => { frames.push(frame); }));
    assert.equal(frames.length, 0);
  }
});

test("truncation, missing usage and token overflow never yield a final result", async () => {
  for (const chunk of [last({ response_metadata: { done: true, done_reason: "length" } }),
    new AIMessageChunk("unfinished"), last({ usage_metadata: { input_tokens: 6000, output_tokens: 20, total_tokens: 6020 } })]) {
    const frames: WebFrame[] = [];
    await assert.rejects(runWebGraph(input, fake(new AIMessageChunk("Text"), chunk), signal, async frame => { frames.push(frame); }));
    assert.ok(frames.every(f => f.type !== "result"));
  }
});

test("history reconstructs same-name call IDs in order and rejects orphan results", () => {
  const history = toMessages([{ role: "assistant", content: "", tool_calls: [
    { function: { name: "get_cart", arguments: {} } }, { function: { name: "get_cart", arguments: {} } }] },
  { role: "tool", content: "first", tool_name: "get_cart" }, { role: "tool", content: "second", tool_name: "get_cart" }]);
  assert.equal(history.length, 3);
  assert.notEqual(history[1]!.toDict().data.tool_call_id, history[2]!.toDict().data.tool_call_id);
  assert.throws(() => toMessages([{ role: "tool", content: "orphan", tool_name: "get_cart" }]));
  assert.throws(() => toMessages([{ role: "assistant", content: "", tool_calls: [{ function: { name: "get_cart", arguments: {} } }] }]));
});

test("malformed input and confirmation tools are rejected before inference", async () => {
  let calls = 0;
  const model: StreamModel = async () => { calls++; return fake(last())([], [], signal); };
  await assert.rejects(runWebGraph({ ...input, tools: [{ ...input.tools[0], name: "confirm_action" }] }, model, signal, async () => {}));
  await assert.rejects(runWebGraph({ ...input, messages: [{ role: "user", content: "x".repeat(30_000) }] }, model, signal, async () => {}));
  assert.equal(calls, 0);
});

test("HTTP boundary requires the internal key, refuses browser origins, bounds input and serializes inference", async () => {
  const key = "a".repeat(64);
  let finish!: () => void;
  const held = new Promise<void>(resolve => { finish = resolve; });
  const server = createWebServer(key, async () => (async function* () {
    yield new AIMessageChunk("Hello"); await held; yield last();
  })());
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  const url = `http://127.0.0.1:${address.port}`;
  const headers = { Authorization: `Bearer ${key}`, "Content-Type": "application/json" };
  try {
    assert.equal((await fetch(`${url}/health`)).status, 401);
    assert.equal((await fetch(`${url}/health`, { headers: { ...headers, Origin: "http://localhost:3000" } })).status, 401);
    assert.equal((await fetch(`${url}/health`, { headers })).status, 200);
    assert.equal((await fetch(`${url}/round`, { method: "POST", headers, body: "x".repeat(25_000) })).status, 413);
    const active = await fetch(`${url}/round`, { method: "POST", headers, body: JSON.stringify(input) });
    assert.equal(active.status, 200);
    assert.equal((await fetch(`${url}/round`, { method: "POST", headers, body: JSON.stringify(input) })).status, 503);
    finish();
    const result = await active.text();
    assert.match(result, /"type":"delta"/); assert.match(result, /"route":"answer"/);
  } finally { finish(); server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve())); }
});
