import type { BaseMessage, AIMessage, ToolMessage } from "@langchain/core/messages";

export const contextLimits = { turns: 4, bytes: 8000 } as const;

// Drop entire oldest turns, never one half of an assistant/tool-call pair.
export function retainCompleteTurns(messages: BaseMessage[]): BaseMessage[] {
  const turns: BaseMessage[][] = [];
  for (const message of messages) {
    if (message.type === "system") continue;
    if (message.type === "human") turns.push([]);
    if (!turns.length) throw new Error("Conversation history does not begin with a user message.");
    turns.at(-1)!.push(message);
  }
  for (const turn of turns) {
    const pending = new Set<string>();
    for (const message of turn) {
      if (message.type === "ai") {
        if (pending.size) throw new Error("Conversation has an incomplete tool exchange.");
        for (const call of (message as AIMessage).tool_calls ?? []) {
          if (!call.id || pending.has(call.id)) throw new Error("Invalid tool-call pairing in conversation.");
          pending.add(call.id);
        }
      } else if (message.type === "tool" && !pending.delete((message as ToolMessage).tool_call_id)) {
        throw new Error("Conversation has an unmatched tool result.");
      }
    }
    if (pending.size || turn.at(-1)?.type !== "ai") throw new Error("Conversation has an unfinished turn.");
  }
  const kept: BaseMessage[][] = [];
  for (const turn of turns.slice(-contextLimits.turns).reverse()) {
    const candidate = [turn, ...kept];
    if (Buffer.byteLength(JSON.stringify(candidate.flat()), "utf8") > contextLimits.bytes) {
      if (!kept.length) throw new Error("The latest turn exceeds the memory budget. Start a new conversation with a narrower question.");
      break;
    }
    kept.unshift(turn);
  }
  return kept.flat();
}

// Old IDs lose authority when their source turn leaves the retained context.
export function referencedProductIds(messages: BaseMessage[]): string[] {
  const ids = new Set<string>();
  for (const message of messages) {
    if (message.type === "human" && typeof message.content === "string") {
      for (const id of message.content.match(/\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b/gi) ?? []) ids.add(id.toLowerCase());
    }
    if (message.type !== "tool" || (message as ToolMessage).status === "error" || typeof message.content !== "string") continue;
    const tool = message as ToolMessage;
    if (tool.name !== "search_products" && tool.name !== "get_product") continue;
    try {
      const body = JSON.parse(message.content);
      const items = tool.name === "search_products" ? body.items : [body];
      if (Array.isArray(items)) for (const item of items) {
        if (typeof item?.id === "string") ids.add(item.id.toLowerCase());
      }
    } catch { /* Text errors and nonstructured tool results confer no product authority. */ }
  }
  return [...ids];
}
