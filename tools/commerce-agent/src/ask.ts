// Tracing is introduced in a later checkpoint, after payload filtering is configured.
process.env.LANGSMITH_TRACING = "false";
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGCHAIN_TRACING = "false";

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === "--help") {
    console.log('Usage: npm run ask -- "Show two in-stock Audio products under $100, cheapest first."');
    console.log("Requires the local MCP server and Ollama with qwen3.8:27b. One question per command; no saved conversation.");
    return;
  }
  const question = args.join(" ").trim();
  if (!question || question.length > 2000 || args.some(arg => arg.startsWith("--"))) {
    throw new Error("Supply a question of 1–2000 characters, or use --help.");
  }

  const [{ connectMcp }, { createCatalogModel, modelName }, { runCatalogAgent }] = await Promise.all([
    import("./mcp.js"), import("./model.js"), import("./catalog-agent.js"),
  ]);
  const signal = AbortSignal.timeout(180_000);
  const client = await connectMcp(signal);
  try {
    const model = createCatalogModel(signal);
    console.log(`Local model: ${modelName}. Public catalog tools only. Tracing disabled.`);
    const answer = await runCatalogAgent(question, client, tools => model.bindTools(tools), signal, event => {
      if (event.type === "model") console.log(`\n[Model round ${event.round}]`);
      else if (event.type === "call") console.log(`\n[Model-selected tool] ${event.name}\n${JSON.stringify(event.arguments, null, 2)}`);
      else console.log(`\n[MCP ${event.isError ? "error" : "result"}] ${event.name}\n${JSON.stringify(event.data, null, 2)}`);
    });
    console.log(`\n[Answer]\n${answer}`);
  } finally {
    await client.close();
  }
}

main().catch((error: unknown) => {
  console.error(`Catalog assistant failed: ${error instanceof Error ? error.message : "Unknown error"}`);
  console.error("Check MCP on port 5081 and Ollama on port 11434. Use --help for usage.");
  process.exitCode = 1;
});
