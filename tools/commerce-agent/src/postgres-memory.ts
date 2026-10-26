import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { randomUUID } from "node:crypto";
import pg from "pg";
import { PostgresSaver } from "@langchain/langgraph-checkpoint-postgres";
import { memoryLimits, notFound, checkReady, validateConversationId } from "./conversations.js";
import type { Conversation, ConversationStore, MemoryRun } from "./conversations.js";
import { reviewThread, guid } from "./review-contract.js";

export async function localMemoryPool(): Promise<pg.Pool> {
  const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
  if (!env.POSTGRES_PASSWORD) throw new Error("Run scripts/start.ps1 to configure the local database first.");
  // Local developer CLI only. Do not distribute database credentials to web clients.
  return new pg.Pool({ host: "127.0.0.1", port: 5438, database: "commercepilot", user: "commercepilot",
    password: env.POSTGRES_PASSWORD, max: 6, connectionTimeoutMillis: 5000, statement_timeout: 10_000,
    idleTimeoutMillis: 10_000 });
}

export class PostgresConversations implements ConversationStore {
  private readonly saver: PostgresSaver;
  private readonly table: string;
  constructor(private readonly pool: pg.Pool, private readonly schema = "commerce_agent") {
    if (!/^commerce_agent(?:_test_[a-z0-9]+)?$/.test(schema)) throw new Error("Invalid memory schema.");
    this.saver = new PostgresSaver(pool, undefined, { schema });
    this.table = `"${schema}".conversations`;
  }
  async setup() {
    await this.saver.setup();
    await this.pool.query(`CREATE TABLE IF NOT EXISTS ${this.table} (
      id uuid PRIMARY KEY, owner_id text NOT NULL, turns integer NOT NULL DEFAULT 0 CHECK (turns BETWEEN 0 AND 30),
      status text NOT NULL DEFAULT 'ready' CHECK (status IN ('ready', 'running', 'failed')),
      expires_at timestamptz NOT NULL DEFAULT (now() + interval '24 hours'))`);
    await this.pool.query(`CREATE INDEX IF NOT EXISTS conversations_owner ON ${this.table} (owner_id, expires_at)`);
    await this.pool.query(`CREATE INDEX IF NOT EXISTS conversations_expiry ON ${this.table} (expires_at)`);
    await this.pool.query(`CREATE TABLE IF NOT EXISTS "${this.schema}".review_decisions (
      action_id uuid PRIMARY KEY, conversation_id uuid NOT NULL REFERENCES ${this.table}(id) ON DELETE CASCADE,
      attempted_at timestamptz NOT NULL DEFAULT now())`);
  }
  private async locked<T>(key: string, work: (client: pg.PoolClient) => Promise<T>) {
    const client = await this.pool.connect();
    let locked = false;
    try {
      const result = await client.query("SELECT pg_try_advisory_lock(hashtextextended($1, 0)) AS locked", [key]);
      locked = result.rows[0].locked;
      if (!locked) throw new Error("Conversation is busy. Try again when its current operation finishes.");
      return await work(client);
    } finally {
      let discard = false;
      try { if (locked) await client.query("SELECT pg_advisory_unlock(hashtextextended($1, 0))", [key]); }
      catch (error) { discard = true; throw error; }
      finally { client.release(discard); } // Never return a possibly still-locked session to the pool.
    }
  }
  private async owned(client: pg.Pool | pg.PoolClient, owner: string, id: string): Promise<Conversation> {
    validateConversationId(id);
    const result = await client.query(`SELECT id, turns, status, expires_at AS "expiresAt" FROM ${this.table}
      WHERE id = $1 AND owner_id = $2 AND expires_at > now()`, [id, owner]);
    if (!result.rowCount) throw notFound();
    return result.rows[0];
  }
  async create(owner: string): Promise<Conversation> {
    return this.locked(`agent-owner:${owner}`, async client => {
      const count = await client.query(`SELECT count(*)::int AS count FROM ${this.table} WHERE owner_id = $1 AND expires_at > now()`, [owner]);
      if (count.rows[0].count >= memoryLimits.conversations) throw new Error("Conversation limit reached. Delete a conversation first.");
      const result = await client.query(`INSERT INTO ${this.table} (id, owner_id) VALUES ($1, $2)
        RETURNING id, turns, status, expires_at AS "expiresAt"`, [randomUUID(), owner]);
      return result.rows[0];
    });
  }
  async list(owner: string): Promise<Conversation[]> {
    const result = await this.pool.query(`SELECT id, turns, status, expires_at AS "expiresAt" FROM ${this.table}
      WHERE owner_id = $1 AND expires_at > now() ORDER BY expires_at DESC LIMIT 20`, [owner]);
    return result.rows;
  }
  async run<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T> {
    await this.owned(this.pool, owner, id); // No state reads or lock acquisition for another owner's ID.
    return this.locked(`agent-thread:${id}`, async client => {
      checkReady(await this.owned(client, owner, id));
      // Persist before inference: process death leaves an explicit unfinished turn, never an implicit retry.
      await client.query(`UPDATE ${this.table} SET status = 'running', turns = turns + 1 WHERE id = $1 AND owner_id = $2`, [id, owner]);
      try {
        const result = await work({ checkpointer: this.saver, threadId: id });
        await client.query(`UPDATE ${this.table} SET status = 'ready' WHERE id = $1 AND owner_id = $2`, [id, owner]);
        return result;
      } catch (error) {
        await client.query(`UPDATE ${this.table} SET status = 'failed' WHERE id = $1 AND owner_id = $2`, [id, owner]);
        throw error;
      }
    });
  }
  private async erase(client: pg.PoolClient, id: string) {
    // Atomic removal includes all versions/blobs/pending writes, not just the latest messages.
    await client.query("BEGIN");
    try {
      for (const name of ["checkpoint_writes", "checkpoint_blobs", "checkpoints"]) {
        await client.query(`DELETE FROM "${this.schema}".${name} WHERE thread_id = ANY($1::text[])`, [[id, reviewThread(id)]]);
      }
      await client.query(`DELETE FROM ${this.table} WHERE id = $1`, [id]);
      await client.query("COMMIT");
    } catch (error) { await client.query("ROLLBACK"); throw error; }
  }
  async remove(owner: string, id: string) {
    await this.owned(this.pool, owner, id);
    await this.locked(`agent-thread:${id}`, async client => {
      await this.owned(client, owner, id);
      await this.erase(client, id);
    });
  }
  async inspect<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T> {
    await this.owned(this.pool, owner, id);
    return this.locked(`agent-thread:${id}`, async client => {
      await this.owned(client, owner, id);
      return work({ checkpointer: this.saver, threadId: id });
    });
  }
  async claimReviewDecision(owner: string, id: string, actionId: string): Promise<boolean> {
    guid.parse(actionId);
    await this.owned(this.pool, owner, id);
    const result = await this.pool.query(`INSERT INTO "${this.schema}".review_decisions (action_id, conversation_id)
      VALUES ($1,$2) ON CONFLICT (action_id) DO NOTHING RETURNING action_id`, [actionId, id]);
    return result.rowCount === 1;
  }
  async pruneExpired(): Promise<number> {
    const expired = await this.pool.query(`SELECT id FROM ${this.table} WHERE expires_at <= now() ORDER BY expires_at LIMIT 100`);
    let removed = 0;
    for (const { id } of expired.rows) {
      try {
        await this.locked(`agent-thread:${id}`, async client => { await this.erase(client, id); removed++; });
      } catch (error) {
        if (!(error instanceof Error && error.message.startsWith("Conversation is busy"))) throw error;
      }
    }
    return removed;
  }
  async close() { await this.pool.end(); }
}
