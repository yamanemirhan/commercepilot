import { readFile, mkdir, writeFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { Client, RunTree } from "langsmith";
import type { AgentEvent } from "./catalog-agent.js";

export type TraceMode = "off" | "preview" | "langsmith";
export type TraceConfig = { apiKey: string; endpoint: string; project: string; workspaceId?: string };
type Status = "completed" | "failed" | "cancelled";
type Span = { name: "catalog_agent" | "model" | "tools" | "search_products" | "get_product";
  parent: number; start: number; end?: number; status?: Status; inputTokens?: number; outputTokens?: number };
const localDirectory = new URL("../../../.local/", import.meta.url);
const endpoints = new Set(["https://api.smith.langchain.com", "https://eu.api.smith.langchain.com"]);
const tokens = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) >= 0 && Number(value) <= 8192;

export function validateTraceConfig(env: Record<string, string | undefined>): TraceConfig {
  const apiKey = env.LANGSMITH_API_KEY?.trim();
  if (!apiKey || apiKey === "YOUR_TRACING_KEY" || apiKey.length > 512 || /\s/.test(apiKey))
    throw new Error("Set LANGSMITH_API_KEY in .local/langsmith.env before using --trace. Never paste the key into chat.");
  const endpoint = env.LANGSMITH_ENDPOINT?.trim() || "https://api.smith.langchain.com";
  if (!endpoints.has(endpoint)) throw new Error("LANGSMITH_ENDPOINT must be the official US or EU HTTPS endpoint, without a trailing slash.");
  const project = env.LANGSMITH_PROJECT?.trim() || "commercepilot-learning";
  if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/.test(project)) throw new Error("LANGSMITH_PROJECT must be a short project name using letters, digits, hyphens or underscores.");
  const workspaceId = env.LANGSMITH_WORKSPACE_ID?.trim() || undefined;
  if (workspaceId && !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(workspaceId))
    throw new Error("LANGSMITH_WORKSPACE_ID must be a workspace UUID, or left blank for a workspace-scoped key.");
  return { apiKey, endpoint, project, workspaceId };
}

export async function loadTraceConfig(): Promise<TraceConfig> {
  let file: Record<string, string | undefined> = {};
  try { file = parseEnv(await readFile(new URL("langsmith.env", localDirectory), "utf8")); }
  catch (error) { if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw new Error("Unable to read .local/langsmith.env."); }
  // Do not load arbitrary env-file keys into process.env or enable automatic tracing.
  return validateTraceConfig({ ...process.env, ...file });
}

// These functions are also applied by the SDK immediately before serialization.
function safeOutputs(value: Record<string, unknown>) {
  const result: Record<string, unknown> = {};
  if (["completed", "failed", "cancelled"].includes(String(value.status))) result.status = value.status;
  const usage = value.usage_metadata as Record<string, unknown> | undefined;
  if (usage && tokens(usage.input_tokens) && tokens(usage.output_tokens))
    result.usage_metadata = { input_tokens: usage.input_tokens, output_tokens: usage.output_tokens,
      total_tokens: usage.input_tokens + usage.output_tokens };
  return result;
}

function safeMetadata(value: Record<string, unknown>) {
  return { source: "commercepilot-cli", privacy: "metadata-only",
    ...(value.ls_provider === "ollama" ? { ls_provider: "ollama", ls_model_name: "qwen3.8:27b" } : {}) };
}

/** A bounded observer for this sequential graph. Raw event payloads are never retained. */
export class CatalogTrace {
  private readonly spans: Span[];
  private node?: number;
  private tool?: number;
  private finished = false;
  constructor(private readonly now: () => number = Date.now) {
    this.spans = [{ name: "catalog_agent", parent: -1, start: now() }];
  }

  observe(event: AgentEvent): void {
    if (this.finished) return;
    if (event.type === "node" && (event.name === "model" || event.name === "tools")) {
      if (event.phase === "started") {
        this.node = this.start(event.name, 0);
      } else if (this.node !== undefined && this.spans[this.node]!.name === event.name) {
        const span = this.spans[this.node]!;
        if (event.name === "model" && tokens(event.inputTokens) && tokens(event.outputTokens)) {
          span.inputTokens = event.inputTokens; span.outputTokens = event.outputTokens;
        }
        this.end(this.node, "completed"); this.node = undefined;
      }
    } else if (event.type === "call" && this.node !== undefined && this.spans[this.node]!.name === "tools" &&
        (event.name === "search_products" || event.name === "get_product")) {
      this.tool = this.start(event.name, this.node);
    } else if (event.type === "result" && this.tool !== undefined && event.name === this.spans[this.tool]!.name) {
      this.end(this.tool, event.isError ? "failed" : "completed"); this.tool = undefined;
    }
  }

