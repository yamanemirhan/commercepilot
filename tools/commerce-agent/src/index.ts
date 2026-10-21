import { connectMcp, mcpEndpoint } from "./mcp.js";

const printJson = (value: unknown) => console.log(JSON.stringify(value, null, 2));

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === "--help") {
    console.log('Usage: npm run mcp -- [search text | --list | --help]');
    console.log('Examples: npm run mcp -- "desk" | npm run mcp -- --list');
    console.log('Default search: desk. Uses the local public MCP endpoint; no login or model required.');
    return;
  }

  const listOnly = args.length === 1 && args[0] === "--list";
  if (!listOnly && args.some((arg) => arg.startsWith("--"))) {
    throw new Error("Unknown option. Use --help for usage.");
  }
  const search = args.length === 0 ? "desk" : args.join(" ").trim();
  if (!listOnly && (search.length === 0 || search.length > 100)) {
    throw new Error("Search must contain between 1 and 100 characters.");
  }

  // One deadline covers connection, discovery and the tool call.
  const signal = AbortSignal.timeout(30_000);
  const client = await connectMcp(signal);
  try {
    console.log(`Connected to ${mcpEndpoint}`);
    console.log("\n1. tools/list: the server's available tools and input schemas.");
    let cursor: string | undefined;
    let searchAvailable = false;
    do {
      const result = await client.listTools(cursor ? { cursor } : {}, { signal });
      for (const tool of result.tools) {
        console.log(`\n${tool.name}: ${tool.description ?? ""}`);
        printJson(tool.inputSchema);
        if (tool.name === "search_products") searchAvailable = true;
      }
      cursor = result.nextCursor;
    } while (cursor);

    if (listOnly) return;
    if (!searchAvailable) throw new Error("The server did not advertise search_products.");

    // These arguments are chosen by this program, not by an AI model.
    const request = {
      name: "search_products",
      arguments: { search, sort: "price-asc", page: 1, pageSize: 5, inStock: true },
    };
    console.log("\n2. tools/call: the exact tool name and arguments being sent.");
    printJson(request);
    const result = await client.callTool(request, { signal });

    console.log("\n3. MCP result: structured data and content returned by the server.");
    printJson(result);
    if (result.isError) {
      console.error("The tool reported an error. No automatic retry was made.");
      process.exitCode = 1;
    } else {
      console.log("\nCatalog read completed. No cart, stock or order was changed.");
    }
  } finally {
    await client.close();
  }
}

main().catch((error: unknown) => {
  console.error(`MCP client failed: ${error instanceof Error ? error.message : "Unknown error"}`);
  console.error("Check that the MCP service is ready at http://127.0.0.1:5081/health/ready. Use --help for usage.");
  process.exitCode = 1;
});
