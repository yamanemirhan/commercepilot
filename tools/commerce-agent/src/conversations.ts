import { randomUUID } from "node:crypto";
import { MemorySaver } from "@langchain/langgraph";
import type { BaseCheckpointSaver } from "@langchain/langgraph";
import { reviewThread, guid } from "./review-contract.js";

export const memoryLimits = { conversations: 20, turns: 30, lifetimeMs: 24 * 60 * 60 * 1000 } as const;
export type Conversation = { id: string; turns: number; status: "ready" | "running" | "failed"; expiresAt: Date };
export type MemoryRun<T> = (memory: { checkpointer: BaseCheckpointSaver; threadId: string }) => Promise<T>;
export interface ConversationStore {
  create(owner: string): Promise<Conversation>;
  list(owner: string): Promise<Conversation[]>;
  run<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T>;
  inspect<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T>;
  claimReviewDecision(owner: string, id: string, actionId: string): Promise<boolean>;
  remove(owner: string, id: string): Promise<void>;
  close(): Promise<void>;
}
export const notFound = () => new Error("Conversation not found or expired.");
export function validateConversationId(id: string): void {
  if (!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(id)) throw notFound();
}
export function checkReady(row: Conversation): void {
  if (row.status !== "ready") throw new Error("Conversation is busy or has an unfinished/failed turn. Start a new conversation.");
  if (row.turns >= memoryLimits.turns) throw new Error("Conversation turn limit reached. Start a new conversation.");
}

// The identity resolver must come from authentication, never from a prompt or caller-supplied user ID.
export class Conversations {
  constructor(private readonly store: ConversationStore, private readonly identify: () => Promise<string>) {}
  async create() { return this.store.create(await this.identify()); }
  async list() { return this.store.list(await this.identify()); }
  async run<T>(id: string, work: MemoryRun<T>) { return this.store.run(await this.identify(), id, work); }
  async inspect<T>(id: string, work: MemoryRun<T>) { return this.store.inspect(await this.identify(), id, work); }
  async claimReviewDecision(id: string, actionId: string) { return this.store.claimReviewDecision(await this.identify(), id, actionId); }
  async remove(id: string) { return this.store.remove(await this.identify(), id); }
}

export class RamConversations implements ConversationStore {
  private readonly rows = new Map<string, Conversation & { owner: string }>();
  private readonly saver = new MemorySaver();
  private readonly busy = new Set<string>();
  private readonly decisions = new Map<string, string>();
  constructor(private readonly now = () => Date.now()) {}
  private get(owner: string, id: string) {
    validateConversationId(id);
    const row = this.rows.get(id);
    if (!row || row.owner !== owner || row.expiresAt.getTime() <= this.now()) throw notFound();
    return row;
  }
  private async prune() {
    for (const [id, row] of this.rows) if (row.expiresAt.getTime() <= this.now() && !this.busy.has(id)) {
      await this.erase(id);
    }
  }
  async create(owner: string): Promise<Conversation> {
    await this.prune();
    // A teaching process also has a global RAM cap, regardless of account count.
    if (this.rows.size >= memoryLimits.conversations) throw new Error("RAM conversation limit reached. Delete a conversation first.");
    const row = { id: randomUUID(), owner, turns: 0, status: "ready" as const,
      expiresAt: new Date(this.now() + memoryLimits.lifetimeMs) };
    this.rows.set(row.id, row);
    return { id: row.id, turns: row.turns, status: row.status, expiresAt: row.expiresAt };
  }
  async list(owner: string): Promise<Conversation[]> {
    await this.prune();
    return [...this.rows.values()].filter(row => row.owner === owner).map(({ owner: _, ...row }) => ({ ...row }));
  }
  async run<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T> {
    const row = this.get(owner, id);
    checkReady(row);
    row.status = "running"; row.turns++; this.busy.add(id);
    try {
      const result = await work({ checkpointer: this.saver, threadId: id });
      row.status = "ready";
      return result;
    } catch (error) { row.status = "failed"; throw error; }
    finally { this.busy.delete(id); }
  }
  async remove(owner: string, id: string) {
    this.get(owner, id);
    if (this.busy.has(id)) throw new Error("Conversation is busy.");
    await this.erase(id);
  }
  async inspect<T>(owner: string, id: string, work: MemoryRun<T>): Promise<T> {
    this.get(owner, id);
    if (this.busy.has(id)) throw new Error("Conversation is busy.");
    this.busy.add(id);
    try { return await work({ checkpointer: this.saver, threadId: id }); }
    finally { this.busy.delete(id); }
  }
  async claimReviewDecision(owner: string, id: string, actionId: string): Promise<boolean> {
    this.get(owner, id); guid.parse(actionId);
    if (this.decisions.has(actionId)) return false;
    this.decisions.set(actionId, id); return true;
  }
  private async erase(id: string) {
    this.rows.delete(id);
    await this.saver.deleteThread(id); await this.saver.deleteThread(reviewThread(id));
    for (const [action, conversation] of this.decisions) if (conversation === id) this.decisions.delete(action);
  }
  async close() {
    for (const id of this.rows.keys()) await this.erase(id);
  }
}
