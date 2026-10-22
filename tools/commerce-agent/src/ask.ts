// Ambient SDK tracing must never export raw graph state. --trace uses our explicit allowlist.
process.env.LANGSMITH_TRACING = "false";
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGCHAIN_TRACING = "false";

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === "--help") {
    console.log('Usage: npm run ask -- "Show two in-stock Audio products under $100, cheapest first."');
    console.log("Requires the local MCP server and Ollama with qwen3.8:27b. One question per command; no saved conversation.");
    console.log("Optional: --trace-preview saves metadata locally; --trace sends metadata to LangSmith using .local/langsmith.env.");
    return;
  }
  const mode = args[0] === "--trace" ? "langsmith" : args[0] === "--trace-preview" ? "preview" : "off";
  const questionArgs = mode === "off" ? args : args.slice(1);
  const question = questionArgs.join(" ").trim();
  if (!question || question.length > 2000 || questionArgs.some(arg => arg.startsWith("--"))) {
    throw new Error("Supply a question of 1–2000 characters, or use --help.");
  }

  const [{ connectMcp }, { createCatalogModel, modelName }, { runCatalogAgent }, { CatalogTrace, loadTraceConfig }] = await Promise.all([
    import("./mcp.js"), import("./model.js"), import("./catalog-agent.js"), import("./tracing.js"),
  ]);
  const config = mode === "langsmith" ? await loadTraceConfig() : undefined;
  const trace = mode === "off" ? undefined : new CatalogTrace();
  const signal = AbortSignal.timeout(180_000);
  let client: Awaited<ReturnType<typeof connectMcp>> | undefined;
  let status: "completed" | "failed" | "cancelled" = "failed";
  try {
    client = await connectMcp(signal);
    const model = createCatalogModel(signal);
    console.log(`Local model: ${modelName}. Public catalog tools only. Trace mode: ${mode}.`);
    const answer = await runCatalogAgent(question, client, tools => model.bindTools(tools), signal, event => {
      trace?.observe(event);
      if (event.type === "node") console.log(`[Graph: ${event.name}] ${event.phase}${event.elapsedMs === undefined ? "" : ` (${event.elapsedMs} ms)`}`);
      else if (event.type === "model") console.log(`\n[Model round ${event.round}]`);
      else if (event.type === "call") console.log(`\n[Model-selected tool] ${event.name}\n${JSON.stringify(event.arguments, null, 2)}`);
      else console.log(`\n[MCP ${event.isError ? "error" : "result"}] ${event.name}\n${JSON.stringify(event.data, null, 2)}`);
    });
    status = "completed";
    console.log(`\n[Answer]\n${answer}`);
  } catch (error) {
    status = signal.aborted ? "cancelled" : "failed";
    throw error;
  } finally {
    trace?.finish(status);
    try { await client?.close(); }
    finally {
      if (trace && mode === "preview") {
        try { console.log(`[Trace preview] ${await trace.preview()} (no upload)`); }
        catch { console.warn("Trace preview could not be saved. The catalog result is unchanged."); }
      } else if (trace && config) {
        const result = await trace.upload(config);
        if (result.ok) console.log(`[LangSmith] Metadata accepted. Project: ${config.project}; trace ID: ${result.traceId}`);
        else console.warn("LangSmith export failed. Check the key, region, workspace and quota. The catalog result is unchanged; no automatic retry.");
      }
    }
  }
}

main().catch((error: unknown) => {
  console.error(`Catalog assistant failed: ${error instanceof Error ? error.message : "Unknown error"}`);
  console.error("Check MCP on port 5081 and Ollama on port 11434. Use --help for usage.");
  process.exitCode = 1;
});
