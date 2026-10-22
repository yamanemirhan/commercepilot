import { AIMessage } from "@langchain/core/messages";
import type { BaseMessage, ToolCall } from "@langchain/core/messages";
import type { Client, Tool } from "@modelcontextprotocol/client";
import { runCatalogAgent } from "../src/catalog-agent.js";
import type { MemoryRun } from "../src/conversations.js";

export const firstId = "10000000-0000-0000-0000-000000000020";
export const secondId = "10000000-0000-0000-0000-000000000021";
export function reply(content: string, calls: ToolCall[] = []) {
  return new AIMessage({ content, tool_calls: calls, additional_kwargs: { reasoning_content: "PRIVATE_REASONING_MARKER" },
    response_metadata: { done: true, done_reason: "stop", private: "PROVIDER_METADATA_MARKER" },
    usage_metadata: { input_tokens: 100, output_tokens: 20, total_tokens: 120 } });
}
export function fixture() {
  const called: { name: string; arguments?: Record<string, unknown> }[] = [];
  const client: Pick<Client, "listTools" | "callTool"> = {
    listTools: async () => ({ tools: [
      { name: "search_products", inputSchema: { type: "object", properties: { pageSize: { type: "integer" } } } },
      { name: "get_product", inputSchema: { type: "object", properties: { productId: { type: "string" } }, required: ["productId"] } },
    ] as Tool[] }),
    callTool: async call => {
      called.push(call);
      return { content: [], structuredContent: call.name === "search_products"
        ? { items: [{ id: firstId, name: "First speaker", price: 49 }, { id: secondId, name: "Second speaker", price: 69 }] }
        : { id: call.arguments?.productId, name: "Second speaker", price: 69, stock: 14 } };
    },
  };
  const turn = (question: string, replies: AIMessage[], inspect?: (messages: BaseMessage[]) => void): MemoryRun<string> =>
    memory => runCatalogAgent(question, client, () => ({ invoke: async messages => {
      inspect?.(messages);
      const next = replies.shift();
      if (!next) throw new Error("Missing test reply.");
      return next;
    } }), AbortSignal.timeout(5000), () => {}, memory);
  return { called, turn };
}
