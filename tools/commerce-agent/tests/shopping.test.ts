import assert from "node:assert/strict";
import { test } from "node:test";
import { MemorySaver } from "@langchain/langgraph";
import type { Client, Tool } from "@modelcontextprotocol/client";
import type { AIMessage, BaseMessage } from "@langchain/core/messages";
import { runCatalogAgent } from "../src/catalog-agent.js";
import { permittedNames, shoppingResult } from "../src/shopping-tools.js";
import { connectShopperMcp } from "../src/shopper-mcp.js";
import { reply, firstId } from "./memory-fixture.js";

const orderId = "20000000-0000-0000-0000-000000000001";
const cart = { items: [{ productId: firstId, name: "Test product", quantity: 1, unitPrice: 10, availableStock: 5, lineTotal: 10 }],
  itemCount: 1, total: 10, currency: "USD", authorization: "TRANSPORT_SECRET" };
const order = { id: orderId, number: "CP-TEST", status: "Delivered", paymentStatus: "Captured", total: 10,
  currency: "USD", createdAt: "2026-10-23T10:00:00Z" };
function fixture() {
  const executed: string[] = [];
  const names = permittedNames({ cartWrite: true });
  const definitions: Tool[] = names.map(name => ({ name, inputSchema: { type: "object",
    properties: name === "search_products" ? { pageSize: { type: "integer" } } : name === "get_order_status"
      ? { orderId: { type: "string" } } : ["get_product", "add_to_cart", "update_cart_item", "remove_from_cart"].includes(name)
        ? { productId: { type: "string" }, ...(name.includes("item") || name === "add_to_cart" ? { quantity: { type: "integer" } } : {}) } : {},
    required: name === "get_order_status" ? ["orderId"] : name === "add_to_cart" || name === "update_cart_item" ? ["productId", "quantity"] : [],
  } } as Tool));
  const client: Pick<Client, "listTools" | "callTool"> = {
    listTools: async () => ({ tools: definitions }),
    callTool: async request => {
      executed.push(request.name);
      const data = request.name === "get_my_orders" ? { orders: [order], limit: 100 } : request.name === "get_order_status"
        ? { ...order, capturedAmount: 10, refundedAmount: 0, canCancel: false, canRefund: true,
          destination: "PRIVATE_ADDRESS", items: [], timeline: [], cookie: "COOKIE_SECRET" } : cart;
      return { content: [], structuredContent: data };
    },
  };
  const run = (question: string, replies: AIMessage[], write = false, inspect?: (messages: BaseMessage[]) => void, saver?: MemorySaver) =>
    runCatalogAgent(question, client, tools => {
      assert.equal(tools.length, write ? 8 : 5);
      return { invoke: async messages => { inspect?.(messages); return replies.shift()!; } };
    }, AbortSignal.timeout(5000), () => {}, saver ? { checkpointer: saver, threadId: "shopping-test" } : undefined, { cartWrite: write });
  return { run, executed, client, definitions };
}

test("read-only turns cannot mutate even if MCP advertises write tools", async () => {
  const { run, executed } = fixture();
  await assert.rejects(run(`Add ${firstId}`, [reply("", [{ id: "w", name: "add_to_cart", args: { productId: firstId, quantity: 1 } }])]), /unavailable/);
  assert.deepEqual(executed, []);
});
for (const [label, args] of [
  ["owner injection", { productId: firstId, quantity: 1, userId: "sam" }],
  ["invalid quantity", { productId: firstId, quantity: 0 }],
  ["oversized quantity", { productId: firstId, quantity: 100 }],
  ["invented product", { productId: orderId, quantity: 1 }],
] as const) test(`rejects ${label} before any mutation`, async () => {
  const { run, executed } = fixture();
  await assert.rejects(run(`Add ${firstId}`, [reply("", [{ id: "w", name: "add_to_cart", args }])], true));
  assert.deepEqual(executed, ["get_cart"]);
});

test("updates need a current cart read; a second mutation is never executed", async () => {
  const { run, executed } = fixture();
  const change = { id: "w", name: "update_cart_item", args: { productId: firstId, quantity: 2 } };
  const empty = fixture();
  empty.client.callTool = async () => ({ content: [], structuredContent: { ...cart, items: [] } });
  await assert.rejects(empty.run(`Set ${firstId} to two`, [reply("", [change])], true), /current cart/);
  await assert.rejects(run("Set the first cart item to two", [reply("", [change]), reply("", [{ ...change, id: "w-again" }])], true), /one cart change/);
  assert.deepEqual(executed, ["get_cart", "update_cart_item"]);
});

