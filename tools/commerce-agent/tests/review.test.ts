import assert from "node:assert/strict";
import { test } from "node:test";
import { MemorySaver } from "@langchain/langgraph";
import { createReviewFlow, decideReview } from "../src/review-graph.js";
import { formatReview, reviewThread, sameReview } from "../src/review-contract.js";
import type { Review } from "../src/review-contract.js";
import { Conversations, RamConversations } from "../src/conversations.js";
import { ShopperSession } from "../src/shopper-session.js";
import { reviewFixture } from "./review-fixture.js";

function harness() {
  let current = reviewFixture(); let prepared = 0, decided = 0, reserved = false;
  const memory = { checkpointer: new MemorySaver(), threadId: "11111111-1111-4111-8111-111111111111" };
  const signal = AbortSignal.timeout(5000);
  const gateway = {
    prepare: async () => { prepared++; return structuredClone(current); },
    read: async () => structuredClone(current),
    decide: async (_: Review, decision: "approve" | "reject") => {
      decided++; current.status = decision === "approve" ? "Completed" : "Rejected";
      return structuredClone(current);
    },
  };
  const flow = () => createReviewFlow(memory, gateway, signal);
  const claim = async () => { if (reserved) return false; reserved = true; return true; };
  return { memory, signal, gateway, flow, claim, current: () => current,
    set: (value: Review) => { current = value; }, counts: () => ({ prepared, decided }) };
}

test("interrupt persists exact terms; graph-only resume cannot execute or repeat preparation", async () => {
  const h = harness();
  const shown = await h.flow().start({ kind: "Checkout", paymentMode: "success" });
  assert.deepEqual(shown, h.current()); assert.equal((await h.flow().inspect()).waiting, true);
  await h.flow().sync(); // No human endpoint call: still pending, interrupted again.
  assert.equal((await h.flow().inspect()).waiting, true);
  assert.deepEqual(h.counts(), { prepared: 1, decided: 0 });
  await assert.rejects(h.flow().start({ kind: "Checkout", paymentMode: "success" }), /already pending/);
  const saved = await h.memory.checkpointer.getTuple({ configurable: { thread_id: reviewThread(h.memory.threadId) } });
  assert.ok(saved?.checkpoint);
});

for (const decision of ["approve", "reject"] as const) test(`explicit ${decision} is outside the graph; resume verifies API outcome`, async () => {
  const h = harness(); const flow = h.flow();
  const shown = await flow.start({ kind: "Checkout", paymentMode: "success" });
  const result = await decideReview(flow, shown, shown.id, decision, h.gateway, h.claim, h.signal);
  assert.equal(result.review?.status, decision === "approve" ? "Completed" : "Rejected");
  assert.equal((await flow.inspect()).waiting, false);
  assert.deepEqual(h.counts(), { prepared: 1, decided: 1 });
  await assert.rejects(decideReview(flow, shown, shown.id, decision, h.gateway, h.claim, h.signal), /displayed/);
});

test("tampered/foreign receipts and expired or stale reviews cannot send confirmation", async () => {
  const h = harness(); const flow = h.flow(); const shown = await flow.start({ kind: "Checkout", paymentMode: "success" });
  await assert.rejects(decideReview(flow, { ...shown, snapshotHash: "B".repeat(64) }, shown.id, "approve", h.gateway, h.claim, h.signal));
  await assert.rejects(decideReview(flow, shown, "33333333-3333-4333-8333-333333333333", "approve", h.gateway, h.claim, h.signal));
  h.set({ ...shown, status: "Expired" });
  assert.equal((await decideReview(flow, shown, shown.id, "approve", h.gateway, h.claim, h.signal)).review?.status, "Expired");
  assert.equal(h.counts().decided, 0);
  const stale = harness(); const staleFlow = stale.flow(); const terms = await staleFlow.start({ kind: "Checkout", paymentMode: "success" });
  stale.set({ ...terms, status: "Stale" });
  assert.equal((await decideReview(staleFlow, terms, terms.id, "approve", stale.gateway, stale.claim, stale.signal)).review?.status, "Stale");
  assert.equal(stale.counts().decided, 0);
});

test("lost decision response is never replayed; read-only sync recovers authoritative success", async () => {
  const h = harness(); const flow = h.flow(); const shown = await flow.start({ kind: "Checkout", paymentMode: "success" });
  const gateway = { ...h.gateway, decide: async () => { h.set({ ...shown, status: "Completed" }); throw new Error("lost response"); } };
  assert.equal((await decideReview(flow, shown, shown.id, "approve", gateway, h.claim, h.signal)).status, "uncertain");
  assert.equal((await h.flow().sync()).status, "Completed");
  assert.equal((await h.flow().inspect()).waiting, false);
  assert.equal(h.counts().prepared, 1);
});

