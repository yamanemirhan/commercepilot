import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { z } from "zod";
import { webRequest } from "./web-graph.js";
import type { WebReply, WebRequest } from "./web-graph.js";

export const root = new URL("../../../", import.meta.url);
export const variantSchema = z.enum(["baseline", "concise"]);
export const caseSchema = z.object({ id: z.string().regex(/^\d{2}-[a-z-]+$/),
  category: z.enum(["filters", "references", "cart", "orders", "reviews", "conversation", "safety", "recovery", "rag"]),
  messages: webRequest.shape.messages,
  expectedCalls: z.array(z.object({ name: z.string(), arguments: z.record(z.string(), z.unknown()) }).strict()),
  requiredTextGroups: z.array(z.array(z.string()).min(1)).optional(),
  forbiddenText: z.array(z.string()).optional(), forbiddenPatterns: z.array(z.string().max(300)).optional(),
}).strict();
export type EvalCase = z.infer<typeof caseSchema>;
export const hash = (value: string) => createHash("sha256").update(value).digest("hex");
export function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.entries(value).sort(([a], [b]) => a.localeCompare(b))
    .map(([k,v]) => `${JSON.stringify(k)}:${canonical(v)}`).join(",")}}`;
  return JSON.stringify(value);
}

// Read only these known C# constant forms. Fail closed if their structure changes;
// do not maintain a separate, silently stale copy of the production prompt.
export function rawConstant(source: string, name: string): string {
  const match = source.replaceAll("\r\n", "\n").match(new RegExp(`${name} = (?:ShoppingInstructions.SystemMessage \\+ )?"""\\n([\\s\\S]*?)\\n( +)""";`));
  if (!match) throw new Error("Production prompt format changed; update the evaluation loader.");
  return match[1]!.split("\n").map(line => line.startsWith(match[2]!) ? line.slice(match[2]!.length) : line).join("\n");
}
export async function loadSuite() {
  const [basic, rag, schemas, shopping, policy, service, graph, model, evaluator, runner] = await Promise.all([
    "tools/commerce-agent/evaluation/cases.json", "tools/commerce-agent/evaluation/rag-cases.json",
    "tools/commerce-agent/evaluation/tool-schemas.json", "src/Commerce.AI/ShoppingInstructions.cs",
    "src/Commerce.Api/ChatPolicy.cs", "src/Commerce.Api/ChatService.cs",
    "tools/commerce-agent/src/web-graph.ts", "tools/commerce-agent/src/model.ts",
    "tools/commerce-agent/src/evaluation-core.ts", "tools/commerce-agent/src/evaluation-cli.ts",
  ].map(path => readFile(new URL(path, root), "utf8")));
  const cases = z.array(caseSchema).length(44).parse([...JSON.parse(basic!), ...JSON.parse(rag!)]);
  if (new Set(cases.map(c => c.id)).size !== cases.length) throw new Error("Duplicate scenario ID.");
  const tools = webRequest.shape.tools.parse(JSON.parse(schemas!));
  if (tools.length !== 12) throw new Error("Expected twelve frozen tool schemas.");
  const siteExpression = service!.match(/instructions \+= ([\s\S]*?);\r?\n\s*var preceding/);
  const literals = siteExpression?.[1]?.match(/"(?:\\.|[^"\\])*"/g);
  if (!literals || literals.length !== 5) throw new Error("Production site prompt format changed.");
  const prompt = rawConstant(shopping!, "SystemMessage") + rawConstant(policy!, "Instructions") + literals.map(s => JSON.parse(s)).join("");
  return { cases, tools, prompt, corpusHash: hash(canonical(cases)), schemasHash: hash(canonical(tools)),
    basePromptHash: hash(prompt), graphHash: hash(graph!.replaceAll("\r\n", "\n") + model!.replaceAll("\r\n", "\n")),
    evaluatorHash: hash(evaluator!.replaceAll("\r\n", "\n") + runner!.replaceAll("\r\n", "\n")) };
}
export function variantPrompt(prompt: string, variant: z.infer<typeof variantSchema>) {
  return variant === "baseline" ? prompt : prompt + "\nFor this response, use at most two short sentences. " +
    "Name the required next step clearly. Preserve every necessary safety or uncertainty explanation. Do not restate facts already shown on cards.";
}

export function score(test: EvalCase, reply: WebReply | null, tools: WebRequest["tools"]) {
  if (!reply) return { toolMatch: false, explanation: false, passed: false };
  const calls = reply.message.tool_calls.map(c => c.function);
  const toolMatch = calls.length === test.expectedCalls.length && calls.every((actual, i) => {
    const expected = test.expectedCalls[i]!;
    if (actual.name !== expected.name) return false;
    const properties = tools.find(t => t.name === actual.name)?.parameters.properties as Record<string, { default?: unknown }> | undefined;
    const value = (args: Record<string, unknown>, key: string) => Object.hasOwn(args, key) ? args[key] : properties?.[key]?.default ?? null;
    return [...new Set([...Object.keys(actual.arguments), ...Object.keys(expected.arguments)])]
      .every(key => canonical(value(actual.arguments, key)) === canonical(value(expected.arguments, key)));
  });
  const text = reply.message.content.toLowerCase();
  const explanation = (calls.length > 0 || text.trim().length > 0) &&
    (test.requiredTextGroups ?? []).every(group => group.some(part => text.includes(part.toLowerCase()))) &&
    !(test.forbiddenText ?? []).some(part => text.includes(part.toLowerCase())) &&
    !(test.forbiddenPatterns ?? []).some(pattern => new RegExp(pattern, "im").test(text));
  return { toolMatch, explanation, passed: toolMatch && explanation };
}

const sha = z.string().regex(/^[a-f0-9]{64}$/);
const count = z.number().int().min(0).max(100_000);
const replySchema = z.object({ message: z.object({ role: z.literal("assistant"), content: z.string().max(131072),
  tool_calls: z.array(z.object({ function: z.object({ name: z.string(), arguments: z.record(z.string(), z.unknown()) }) })).max(4) }),
  usage: z.object({ inputTokens: count, outputTokens: count, totalNanoseconds: z.number().nonnegative(), loadNanoseconds: z.number().nonnegative() }) });
export const reportSchema = z.object({ version: z.literal(1), id: z.string().uuid(), createdAt: z.string().datetime(),
  mode: z.literal("proposal-only"), variant: variantSchema, repeats: z.number().int().min(1).max(3),
  corpusHash: sha, schemasHash: sha, basePromptHash: sha, promptHash: sha, graphHash: sha, evaluatorHash: sha,
  model: z.literal("qwen3.8:27b"), modelDigest: sha, ollamaVersion: z.string().max(80),
  completed: z.boolean(), rows: z.array(z.object({ caseId: caseSchema.shape.id, repeat: z.number().int().min(1).max(3),
    category: caseSchema.shape.category, startedAt: z.string().datetime(), durationMs: z.number().min(0).max(240_000),
    firstTextMs: z.number().min(0).max(240_000).nullable(), error: z.enum(["round-failed", "cancelled"]).nullable(),
    reply: replySchema.nullable(), toolMatch: z.boolean(), explanation: z.boolean(), passed: z.boolean(),
  }).strict().refine(row => row.firstTextMs === null || row.firstTextMs <= row.durationMs, "Invalid first-text timing")
    .refine(row => row.error === null ? row.reply !== null : row.reply === null, "Inconsistent result/error")).max(132) }).strict();
export type Report = z.infer<typeof reportSchema>;
export const critical = (category: string) => ["safety", "recovery", "rag"].includes(category);
export function summary(report: Report) {
  const durations = report.rows.map(r => r.durationMs).sort((a,b) => a-b);
  return { completed: report.completed, passed: report.rows.filter(r => r.passed).length, total: report.rows.length,
    criticalFailures: report.rows.filter(r => critical(r.category) && !r.passed).map(r => r.caseId),
    medianMs: durations.length ? durations[Math.floor(durations.length / 2)]! : null,
    p95Ms: durations.length ? durations[Math.ceil(durations.length * .95) - 1]! : null,
    inputTokens: report.rows.reduce((sum,r) => sum + (r.reply?.usage.inputTokens ?? 0),0),
    outputTokens: report.rows.reduce((sum,r) => sum + (r.reply?.usage.outputTokens ?? 0),0),
    missingUsage: report.rows.filter(r => !r.reply).length };
}
export function compare(a: Report, b: Report) {
  for (const field of ["version", "corpusHash", "schemasHash", "basePromptHash", "graphHash", "evaluatorHash", "modelDigest", "ollamaVersion", "repeats"] as const)
    if (a[field] !== b[field]) throw new Error(`Incomparable reports: ${field} differs.`);
  if (!a.completed || !b.completed || a.rows.length !== 44 * a.repeats || b.rows.length !== 44 * b.repeats)
    throw new Error("Only complete reports can be compared.");
  for (const report of [a,b]) {
    if (new Set(report.rows.map(r => r.caseId)).size !== 44 || report.rows.some(r => r.repeat > report.repeats))
      throw new Error("Invalid repetition coverage.");
  }
  const key = (row: Report["rows"][number]) => `${row.caseId}:${row.repeat}`;
  const rows = new Map(a.rows.map(row => [key(row),row]));
  if (rows.size !== a.rows.length || new Set(b.rows.map(key)).size !== b.rows.length || b.rows.some(r => !rows.has(key(r))))
    throw new Error("Scenario/repetition identities differ or repeat.");
  return { baseline: summary(a), candidate: summary(b),
    regressions: b.rows.filter(r => rows.get(key(r))!.passed && !r.passed).map(key),
    improvements: b.rows.filter(r => !rows.get(key(r))!.passed && r.passed).map(key) };
}
