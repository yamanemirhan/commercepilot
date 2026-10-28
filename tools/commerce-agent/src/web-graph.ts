import { AIMessage, HumanMessage, SystemMessage, ToolMessage } from "@langchain/core/messages";
import type { AIMessageChunk, BaseMessage } from "@langchain/core/messages";
import type { BindToolsInput } from "@langchain/core/language_models/chat_models";
import { StateGraph, StateSchema, START, END } from "@langchain/langgraph";
import { AjvJsonSchemaValidator } from "@modelcontextprotocol/client/validators/ajv";
import { z } from "zod";

const names = new Set(["search_products", "get_product", "get_cart", "add_to_cart", "update_cart_item",
  "remove_from_cart", "get_my_orders", "get_order_status", "prepare_order", "prepare_cancellation",
  "prepare_refund", "get_pending_action_status"]);
const call = z.object({ function: z.object({ name: z.string(), arguments: z.record(z.string(), z.unknown()) }).strict() }).strict();
export const webRequest = z.object({
  messages: z.array(z.object({ role: z.enum(["system", "user", "assistant", "tool"]), content: z.string(),
    tool_calls: z.array(call).max(4).optional(), tool_name: z.string().optional() }).strict()).min(1).max(64),
  tools: z.array(z.object({ name: z.string(), description: z.string(), parameters: z.record(z.string(), z.unknown()) }).strict()).max(24),
}).strict();
export type WebRequest = z.infer<typeof webRequest>;
export type WebReply = { message: { role: "assistant"; content: string; tool_calls: z.infer<typeof call>[] };
  usage: { inputTokens: number; outputTokens: number; totalNanoseconds: number; loadNanoseconds: number } };
export type WebFrame = { type: "delta"; text: string } | { type: "result"; route: "answer" | "tools"; reply: WebReply };
export type StreamModel = (messages: BaseMessage[], tools: BindToolsInput[], signal: AbortSignal) => Promise<AsyncIterable<AIMessageChunk>>;

// .NET stores complete turns and tool results. Reconstruct paired LangChain IDs only
// for this inference; never put cookies, MCP tokens or a second chat store here.
export function toMessages(input: WebRequest["messages"]): BaseMessage[] {
  const messages: BaseMessage[] = [];
  let pending: { name: string; id: string }[] = [];
  for (const [index, message] of input.entries()) {
    if (message.role === "tool") {
      const next = pending.shift();
      if (!next || next.name !== message.tool_name || message.tool_calls) throw new Error("Unpaired tool result.");
      messages.push(new ToolMessage({ content: message.content, tool_call_id: next.id, name: next.name }));
      continue;
    }
    if (pending.length || message.tool_name || (message.role !== "assistant" && message.tool_calls?.length))
      throw new Error("Incomplete tool history.");
    if (message.role === "assistant") {
      const calls = (message.tool_calls ?? []).map((call, i) => ({ id: `call_${index}_${i}`,
        name: call.function.name, args: call.function.arguments, type: "tool_call" as const }));
      pending = calls.map(({ id, name }) => ({ id, name }));
      messages.push(new AIMessage({ content: message.content, tool_calls: calls }));
    } else messages.push(message.role === "system" ? new SystemMessage(message.content) : new HumanMessage(message.content));
  }
  if (pending.length) throw new Error("Missing tool results.");
  return messages;
}

// A bounded graph segment, not a second commerce executor. The trusted API owns
// the outer tool loop, durable history, references, human decisions and recovery.
export async function runWebGraph(raw: unknown, model: StreamModel, signal: AbortSignal,
  emit: (frame: WebFrame) => Promise<void>): Promise<void> {
  const request = webRequest.parse(raw);
  if (Buffer.byteLength(JSON.stringify(request)) > 24_576) throw new Error("Input budget exceeded.");
  if (new Set(request.tools.map(t => t.name)).size !== request.tools.length || request.tools.some(t => !names.has(t.name)))
    throw new Error("Unavailable tool catalog.");
  const messages = toMessages(request.messages);
  const validator = new AjvJsonSchemaValidator();
  const definitions = request.tools.map(t => ({ type: "function" as const, function: t }));
  const validators = new Map(request.tools.map(t => [t.name,
    validator.getValidator({ ...t.parameters, additionalProperties: false } as Parameters<typeof validator.getValidator>[0])]));
  const State = new StateSchema({ reply: z.custom<AIMessageChunk>().optional(), result: z.custom<WebReply>().optional() });
  const graph = new StateGraph(State)
    .addNode("model", async () => {
      let reply: AIMessageChunk | undefined;
      let bytes = 0;
      for await (const chunk of await model(messages, definitions, signal)) {
        signal.throwIfAborted();
        bytes += Buffer.byteLength(JSON.stringify(chunk));
        if (bytes > 1_048_576 || typeof chunk.content !== "string") throw new Error("Invalid model stream.");
        reply = reply ? reply.concat(chunk) : chunk;
        if ((reply.tool_calls?.length ?? 0) > 4) throw new Error("Tool budget exceeded.");
        // Only public text, never additional_kwargs.reasoning_content or raw state.
        if (chunk.content) await emit({ type: "delta", text: chunk.content });
      }
      if (!reply) throw new Error("Empty model stream.");
      return { reply };
    })
    .addNode("validate", state => {
      const reply = state.reply!;
      const usage = reply.usage_metadata;
      if (reply.response_metadata.done !== true || reply.response_metadata.done_reason !== "stop" ||
        !usage || !Number.isSafeInteger(usage.input_tokens) || !Number.isSafeInteger(usage.output_tokens) ||
        usage.input_tokens <= 0 || usage.input_tokens >= 5888 || usage.output_tokens < 0 ||
        usage.input_tokens + usage.output_tokens >= 8192 || reply.invalid_tool_calls?.length)
        throw new Error("Incomplete model response.");
      const calls = reply.tool_calls ?? [];
      if ((!calls.length && !String(reply.content).trim()) || calls.length > 4) throw new Error("Invalid answer.");
      const ids = new Set<string>();
      for (const call of calls) {
        if (!call.id || ids.has(call.id) || !validators.get(call.name)?.(call.args).valid)
          throw new Error("Invalid proposed operation.");
        ids.add(call.id);
      }
      return { result: { message: { role: "assistant" as const, content: String(reply.content),
        tool_calls: calls.map(call => ({ function: { name: call.name, arguments: call.args } })) },
        usage: { inputTokens: usage.input_tokens, outputTokens: usage.output_tokens,
          totalNanoseconds: 0, loadNanoseconds: 0 } } };
    })
    .addNode("tools_handoff", async state => { await emit({ type: "result", route: "tools", reply: state.result! }); return {}; })
    .addNode("answer", async state => { await emit({ type: "result", route: "answer", reply: state.result! }); return {}; })
    .addEdge(START, "model").addEdge("model", "validate")
    .addConditionalEdges("validate", state => state.result!.message.tool_calls.length ? "tools_handoff" : "answer")
    .addEdge("tools_handoff", END).addEdge("answer", END).compile();
  await graph.invoke({}, { signal, recursionLimit: 6, callbacks: [] });
}
