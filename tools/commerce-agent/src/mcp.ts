import { Client, StreamableHTTPClientTransport } from "@modelcontextprotocol/client";

export const mcpEndpoint = new URL("http://127.0.0.1:5081/mcp");

export async function connectMcp(signal: AbortSignal): Promise<Client> {
  const client = new Client(
    { name: "commercepilot-agent", version: "0.1.0" },
    { versionNegotiation: { mode: "auto" } },
  );
  const transport = new StreamableHTTPClientTransport(mcpEndpoint, {
    requestInit: { redirect: "error" },
  });

  try {
    // The SDK handles MCP negotiation and JSON-RPC over HTTP.
    // No bearer token means only public catalog tools are available.
    await client.connect(transport, { signal });
    return client;
  } catch (error) {
    await client.close();
    throw error;
  }
}
