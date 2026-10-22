import assert from "node:assert/strict";
import { test } from "node:test";
import { AIMessage, ToolMessage } from "@langchain/core/messages";
import type { BaseMessage, ToolCall } from "@langchain/core/messages";
import type { Client, Tool, CallToolResult } from "@modelcontextprotocol/client";
import { runCatalogAgent, type AgentEvent } from "../src/catalog-agent.js";

const productId = "10000000-0000-0000-0000-000000000020";
const catalog: Tool[] = [
  { name: "search_products", inputSchema: { type: "object", properties: { search: { type: "string" }, pageSize: { type: "integer" } } } },
  { name: "get_product", inputSchema: { type: "object", properties: { productId: { type: "string", format: "uuid" } }, required: ["productId"] } },
];
const search: ToolCall = { id: "search-1", name: "search_products", args: { search: "desk", pageSize: 2 } };

function reply(calls: ToolCall[] = [], content = ""): AIMessage {
  return new AIMessage({ content, tool_calls: calls, additional_kwargs: { reasoning_content: "not for history or output" },
    response_metadata: { done: true, done_reason: "stop" },
    usage_metadata: { input_tokens: 100, output_tokens: 20, total_tokens: 120 } });
}

function fixture(output?: CallToolResult) {
  const executed: string[] = [];
  const client: Pick<Client, "listTools" | "callTool"> = {
    listTools: async () => ({ tools: [...catalog, { name: "add_to_cart", inputSchema: { type: "object" } }] }),
    callTool: async request => {
      executed.push(request.name);
      return output ?? { content: [], structuredContent: { items: [{ id: productId, name: "Desk light", price: 69 }] } };
    },
  };
  return { client, executed };
}

test("binds only public tools and feeds paired, sanitized results back to the model", async () => {
  const { client, executed } = fixture();
  const frames: BaseMessage[][] = [];
  const replies = [reply([search]), reply([{ id: "detail-1", name: "get_product", args: { productId } }]), reply([], "Desk light costs $69.")];
  const answer = await runCatalogAgent("Find a desk light", client, tools => {
    assert.equal(tools.length, 2);
    assert.ok(!JSON.stringify(tools).includes("add_to_cart"));
    return { invoke: async messages => { frames.push([...messages]); return replies.shift()!; } };
  }, AbortSignal.timeout(5000), () => {});
  assert.equal(answer, "Desk light costs $69.");
  assert.deepEqual(executed, ["search_products", "get_product"]);
  const second = frames[1]!;
  assert.ok(second[3] instanceof ToolMessage);
  assert.equal(second[3].tool_call_id, search.id);
  assert.ok(second[3].content.toString().includes(productId));
  assert.deepEqual(second[2]!.additional_kwargs, {});
});

for (const [name, call] of [
  ["private tool", { id: "bad", name: "add_to_cart", args: { productId } }],
  ["extra argument", { ...search, args: { pageSize: 2, userId: "someone" } }],
  ["wrong argument type", { ...search, args: { pageSize: "two" } }],
  ["invented product ID", { id: "bad", name: "get_product", args: { productId } }],
  ["oversized search", { ...search, args: { pageSize: 48 } }],
] as const) {
  test(`rejects ${name} before executing any tool in a batch`, async () => {
    const { client, executed } = fixture();
    await assert.rejects(runCatalogAgent("Find products", client, () => ({ invoke: async () => reply([search, { ...call, id: "bad" }]) }),
      AbortSignal.timeout(5000), () => {}));
    assert.deepEqual(executed, []);
  });
}

test("rejects truncated model output before executing its proposed call", async () => {
  const { client, executed } = fixture();
  const incomplete = reply([search]);
  incomplete.response_metadata.done_reason = "length";
  await assert.rejects(runCatalogAgent("Find products", client, () => ({ invoke: async () => incomplete }),
    AbortSignal.timeout(5000), () => {}), /incomplete/);
  assert.deepEqual(executed, []);
});

test("passes tool errors to the model as errors, without claiming success", async () => {
  const { client } = fixture({ content: [{ type: "text", text: "Catalog unavailable" }], isError: true });
  let round = 0;
  const answer = await runCatalogAgent("Find products", client, () => ({ invoke: async messages => {
    if (round++ === 0) return reply([search]);
    const result = messages.at(-1) as ToolMessage;
    assert.equal(result.status, "error");
    assert.match(result.content.toString(), /Catalog unavailable/);
    return reply([], "I could not retrieve the catalog.");
  } }), AbortSignal.timeout(5000), () => {});
  assert.match(answer, /could not retrieve/);
});

test("rejects an automatic repeat of the same failed call", async () => {
  const { client, executed } = fixture({ content: [{ type: "text", text: "Unavailable" }], isError: true });
  await assert.rejects(runCatalogAgent("Find products", client, () => ({ invoke: async () => reply([search]) }),
    AbortSignal.timeout(5000), () => {}), /repeat a failed/);
  assert.equal(executed.length, 1);
});

test("bounds a model that keeps asking for tools instead of answering", async () => {
  const { client, executed } = fixture();
  await assert.rejects(runCatalogAgent("Find products", client, () => ({ invoke: async () => reply([search]) }),
    AbortSignal.timeout(5000), () => {}), /budget exceeded/);
  assert.equal(executed.length, 3);
});

test("cancellation after inference prevents tool execution", async () => {
  const { client, executed } = fixture();
  const cancellation = new AbortController();
  await assert.rejects(runCatalogAgent("Find products", client, () => ({ invoke: async () => {
    cancellation.abort(); return reply([search]);
  } }), cancellation.signal, () => {}));
  assert.deepEqual(executed, []);
});

test("graph routes model to tools and back, then ends; a new run has no prior product state", async () => {
  const { client } = fixture();
  const events: AgentEvent[] = [];
  const replies = [reply([search]), reply([], "Here is a desk light.")];
  await runCatalogAgent("Find a light", client, () => ({ invoke: async () => replies.shift()! }),
    AbortSignal.timeout(5000), event => events.push(event));
  assert.deepEqual(events.flatMap(event => event.type === "node" && event.phase === "started" ? [event.name] : []),
    ["model", "tools", "model"]);
  await assert.rejects(runCatalogAgent("Tell me about it", client, () => ({ invoke: async () =>
    reply([{ id: "detail", name: "get_product", args: { productId } }]) }), AbortSignal.timeout(5000), () => {}), /not supplied or retrieved/);
});

test("a direct answer ends the graph without entering its tools node", async () => {
  const { client, executed } = fixture();
  const events: AgentEvent[] = [];
  assert.equal(await runCatalogAgent("Hello", client, () => ({ invoke: async () => reply([], "Hello!") }),
    AbortSignal.timeout(5000), event => events.push(event)), "Hello!");
  assert.deepEqual(executed, []);
  assert.ok(!events.some(event => event.type === "node" && event.name === "tools"));
});
