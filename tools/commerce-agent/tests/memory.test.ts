import assert from "node:assert/strict";
import { test } from "node:test";
import { AIMessage, HumanMessage, ToolMessage } from "@langchain/core/messages";
import { RamConversations, Conversations, memoryLimits } from "../src/conversations.js";
import { retainCompleteTurns, referencedProductIds } from "../src/conversation-context.js";
import { fixture, reply, firstId, secondId } from "./memory-fixture.js";

test("RAM memory preserves paired results and reference IDs across turns, resets per-turn graph state", async () => {
  const store = new RamConversations();
  const service = new Conversations(store, async () => "alex");
  const row = await service.create();
  const { turn, called } = fixture();
  await service.run(row.id, turn("Show two speakers", [reply("", [{ id: "s1", name: "search_products", args: { pageSize: 2 } }]),
    reply("1. First speaker. 2. Second speaker.")]));
  let inspected = false;
  const answer = await service.run(row.id, turn("Tell me about the second one", [reply("", [
    { id: "g1", name: "get_product", args: { productId: secondId } }]), reply("The second speaker costs $69.")], messages => {
    const serialized = JSON.stringify(messages);
    assert.match(serialized, /First speaker/);
    assert.match(serialized, /Second speaker/);
    assert.ok(!serialized.includes("PRIVATE_REASONING_MARKER"));
    assert.ok(!serialized.includes("PROVIDER_METADATA_MARKER"));
    assert.equal(messages.filter(m => m.type === "system").length, 1);
    inspected = true;
  }));
  assert.ok(inspected);
  assert.match(answer, /69/);
  assert.equal(called[1]?.arguments?.productId, secondId);
  assert.equal((await service.list())[0]?.turns, 2);
  await store.close();
});

test("another account cannot list, read through a turn, or delete a conversation; same-owner new thread is empty", async () => {
  const store = new RamConversations();
  let owner = "alex";
  const service = new Conversations(store, async () => owner);
  const row = await service.create();
  owner = "sam";
  assert.deepEqual(await service.list(), []);
  await assert.rejects(service.run(row.id, async () => assert.fail("must not reach graph")), /not found/);
  await assert.rejects(service.remove(row.id), /not found/);
  owner = "alex";
  const { turn } = fixture();
  await service.run(row.id, turn("My catalog preference is Audio", [reply("I can help with Audio.")]));
  const fresh = await service.create();
  await service.run(fresh.id, turn("Hello", [reply("Hello!")], messages => assert.ok(!JSON.stringify(messages).includes("My catalog preference"))));
  await service.remove(row.id);
  await assert.rejects(service.run(row.id, async () => "unused"), /not found/);
  await store.close();
  assert.deepEqual(await new RamConversations().list("alex"), []);
});

test("auth failure prevents every storage operation", async () => {
  const service = new Conversations(new RamConversations(), async () => { throw new Error("Session expired"); });
  await assert.rejects(service.create(), /Session expired/);
  await assert.rejects(service.list(), /Session expired/);
  await assert.rejects(service.run(firstId, async () => assert.fail()), /Session expired/);
  await assert.rejects(service.remove(firstId), /Session expired/);
});

test("concurrent turns and deletion are rejected; failure blocks replay", async () => {
  const store = new RamConversations();
  const row = await store.create("alex");
  let release!: () => void;
  const running = store.run("alex", row.id, () => new Promise<void>(resolve => { release = resolve; }));
  await assert.rejects(store.run("alex", row.id, async () => assert.fail()), /busy/);
  await assert.rejects(store.remove("alex", row.id), /busy/);
  release(); await running;
  await assert.rejects(store.run("alex", row.id, async () => { throw new Error("Inference failed"); }));
  await assert.rejects(store.run("alex", row.id, async () => assert.fail()), /unfinished\/failed/);
  await store.remove("alex", row.id);
  await store.close();
});

test("hard conversation, turn and expiry limits apply", async () => {
  let now = Date.now();
  const store = new RamConversations(() => now);
  const row = await store.create("alex");
  for (let i = 0; i < memoryLimits.turns; i++) await store.run("alex", row.id, async () => "ok");
  await assert.rejects(store.run("alex", row.id, async () => assert.fail()), /turn limit/);
  for (let i = 1; i < memoryLimits.conversations; i++) await store.create("alex");
  await assert.rejects(store.create("sam"), /limit/);
  now += memoryLimits.lifetimeMs;
  await assert.rejects(store.run("alex", row.id, async () => assert.fail()), /expired/);
  assert.deepEqual(await store.list("alex"), []);
  await store.close();
});

test("context trimming keeps whole turns and drops product authority with its source", () => {
  const old = [new HumanMessage("Search"), new AIMessage({ content: "", tool_calls: [
    { id: "search", name: "search_products", args: {} }] }),
    new ToolMessage({ content: JSON.stringify({ items: [{ id: firstId }] }), name: "search_products", tool_call_id: "search" }),
    new AIMessage("Here is a product.")];
  const newer = Array.from({ length: 4 }, (_, i) => [new HumanMessage(`Hello ${i}`), new AIMessage("Hello!")]).flat();
  const retained = retainCompleteTurns([...old, ...newer]);
  assert.equal(retained.length, 8);
  assert.deepEqual(referencedProductIds(retained), []);
  assert.deepEqual(referencedProductIds(old), [firstId]);
  assert.throws(() => retainCompleteTurns(old.slice(0, 2)), /unfinished/);
  assert.throws(() => retainCompleteTurns([new HumanMessage("Hi"), new AIMessage("x".repeat(8001))]), /budget/);
  const longTurns = [1, 2, 3, 4].flatMap(i => [new HumanMessage(`Hi ${i}`), new AIMessage("x".repeat(2300))]);
  const bounded = retainCompleteTurns(longTurns);
  assert.ok(Buffer.byteLength(JSON.stringify(bounded)) <= 8000);
  assert.equal(bounded[0]?.type, "human");
  assert.equal(bounded.at(-1)?.type, "ai");
});

test("persisted graph context stays bounded across repeated turns; dropped IDs cannot be invoked", async () => {
  const store = new RamConversations();
  const row = await store.create("alex");
  const { turn, called } = fixture();
  await store.run("alex", row.id, turn("Search", [reply("", [{ id: "search", name: "search_products", args: { pageSize: 2 } }]), reply("Two speakers.")]));
  for (let i = 0; i < 5; i++) await store.run("alex", row.id, turn(`Hello ${i}`, [reply("Hello!")], messages => {
    assert.ok(messages.filter(m => m.type === "human").length <= 5);
  }));
  await assert.rejects(store.run("alex", row.id, turn("Tell me about it", [reply("", [
    { id: "dropped", name: "get_product", args: { productId: firstId } }])])), /not supplied or retrieved/);
  assert.equal(called.length, 1);
  await store.close();
});