test("cart-edit preloads current data before inference and only performs one successful edit", async () => {
  const { run, executed } = fixture();
  const answer = await run("Remove the first item", [reply("", [{ id: "remove", name: "remove_from_cart", args: { productId: firstId } }]),
    reply("The item was removed.")], true, messages => {
    assert.ok(messages.some(message => message.type === "tool" && message.name === "get_cart"));
    assert.ok(!JSON.stringify(messages).includes("TRANSPORT_SECRET"));
  });
  assert.match(answer, /removed/);
  assert.deepEqual(executed, ["get_cart", "remove_from_cart"]);
});

test("order IDs come from supplied/retrieved data and private fields never enter messages/checkpoints", async () => {
  const { run, executed } = fixture();
  const saver = new MemorySaver();
  await run("Show my latest order details", [reply("", [{ id: "list", name: "get_my_orders", args: {} }]),
    reply("", [{ id: "detail", name: "get_order_status", args: { orderId } }]), reply("Your order is delivered.")], false,
    messages => assert.ok(!/PRIVATE_ADDRESS|COOKIE_SECRET|TRANSPORT_SECRET/.test(JSON.stringify(messages))), saver);
  assert.deepEqual(executed, ["get_my_orders", "get_order_status"]);
  const saved = await saver.getTuple({ configurable: { thread_id: "shopping-test" } });
  assert.ok(!/PRIVATE_ADDRESS|COOKIE_SECRET|TRANSPORT_SECRET/.test(JSON.stringify(saved)));
  await assert.rejects(fixture().run("Show an order", [reply("", [{ id: "bad", name: "get_order_status", args: { orderId } }])]), /not supplied/);
});

test("large shopping lists are capped explicitly without changing totals", () => {
  const result = shoppingResult("get_cart", { ...cart, items: Array.from({ length: 12 }, () => cart.items[0]), itemCount: 12, total: 120 }) as typeof cart & { hasMoreItems: boolean };
  assert.equal(result.items.length, 10); assert.equal(result.total, 120); assert.equal(result.hasMoreItems, true);
  assert.equal("authorization" in result, false);
  const orders = shoppingResult("get_my_orders", { orders: Array.from({ length: 9 }, () => order), limit: 100 }) as { orders: unknown[]; hasMoreOrders: boolean };
  assert.equal(orders.orders.length, 5); assert.equal(orders.hasMoreOrders, true);
});

test("scoped connections route requests correctly, deny unavailable tools and do not retry uncertain cart writes", async () => {
  const { definitions } = fixture();
  const grants: string[] = [], sent: string[] = [], closed: string[] = [];
  let owner = "alex";
  const connection = await connectShopperMcp({ owner: async () => owner, issueMcpToken: async grant => { grants.push(grant); return grant; } },
    AbortSignal.timeout(5000), true, async (_, token) => ({
      listTools: async () => ({ tools: definitions }),
      callTool: async request => { sent.push(`${token}:${request.name}`); if (request.name === "add_to_cart") throw new Error("TRANSPORT_SECRET"); return { content: [] }; },
      close: async () => { closed.push(token ?? "public"); },
    }));
  try {
    assert.deepEqual(grants, ["cart:write", "orders:read"]);
    await connection.callTool({ name: "get_cart", arguments: {} });
    await connection.callTool({ name: "get_my_orders", arguments: {} });
    await assert.rejects(connection.callTool({ name: "prepare_order", arguments: {} }), /not authorized/);
    await assert.rejects(connection.callTool({ name: "add_to_cart", arguments: { productId: firstId, quantity: 1 } }), /no automatic retry/);
    await assert.rejects(connection.callTool({ name: "add_to_cart", arguments: { productId: firstId, quantity: 1 } }), /one cart change/);
    owner = "sam";
    await assert.rejects(connection.callTool({ name: "get_cart", arguments: {} }), /Session changed/);
    assert.deepEqual(sent, ["cart:write:get_cart", "orders:read:get_my_orders", "cart:write:add_to_cart"]);
  } finally { await connection.close(); }
  assert.equal(closed.length, 3);
});

test("failed authorization closes already-open clients and never falls back to anonymous", async () => {
  const { definitions } = fixture();
  let closed = 0, connected = 0;
  await assert.rejects(connectShopperMcp({ owner: async () => "alex", issueMcpToken: async () => { throw new Error("SECRET"); } },
    AbortSignal.timeout(5000), false, async () => {
      connected++; return { listTools: async () => ({ tools: definitions }), callTool: async () => ({ content: [] }), close: async () => { closed++; } };
    }), /no anonymous fallback/);
  assert.equal(connected, 1); assert.equal(closed, 1);
});
