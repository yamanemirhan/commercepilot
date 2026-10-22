import assert from "node:assert/strict";
import { test } from "node:test";
import { randomBytes } from "node:crypto";
import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { PostgresConversations, localMemoryPool } from "../src/postgres-memory.js";
import { Conversations } from "../src/conversations.js";
import { ShopperSession } from "../src/shopper-session.js";
import { fixture, reply, secondId } from "./memory-fixture.js";

// Explicit opt-in suite: uses local PostgreSQL and real API authentication; no model or cloud calls.
test("PostgreSQL checkpoints survive reconnect, enforce real account isolation, serialize turns and purge all versions", async () => {
  const schema = `commerce_agent_test_${randomBytes(8).toString("hex")}`;
  const pool = await localMemoryPool();
  const cleanup = await localMemoryPool();
  const store = new PostgresConversations(pool, schema);
  const alex = new ShopperSession();
  const sam = new ShopperSession();
  const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
  let reconnected: PostgresConversations | undefined;
  let closed = false;
  try {
    await store.setup();
    await alex.login("alex@commercepilot.local", env.DEMO_ALEX_PASSWORD!);
    await sam.login("sam@commercepilot.local", env.DEMO_SAM_PASSWORD!);
    const service = new Conversations(store, () => alex.owner());
    const stranger = new Conversations(store, () => sam.owner());
    const row = await service.create();
    const { turn } = fixture();
    await service.run(row.id, turn("Show two speakers", [reply("", [
      { id: "s1", name: "search_products", args: { pageSize: 2 } }]), reply("1. First speaker. 2. Second speaker.")]));
    assert.deepEqual(await stranger.list(), []);
    await assert.rejects(stranger.run(row.id, async () => assert.fail("unauthorized graph execution")), /not found/);
    await assert.rejects(stranger.remove(row.id), /not found/);
    const blobs = await pool.query(`SELECT blob FROM "${schema}".checkpoint_blobs WHERE thread_id = $1`, [row.id]);
    assert.ok(blobs.rows.length > 0);
    for (const { blob } of blobs.rows) if (blob) {
      const body = blob.toString("utf8");
      for (const secret of [env.DEMO_ALEX_PASSWORD!, env.POSTGRES_PASSWORD!, "PRIVATE_REASONING_MARKER", "PROVIDER_METADATA_MARKER"]) {
        assert.ok(!body.includes(secret), "credential/provider metadata leaked into checkpoint");
      }
    }
    await store.close(); closed = true;
    reconnected = new PostgresConversations(await localMemoryPool(), schema);
    const resumed = new Conversations(reconnected, () => alex.owner());
    const answer = await resumed.run(row.id, turn("Tell me about the second one", [reply("", [
      { id: "d1", name: "get_product", args: { productId: secondId } }]), reply("The second speaker costs $69.")], messages => {
      assert.match(JSON.stringify(messages), /Second speaker/);
    }));
    assert.match(answer, /69/);

    let release!: () => void;
    let started!: () => void;
    const entered = new Promise<void>(resolve => { started = resolve; });
    const running = resumed.run(row.id, async () => { started(); await new Promise<void>(resolve => { release = resolve; }); });
    await entered;
    const otherProcess = new PostgresConversations(await localMemoryPool(), schema);
    try {
      const parallel = new Conversations(otherProcess, () => alex.owner());
      await assert.rejects(parallel.run(row.id, async () => assert.fail()), /busy/);
      await assert.rejects(parallel.remove(row.id), /busy/);
    } finally { release(); await running; await otherProcess.close(); }

    await assert.rejects(resumed.run(row.id, async () => { throw new Error("simulated failure"); }));
    await assert.rejects(resumed.run(row.id, async () => assert.fail()), /unfinished\/failed/);
    await resumed.remove(row.id);
    for (const table of ["checkpoints", "checkpoint_blobs", "checkpoint_writes"]) {
      const result = await cleanup.query(`SELECT count(*)::int AS count FROM "${schema}".${table} WHERE thread_id = $1`, [row.id]);
      assert.equal(result.rows[0].count, 0);
    }
    await assert.rejects(resumed.run(row.id, async () => assert.fail()), /not found/);

    const expired = await resumed.create();
    await resumed.run(expired.id, turn("Hello", [reply("Hello!")]));
    await cleanup.query(`UPDATE "${schema}".conversations SET expires_at = now() - interval '1 minute' WHERE id = $1`, [expired.id]);
    await assert.rejects(resumed.run(expired.id, async () => assert.fail()), /expired/);
    assert.equal(await reconnected.pruneExpired(), 1);
    assert.equal((await cleanup.query(`SELECT count(*)::int AS count FROM "${schema}".checkpoints`)).rows[0].count, 0);
    const crashed = await resumed.create();
    await cleanup.query(`UPDATE "${schema}".conversations SET status = 'running' WHERE id = $1`, [crashed.id]);
    await assert.rejects(resumed.run(crashed.id, async () => assert.fail()), /unfinished/);
    const limited = await resumed.create();
    await cleanup.query(`UPDATE "${schema}".conversations SET turns = 30 WHERE id = $1`, [limited.id]);
    await assert.rejects(resumed.run(limited.id, async () => assert.fail()), /turn limit/);
    await alex.logout();
    await assert.rejects(resumed.list(), /session ended/);
  } finally {
    await alex.logout().catch(() => {});
    await sam.logout().catch(() => {});
    if (!closed) await store.close();
    await reconnected?.close();
    // Only this test's randomly named, validated schema is disposable.
    assert.match(schema, /^commerce_agent_test_[a-f0-9]{16}$/);
    await cleanup.query(`DROP SCHEMA IF EXISTS "${schema}" CASCADE`);
    await cleanup.end();
  }
});
