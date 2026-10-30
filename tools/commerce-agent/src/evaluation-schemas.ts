import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import { canonical, loadSuite, root } from "./evaluation-core.js";
import { connectMcp } from "./mcp.js";
import { ShopperSession } from "./shopper-session.js";

// Read-only discovery through each existing scoped connection. Never execute a
// tool or overwrite the reviewed fixture simply because the server changed.
const session = new ShopperSession();
try {
  const env = parseEnv(await readFile(new URL(".env",root),"utf8"));
  const suite = await loadSuite(), signal = AbortSignal.timeout(60_000);
  await session.login("alex@commercepilot.local",env.DEMO_ALEX_PASSWORD ?? "");
  const found = new Map<string,unknown>();
  for (const grant of [undefined,"cart:write","orders:read","checkout:prepare","cancellation:prepare","refund:prepare"] as const) {
    const client = await connectMcp(signal,grant ? await session.issueMcpToken(grant) : undefined);
    try {
      const { tools, nextCursor } = await client.listTools({}, {signal});
      if (nextCursor) throw new Error("Unexpected catalog pagination.");
      for (const tool of tools) {
        const definition = {name:tool.name,description:tool.description ?? "",parameters:tool.inputSchema};
        if (found.has(tool.name) && canonical(found.get(tool.name)) !== canonical(definition))
          throw new Error("Conflicting discovered definitions.");
        found.set(tool.name,definition);
      }
    } finally { await client.close(); }
  }
  const changed = suite.tools.filter(tool => canonical(found.get(tool.name) ?? null) !== canonical(tool));
  if (changed.length || found.size !== suite.tools.length) {
    console.error(`Schema drift: ${changed.map(t => t.name).join(", ") || "catalog size changed"}. Review the fixture; do not overwrite blindly.`);
    process.exitCode = 1;
  } else console.log("All 12 frozen tool definitions match live, scoped MCP discovery. No tools executed.");
} catch {
  console.error("Schema verification failed. Check the local services and demo credentials; no fixture was changed.");
  process.exitCode = 2;
} finally { await session.logout().catch(() => { console.error("Local sign-out could not be confirmed."); process.exitCode = 2; }); }
