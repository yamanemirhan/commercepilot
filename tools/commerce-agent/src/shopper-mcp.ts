import type { Client, Tool } from "@modelcontextprotocol/client";
import { connectMcp } from "./mcp.js";
import type { ShopperSession, McpGrant } from "./shopper-session.js";
import { catalogNames, shoppingReadNames, cartWriteNames } from "./shopping-tools.js";

type Connection = Pick<Client, "listTools" | "callTool" | "close">;
type Connector = (signal: AbortSignal, token?: string) => Promise<Connection>;

// One connection per grant: cart and order credentials are never combined or exposed to LangGraph.
export async function connectShopperMcp(session: Pick<ShopperSession, "owner" | "issueMcpToken">,
  signal: AbortSignal, cartWrite = false, connect: Connector = connectMcp): Promise<Connection> {
  const owner = await session.owner();
  const clients: Connection[] = [];
  const routes = new Map<string, Connection>();
  const definitions: Tool[] = [];
  const close = async () => { await Promise.allSettled(clients.map(client => client.close())); routes.clear(); };
  try {
    const groups: { names: string[]; grant?: McpGrant }[] = [
      { names: catalogNames },
      { names: ["get_cart", ...(cartWrite ? cartWriteNames : [])], grant: cartWrite ? "cart:write" : "cart:read" },
      { names: shoppingReadNames.filter(name => name !== "get_cart"), grant: "orders:read" },
    ];
    for (const group of groups) {
      signal.throwIfAborted();
      if (await session.owner() !== owner) throw new Error("Session changed.");
      const client = await connect(signal, group.grant ? await session.issueMcpToken(group.grant) : undefined);
      clients.push(client);
      let cursor: string | undefined;
      let pages = 0;
      do {
        if (++pages > 10) throw new Error("Tool discovery exceeded its limit.");
        const page = await client.listTools(cursor ? { cursor } : {}, { signal });
        for (const tool of page.tools) if (group.names.includes(tool.name)) {
          if (routes.has(tool.name)) throw new Error("Duplicate tool definition.");
          definitions.push(tool); routes.set(tool.name, client);
        }
        cursor = page.nextCursor;
      } while (cursor);
      if (group.names.some(name => !routes.has(name))) throw new Error("Required authorized tools are unavailable.");
    }
    let mutationAttempted = false;
    return {
      listTools: async () => ({ tools: definitions }),
      callTool: async (request, options) => {
        signal.throwIfAborted();
        const client = routes.get(request.name);
        if (!client) throw new Error("This tool is not authorized for this turn.");
        if (await session.owner() !== owner) throw new Error("Session changed.");
        signal.throwIfAborted();
        if (cartWriteNames.includes(request.name)) {
          if (mutationAttempted) throw new Error("Only one cart change is allowed per turn. Read the cart before trying again.");
          mutationAttempted = true; // Reserve before I/O: a timeout must never repeat a non-idempotent add.
        }
        try { return await client.callTool(request, { ...options, signal }); }
        catch { throw new Error("MCP request failed; no automatic retry. If this was a cart change, read your cart to verify its outcome."); }
      },
      close,
    };
  } catch {
    await close();
    throw new Error("Authorized MCP connection failed. Check your session and service availability; no anonymous fallback.");
  }
}
