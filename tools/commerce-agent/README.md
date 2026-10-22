# CommercePilot TypeScript catalog assistant

A native Node.js client for the existing CommercePilot MCP server, using the official TypeScript MCP SDK and Streamable HTTP. The `mcp` command discovers tools and searches with explicit arguments. The `ask` command uses LangChain, LangGraph and local Ollama to select public catalog tools and answer from their results. Neither command uses shopper credentials or a database connection.

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
- `src/model.ts` configures LangChain's `ChatOllama` with the local Qwen model.
- `src/catalog-agent.ts` defines graph state, model/tool nodes and conditional routing. It binds discovered public tool schemas, validates proposed calls, executes them through MCP and feeds matching `ToolMessage` results back to the model.
- `src/ask.ts` runs one natural-language question and prints tool calls, results and the final answer.
- Business rules and catalog queries still run in the existing .NET MCP server and shared application services.

```text
CLI input -> TypeScript MCP client -> .NET MCP server -> catalog service -> PostgreSQL
                     <- MCP result containing actual catalog data <-
```

## Check

```powershell
npm.cmd --prefix tools/commerce-agent run typecheck
npm.cmd --prefix tools/commerce-agent test
npm.cmd --prefix tools/commerce-agent audit
```

SDK reference: [official TypeScript client documentation](https://ts.sdk.modelcontextprotocol.io/v2/clients/connect).

## Ask the local model

Start Ollama on the host with `qwen3.8:27b` already downloaded (see the root README for GPU requirements). From the repository root:

```powershell
npm.cmd --prefix tools/commerce-agent run ask -- 'Show two in-stock Audio products under $100, cheapest first.'
npm.cmd --prefix tools/commerce-agent run ask -- 'Find the cheapest in-stock desk light, then get its full product details.'
```

Use single quotes in PowerShell so `$100` reaches the model literally. The command prints `[Model-selected tool]`, `[MCP result]` and `[Answer]`. Tool selection and the final English answer come from the model; catalog facts come from the real MCP server. Only `search_products` and `get_product` are allowed, even if the server advertises other tools.

Each command starts fresh: messages and results stay in RAM only for that request. There is no checkpointer, cross-command memory, RAG, authenticated shopping or web-chat integration in this client. LangSmith tracing is explicitly disabled before model initialization. The existing storefront continues using its .NET chat implementation.

The graph runs `START → model → tools → model → END`; a direct reply takes `model → END`. The shared state contains messages, the round count, retrieved product IDs, failed-call fingerprints and the final answer. `MessagesValue` merges new messages into the existing history; nodes return updates instead of mutating shared state. Conditional routing sends only validated calls to the tools node. `[Graph: model]` and `[Graph: tools]` report node starts, completion and elapsed milliseconds. A recursion limit backs up the explicit model/tool budgets.

Execution is limited to 2,000 question characters, four model rounds, two proposed calls per round and a three-minute deadline. Input schemas are validated without coercing arguments; unknown fields, ungrounded product IDs and searches exceeding five results are rejected. Tool errors are passed back as errors, repeated failed calls are blocked, and connection failures stop the command. Hidden reasoning is not printed or included in the next model round. Model settings: 8K context, 2,048 generated tokens including thinking, temperature 0, seed 42, no automatic downloads or model retries. Context limits fail explicitly rather than silently truncating tool history.

The automated tests exercise graph routing, fresh-run isolation and tool safety with controlled model replies. Running the commands above separately exercises the actual local model and MCP service; model wording may vary.
