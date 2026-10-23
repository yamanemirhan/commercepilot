import { Client, StreamableHTTPClientTransport } from "@modelcontextprotocol/client";

export const mcpEndpoint = new URL("http://127.0.0.1:5081/mcp");

export async function connectMcp(signal: AbortSignal, accessToken?: string): Promise<Client> {
  if (accessToken !== undefined && (!accessToken || /\s/.test(accessToken))) throw new Error("Invalid MCP credential.");
  const client = new Client(
    { name: "commercepilot-agent", version: "0.1.0" },
    { versionNegotiation: { mode: "auto" } },
  );
  const transport = new StreamableHTTPClientTransport(accessToken ? new URL("authorized", `${mcpEndpoint}/`) : mcpEndpoint, {
    requestInit: { redirect: "error", ...(accessToken ? { headers: { Authorization: `Bearer ${accessToken}` } } : {}) },
  });

  try {
    // The SDK handles MCP negotiation and JSON-RPC over HTTP.
    // Credentials belong only to HTTP transport; never to model messages or graph state.
    await client.connect(transport, { signal });
    return client;
  } catch (error) {
    await client.close();
    throw error;
  }
}
