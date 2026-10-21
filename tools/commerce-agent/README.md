# CommercePilot TypeScript client

A native Node.js client for the existing CommercePilot MCP server, using the official TypeScript MCP SDK and Streamable HTTP. It discovers public catalog tools and calls `search_products` with explicit arguments. It does not use a language model, shopper credentials or a database connection.

## Run

Requirements: Node.js 24 or later, npm, and the CommercePilot stack started with the repository's `scripts/start.ps1`. MCP readiness is available at `http://127.0.0.1:5081/health/ready`.

From the repository root:

```powershell
npm.cmd --prefix tools/commerce-agent ci
npm.cmd --prefix tools/commerce-agent run mcp -- --list
npm.cmd --prefix tools/commerce-agent run mcp -- "desk"
```

`npm.cmd` avoids PowerShell's script execution policy for `npm.ps1`. On other platforms, use `npm`.

With no arguments, the search defaults to `desk`. Search text must contain 1–100 characters. `--list` discovers tools without calling one; `--help` works without a running server.

The search asks for up to five in-stock products ordered by ascending price. Output includes the advertised input schemas, exact call arguments, and the MCP result envelope. `structuredContent` contains the catalog data; `content` can also contain a text representation of that data. These are representations of the same result, not two tool executions. An empty result is a successful search with no matches.

The client is anonymous, so the current server exposes `search_products` and `get_product`. Private cart and order tools require shopper authorization and are not available to this command. Requests have a shared 30-second deadline; failures exit with a nonzero status and connections close on success or failure.

## Code

- `src/mcp.ts` constructs the SDK client and HTTP transport, negotiates a compatible protocol, and connects to the local MCP endpoint.
- `src/index.ts` handles CLI input, calls `tools/list`, then `tools/call`, and prints the result without a model-generated interpretation.
- Business rules and catalog queries still run in the existing .NET MCP server and shared application services.

```text
CLI input -> TypeScript MCP client -> .NET MCP server -> catalog service -> PostgreSQL
                     <- MCP result containing actual catalog data <-
```

## Check

```powershell
npm.cmd --prefix tools/commerce-agent run typecheck
npm.cmd --prefix tools/commerce-agent audit
```

SDK reference: [official TypeScript client documentation](https://ts.sdk.modelcontextprotocol.io/v2/clients/connect).
