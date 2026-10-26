import assert from "node:assert/strict";
import { test } from "node:test";
import { randomBytes, randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { localMemoryPool, PostgresConversations } from "../src/postgres-memory.js";
import { Conversations } from "../src/conversations.js";
import { ShopperSession } from "../src/shopper-session.js";
import { reviewGateway } from "../src/review-client.js";
import { createReviewFlow, decideReview } from "../src/review-graph.js";
import { reviewThread } from "../src/review-contract.js";

// Isolated temporary shopper + checkpoint schema. Checkout uses simulated failure so stock/cart stay intact.
test("real MCP preparation, durable pause/reconnect, API approval/rejection, stale/expiry and decision recovery", async () => {
  const pool = await localMemoryPool();
  const schema = `commerce_agent_test_${randomBytes(8).toString("hex")}`;
  let store = new PostgresConversations(await localMemoryPool(), schema);
  const owner = randomUUID(), email = `review-${owner}@example.test`;
  const session = new ShopperSession(), other = new ShopperSession();
  const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
  const signal = AbortSignal.timeout(120_000);
  let fixtureCreated = false;
  try {
    await store.setup();
    const seeded = await pool.query(`INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail",
      "EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName")
      SELECT $1,$2::text,upper($2::text),$2::text,upper($2::text),false,"PasswordHash",$3,$4,false,false,true,0,'Review integration fixture'
      FROM "AspNetUsers" WHERE "Email"='alex@commercepilot.local'`, [owner, email, randomUUID(), randomUUID()]);
    assert.equal(seeded.rowCount, 1); fixtureCreated = true;
    await session.login(email, env.DEMO_ALEX_PASSWORD!);
    await other.login("sam@commercepilot.local", env.DEMO_SAM_PASSWORD!);
    const product = (await pool.query('SELECT * FROM "Products" WHERE "Stock" >= 10 ORDER BY "Id" LIMIT 1')).rows[0];
    assert.ok(product);
    await pool.query('INSERT INTO "CartItems" ("UserId","ProductId","Quantity") VALUES ($1,$2,1)', [owner, product.Id]);
    const gateway = reviewGateway(session);
    let service = new Conversations(store, () => session.owner());
    const row = await service.create();
    const terms = await service.run(row.id, memory => createReviewFlow(memory, gateway, signal).start({ kind: "Checkout", paymentMode: "failure" }));
    assert.equal(terms.status, "Pending"); assert.equal(terms.snapshot.items.length, 1);
    const untouched = await pool.query('SELECT count(*)::int AS count FROM "Orders" WHERE "UserId"=$1', [owner]);
    assert.equal(untouched.rows[0].count, 0);
    // Process reconstruction uses durable state and fresh session transport, without rerunning prepare.
    await store.close(); store = new PostgresConversations(await localMemoryPool(), schema);
    service = new Conversations(store, () => session.owner());
    const stranger = new Conversations(store, () => other.owner());
    await assert.rejects(stranger.inspect(row.id, async () => assert.fail()), /not found/);
    await assert.rejects(other.readReview(terms.id, signal));
    await assert.rejects(other.decideReview(terms, "approve", signal));
    const resumed = await service.inspect(row.id, memory => createReviewFlow(memory, gateway, signal).inspect());
    assert.equal(resumed.waiting, true); assert.equal(resumed.review?.id, terms.id);

    // Reserve BEFORE posting; deliberately lose the successful server response.
    let posts = 0;
    const lostReply = { ...gateway, decide: async (...args: Parameters<typeof gateway.decide>) => {
      posts++; await gateway.decide(...args); throw new Error("simulated lost reply");
    } };
    const uncertain = await service.inspect(row.id, memory => decideReview(createReviewFlow(memory, gateway, signal), terms, terms.id, "approve",
      lostReply, () => service.claimReviewDecision(row.id, terms.id), signal));
    assert.equal(uncertain.status, "uncertain"); assert.equal(posts, 1);
    await store.close(); store = new PostgresConversations(await localMemoryPool(), schema);
    service = new Conversations(store, () => session.owner());
    assert.equal(await service.claimReviewDecision(row.id, terms.id), false);
    const outcome = await service.inspect(row.id, memory => createReviewFlow(memory, gateway, signal).sync());
    assert.equal(outcome.status, "Completed"); assert.ok(outcome.orderId);
    const order = (await pool.query('SELECT * FROM "Orders" WHERE "Id"=$1 AND "UserId"=$2', [outcome.orderId, owner])).rows[0];
    assert.equal(order.Status, "PaymentFailed"); assert.equal(order.PaymentStatus, "Failed");
    assert.equal((await pool.query('SELECT "Stock" FROM "Products" WHERE "Id"=$1', [product.Id])).rows[0].Stock, product.Stock);
    assert.equal((await pool.query('SELECT count(*)::int AS count FROM "Orders" WHERE "UserId"=$1', [owner])).rows[0].count, 1);
    await service.remove(row.id);
    for (const table of ["checkpoints", "checkpoint_blobs", "checkpoint_writes"])
      assert.equal((await pool.query(`SELECT count(*)::int AS count FROM "${schema}".${table} WHERE thread_id=$1`, [reviewThread(row.id)])).rows[0].count, 0);
    assert.equal((await pool.query(`SELECT count(*)::int AS count FROM "${schema}".review_decisions`)).rows[0].count, 0);

    for (const mode of ["reject", "expired", "stale"] as const) {
      const conversation = await service.create();
      const review = await service.run(conversation.id, memory => createReviewFlow(memory, gateway, signal).start({ kind: "Checkout", paymentMode: "failure" }));
      if (mode === "stale") await pool.query('UPDATE "CartItems" SET "Quantity"=2 WHERE "UserId"=$1', [owner]);
      if (mode === "expired") {
        // Insert a separate historical fixture; never bypass the immutable-snapshot database trigger.
        const expiredId = randomUUID();
        await pool.query(`INSERT INTO "PendingActions" ("Id","UserId","Kind","Status","SnapshotJson","SnapshotHash","CreatedAt","ExpiresAt","OrderId")
          SELECT $1,"UserId","Kind",'Pending',"SnapshotJson","SnapshotHash",now()-interval '6 minutes',now()-interval '1 minute',"OrderId"
          FROM "PendingActions" WHERE "Id"=$2 AND "UserId"=$3`, [expiredId, review.id, owner]);
        const expired = await session.readReview(expiredId, signal);
        await assert.rejects(session.decideReview(expired, "approve", signal));
        assert.equal((await session.readReview(expiredId, signal)).status, "Expired");
      } else {
        const result = await service.inspect(conversation.id, memory => decideReview(createReviewFlow(memory, gateway, signal), review, review.id,
          mode === "reject" ? "reject" : "approve", gateway, () => service.claimReviewDecision(conversation.id, review.id), signal));
        if (mode === "reject") assert.equal(result.review?.status, "Rejected");
        else {
          assert.equal(result.status, "uncertain");
          assert.equal((await service.inspect(conversation.id, memory => createReviewFlow(memory, gateway, signal).sync())).status, "Stale");
        }
      }
      await service.remove(conversation.id);
    }
    // Eligible synthetic orders never scheduled for the demo worker; cancellation does not release fixture stock.
    for (const kind of ["Cancellation", "Refund"] as const) {
      const id = randomUUID();
      const snapshot = JSON.stringify({ Kind: "Checkout", Items: [{ ProductId: product.Id, Name: product.Name, Artwork: product.Artwork,
        Color: product.Color, Quantity: 1, UnitPrice: Number(product.Price), AvailableStock: product.Stock }],
        Total: Number(product.Price), Currency: "USD", PaymentMode: "success", Destination: "Isolated demo fixture", OrderId: null, OrderVersion: null });
      await pool.query(`INSERT INTO "Orders" ("Id","UserId","SnapshotJson","Total","Currency","Status","PaymentStatus",
        "CapturedAmount","RefundedAmount","StockReleased","Version","CreatedAt","DeliveredAt")
        VALUES ($1,$2,$3,$4,'USD',$5,'Captured',$4,0,true,1,now(),now())`, [id, owner, snapshot, product.Price, kind === "Refund" ? "Delivered" : "Processing"]);
      const conversation = await service.create();
      const terms = await service.run(conversation.id, memory => createReviewFlow(memory, gateway, signal).start({ kind, orderId: id }));
      const completed = await service.inspect(conversation.id, memory => decideReview(createReviewFlow(memory, gateway, signal), terms, terms.id,
        "approve", gateway, () => service.claimReviewDecision(conversation.id, terms.id), signal));
      assert.equal(completed.review?.status, "Completed");
      assert.equal((await pool.query('SELECT "Status" FROM "Orders" WHERE "Id"=$1', [id])).rows[0].Status, kind === "Refund" ? "Refunded" : "Cancelled");
      await service.remove(conversation.id);
    }
    assert.equal((await pool.query('SELECT "Stock" FROM "Products" WHERE "Id"=$1', [product.Id])).rows[0].Stock, product.Stock);
  } finally {
    await session.logout().catch(() => {}); await other.logout().catch(() => {}); await store.close();
    assert.match(schema, /^commerce_agent_test_[a-f0-9]{16}$/);
    await pool.query(`DROP SCHEMA IF EXISTS "${schema}" CASCADE`);
    if (fixtureCreated) {
      await pool.query('DELETE FROM "PendingActions" WHERE "UserId"=$1', [owner]);
      await pool.query('DELETE FROM "OrderEvent" WHERE "OrderId" IN (SELECT "Id" FROM "Orders" WHERE "UserId"=$1)', [owner]);
      await pool.query('DELETE FROM "Orders" WHERE "UserId"=$1', [owner]);
      await pool.query('DELETE FROM "CartItems" WHERE "UserId"=$1', [owner]);
      await pool.query('DELETE FROM "AspNetUsers" WHERE "Id"=$1 AND "Email"=$2', [owner, email]);
    }
    await pool.end();
  }
});