  private start(name: Span["name"], parent: number): number | undefined {
    if (this.spans.length >= 20) return undefined; // Above the graph's maximum, still bounded if it changes.
    this.spans.push({ name, parent, start: this.now() });
    return this.spans.length - 1;
  }
  private end(index: number, status: Status) {
    this.spans[index]!.end = this.now(); this.spans[index]!.status = status;
  }

  finish(status: Status): void {
    if (this.finished) return;
    this.finished = true;
    this.spans.forEach((span, index) => { if (span.end === undefined) this.end(index, status); });
  }

  private payload(client: Client, project: string) {
    if (!this.finished) throw new Error("Finish the trace before exporting it.");
    const trees: RunTree[] = [];
    return this.spans.map(span => {
      const metadata = safeMetadata(span.name === "model" ? { ls_provider: "ollama" } : {});
      const config = { name: span.name, start_time: span.start, end_time: span.end,
        run_type: span.name === "model" ? "llm" : span.name.endsWith("products") || span.name === "get_product" ? "tool" : "chain",
        client, project_name: project, replicas: [], inputs: {}, metadata };
      const tree = span.parent < 0 ? new RunTree(config) : trees[span.parent]!.createChild(config);
      trees.push(tree);
      const outputs = safeOutputs({ status: span.status,
        usage_metadata: span.inputTokens === undefined ? undefined : { input_tokens: span.inputTokens, output_tokens: span.outputTokens } });
      // Select fields explicitly: RunTree.toJSON() would also add SDK runtime metadata.
      return { id: tree.id, trace_id: tree.trace_id, parent_run_id: tree.parent_run_id ?? tree.parent_run?.id,
        dotted_order: tree.dotted_order, name: span.name, run_type: tree.run_type,
        start_time: new Date(span.start).toISOString(), end_time: new Date(span.end!).toISOString(),
        session_name: project, inputs: {}, outputs, extra: { metadata },
        ...(span.status === "failed" ? { error: "Operation failed; details retained locally." } :
          span.status === "cancelled" ? { error: "Operation cancelled." } : {}) };
    });
  }

  async preview(): Promise<string> {
    const client = new Client({ apiKey: "local-preview", apiUrl: "https://api.smith.langchain.com",
      autoBatchTracing: false, omitTracedRuntimeInfo: true, debug: false, disablePromptCache: true });
    const path = new URL("last-agent-trace.json", localDirectory);
    await mkdir(localDirectory, { recursive: true });
    await writeFile(path, JSON.stringify({ post: this.payload(client, "commercepilot-learning") }, null, 2) + "\n", "utf8");
    return ".local/last-agent-trace.json";
  }

  async upload(config: TraceConfig, transport: typeof fetch = fetch): Promise<{ ok: boolean; traceId?: string }> {
    try {
      // One awaited batch after the answer. No background queue, redirects, or automatic retries.
      const deadline = AbortSignal.timeout(5000);
      const client = new Client({ apiKey: config.apiKey, apiUrl: config.endpoint, workspaceId: config.workspaceId ?? "",
        autoBatchTracing: false, tracingSamplingRate: 1, tracingMode: "langsmith", timeout_ms: 5000,
        callerOptions: { maxRetries: 0, maxConcurrency: 1 }, debug: false, disablePromptCache: true,
        omitTracedRuntimeInfo: true, hideInputs: () => ({}), hideOutputs: safeOutputs, hideMetadata: safeMetadata,
        fetchImplementation: (input, init) => transport(input, { ...init, redirect: "error",
          signal: AbortSignal.any([deadline, ...(init?.signal ? [init.signal] : [])]) }) });
      const runCreates = this.payload(client, config.project);
      await client.batchIngestRuns({ runCreates });
      return { ok: true, traceId: runCreates[0]!.id };
    } catch { return { ok: false }; } // Never print provider errors, response bodies or credentials.
  }
}
