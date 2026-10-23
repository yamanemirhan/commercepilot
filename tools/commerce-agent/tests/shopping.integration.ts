import assert from "node:assert/strict";
import { test } from "node:test";
import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { randomUUID } from "node:crypto";
import type { Client, CallToolResult } from "@modelcontextprotocol/client";
import { ShopperSession } from "../src/shopper-session.js";
import { connectMcp } from "../src/mcp.js";
import { connectShopperMcp } from "../src/shopper-mcp.js";
import { localMemoryPool } from "../src/postgres-memory.js";

type Connection = Pick<Client, "callTool" | "close">;
type Cart = { items: { productId: string; quantity: number }[]; total: number };
const cartBody = (result: CallToolResult) => { assert.notEqual(result.isError, true); return result.structuredContent as unknown as Cart; };
async function denied(work: () => Promise<CallToolResult>) {
  let rejected = false;
  try { rejected = (await work()).isError === true; } catch { rejected = true; }
  assert.ok(rejected, "server must reject insufficient scope or foreign ownership");
}

// Opt-in local integration: one previously absent cart line and one cancelled synthetic order;
// remove exactly those fixtures in finally. No checkout/payment/stock transition is performed.
test("real MCP scopes, own-cart edits and foreign-order isolation", async () => {
  const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
  const alex = new ShopperSession(), sam = new ShopperSession();
  const pool = await localMemoryPool();
  const connections: Connection[] = [];
  const signal = AbortSignal.timeout(60_000);
  let productId: string | undefined, owner: string | undefined;
  const orderId = randomUUID();
  let fixtureCreated = false, cartChanged = false;
  try {
    await alex.login("alex@commercepilot.local", env.DEMO_ALEX_PASSWORD!);
    await sam.login("sam@commercepilot.local", env.DEMO_SAM_PASSWORD!);
    owner = await alex.owner();
    const publicClient = await connectMcp(signal); connections.push(publicClient);
    const read = await connectMcp(signal, await alex.issueMcpToken("cart:read")); connections.push(read);
    const write = await connectMcp(signal, await alex.issueMcpToken("cart:write")); connections.push(write);
    const orders = await connectMcp(signal, await alex.issueMcpToken("orders:read")); connections.push(orders);
    // Discovery enables SDK output-schema validation, matching the real agent's path.
    await orders.listTools();
    const samClient = await connectShopperMcp(sam, signal); connections.push(samClient);
    const before = cartBody(await read.callTool({ name: "get_cart", arguments: {} }));
    const otherBefore = cartBody(await samClient.callTool({ name: "get_cart", arguments: {} }));
    const search = await publicClient.callTool({ name: "search_products", arguments: { pageSize: 48, inStock: true } });
    const products = (search.structuredContent as { items: { id: string; stock: number }[] }).items;
    productId = products.find(product => product.stock >= 3 && ![...before.items, ...otherBefore.items].some(item => item.productId === product.id))?.id;
    assert.ok(productId, "Need an in-stock product absent from both demo carts.");
    await denied(() => publicClient.callTool({ name: "get_cart", arguments: {} }));
    await denied(() => read.callTool({ name: "add_to_cart", arguments: { productId, quantity: 1 } }));
    await denied(() => read.callTool({ name: "get_my_orders", arguments: {} }));
    await denied(() => orders.callTool({ name: "get_cart", arguments: {} }));
    assert.deepEqual(cartBody(await read.callTool({ name: "get_cart", arguments: {} })), before);
    cartChanged = true;
    const added = cartBody(await write.callTool({ name: "add_to_cart", arguments: { productId, quantity: 1 } }));
    assert.equal(added.items.find(item => item.productId === productId)?.quantity, 1);
    const updated = cartBody(await write.callTool({ name: "update_cart_item", arguments: { productId, quantity: 2 } }));
    assert.equal(updated.items.find(item => item.productId === productId)?.quantity, 2);
    assert.deepEqual(cartBody(await samClient.callTool({ name: "get_cart", arguments: {} })), otherBefore);

    const snapshot = JSON.stringify({ Kind: "Order", Items: [], Total: 0, Currency: "USD", PaymentMode: "demo",
      Destination: "Agent integration fixture", OrderId: null, OrderVersion: null });
    await pool.query(`INSERT INTO "Orders" ("Id", "UserId", "SnapshotJson", "Total", "Currency", "Status", "PaymentStatus",
      "CapturedAmount", "RefundedAmount", "StockReleased", "Version", "CreatedAt")
      VALUES ($1,$2,$3,0,'USD','Cancelled','Failed',0,0,true,1,now())`, [orderId, owner, snapshot]);
    fixtureCreated = true;
    const own = await orders.callTool({ name: "get_order_status", arguments: { orderId } });
    assert.notEqual(own.isError, true); assert.equal((own.structuredContent as { id: string }).id, orderId);
    assert.equal((own.structuredContent as { nextTransitionAt: string | null }).nextTransitionAt, null);
    await denied(() => samClient.callTool({ name: "get_order_status", arguments: { orderId } }));
    const listed = await samClient.callTool({ name: "get_my_orders", arguments: {} });
    assert.ok(!JSON.stringify(listed).includes(orderId));
    await denied(() => connectMcp(signal, "invalid-credential").then(async client => {
      connections.push(client); return client.callTool({ name: "search_products", arguments: { pageSize: 1 } });
    }));
    cartBody(await write.callTool({ name: "remove_from_cart", arguments: { productId } }));
    cartChanged = false;
    assert.deepEqual(cartBody(await read.callTool({ name: "get_cart", arguments: {} })), before);
  } finally {
    // Exact generated fixture keys only; preserve all pre-existing carts and orders.
    if (cartChanged && productId && owner) await pool.query('DELETE FROM "CartItems" WHERE "UserId" = $1 AND "ProductId" = $2', [owner, productId]);
    if (fixtureCreated) await pool.query('DELETE FROM "Orders" WHERE "Id" = $1 AND "UserId" = $2', [orderId, owner]);
    await Promise.allSettled(connections.map(client => client.close()));
    await alex.logout().catch(() => {}); await sam.logout().catch(() => {});
    await pool.end();
  }
});