test("uncertain request that never reached server remains reserved across flow reconstruction", async () => {
  const h = harness(); const shown = await h.flow().start({ kind: "Checkout", paymentMode: "success" });
  let attempts = 0;
  const gateway = { ...h.gateway, decide: async () => { attempts++; throw new Error("timeout"); } };
  assert.equal((await decideReview(h.flow(), shown, shown.id, "approve", gateway, h.claim, h.signal)).status, "uncertain");
  await assert.rejects(decideReview(h.flow(), shown, shown.id, "approve", gateway, h.claim, h.signal), /already attempted/);
  assert.equal(attempts, 1);
});

test("RAM owner isolation, serialization and deletion also cover review checkpoints and decision claims", async () => {
  const store = new RamConversations(); const alex = new Conversations(store, async () => "alex");
  const sam = new Conversations(store, async () => "sam"); const row = await alex.create(); const h = harness();
  let memory = h.memory;
  await alex.run(row.id, async value => { memory = value as typeof memory; await createReviewFlow(value, h.gateway, h.signal).start({ kind: "Checkout", paymentMode: "success" }); });
  await assert.rejects(sam.inspect(row.id, async () => assert.fail()), /not found/);
  await assert.rejects(sam.claimReviewDecision(row.id, h.current().id), /not found/);
  assert.equal(await alex.claimReviewDecision(row.id, h.current().id), true);
  assert.equal(await alex.claimReviewDecision(row.id, h.current().id), false);
  await alex.remove(row.id);
  assert.equal(await memory.checkpointer.getTuple({ configurable: { thread_id: reviewThread(row.id) } }), undefined);
  await store.close();
});

test("trusted review formatter includes all items, stock, totals, expiry, destination and exact decision IDs", () => {
  const review = reviewFixture(); const output = formatReview(review);
  for (const value of [review.id, review.snapshotHash, review.expiresAt, "quantity 2", "unit 10.00", "stock 10", "Total: 20.00 USD", review.snapshot.destination])
    assert.ok(output.includes(value));
  assert.ok(output.includes(`/approve ${review.id}`));
  assert.ok(!formatReview({ ...review, status: "Expired" }).includes("/approve"));
});

test("equivalent MCP and PostgreSQL timestamp encodings do not change review identity", () => {
  const value = reviewFixture(); value.expiresAt = "2026-10-25T12:00:00.1234567+00:00";
  assert.ok(sameReview(value, { ...value, expiresAt: "2026-10-25T12:00:00.123456Z" }));
  assert.ok(!sameReview(value, { ...value, expiresAt: "2026-10-25T12:00:01.123456Z" }));
});

test("a failed verification node can retry only its read without replaying preparation or decision", async () => {
  const h = harness(); const shown = await h.flow().start({ kind: "Checkout", paymentMode: "success" });
  h.set({ ...shown, status: "Completed" });
  const broken = createReviewFlow(h.memory, { ...h.gateway, read: async () => { throw new Error("temporary read failure"); } }, h.signal);
  await assert.rejects(broken.sync(), /temporary/);
  assert.equal((await h.flow().inspect()).verifying, true);
  assert.equal((await h.flow().sync()).status, "Completed");
  assert.deepEqual(h.counts(), { prepared: 1, decided: 0 });
});

test("decision HTTP uses CSRF and exact snapshot hash; there is no retry after a lost response", async () => {
  let posts = 0;
  const session = new ShopperSession(async (url, init) => {
    const path = String(url);
    if (path.endsWith("session")) return Response.json({ user: { id: "alex" } });
    if (path.endsWith("csrf")) return Response.json({ token: "csrf" });
    assert.ok(path.endsWith(`/actions/${reviewFixture().id}/confirm`));
    assert.equal(new Headers(init?.headers).get("X-CSRF-Token"), "csrf");
    assert.deepEqual(JSON.parse(String(init?.body)), { snapshotHash: "A".repeat(64) });
    assert.equal(init?.redirect, "error"); posts++; throw new Error("lost response");
  });
  await assert.rejects(session.decideReview(reviewFixture(), "approve", AbortSignal.timeout(5000)));
  assert.equal(posts, 1);
});
