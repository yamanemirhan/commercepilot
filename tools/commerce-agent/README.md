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

Each `ask` command starts fresh: messages and results stay in RAM only for that request. The separate `chat` commands below add conversation checkpoints. There is no RAG, authenticated shopping or web-chat integration in this client yet. Automatic LangSmith tracing is explicitly disabled before model initialization; the optional exporter below sends only selected operational metadata. The existing storefront continues using its .NET chat implementation.

The graph runs `START → model → tools → model → END`; a direct reply takes `model → END`. The shared state contains messages, the round count, retrieved product IDs, failed-call fingerprints and the final answer. `MessagesValue` merges new messages into the existing history; nodes return updates instead of mutating shared state. Conditional routing sends only validated calls to the tools node. `[Graph: model]` and `[Graph: tools]` report node starts, completion and elapsed milliseconds. A recursion limit backs up the explicit model/tool budgets.

Execution is limited to 2,000 question characters, four model rounds, two proposed calls per round and a three-minute deadline. Input schemas are validated without coercing arguments; unknown fields, ungrounded product IDs and searches exceeding five results are rejected. Tool errors are passed back as errors, repeated failed calls are blocked, and connection failures stop the command. Hidden reasoning is not printed or included in the next model round. Model settings: 8K context, 2,048 generated tokens including thinking, temperature 0, seed 42, no automatic downloads or model retries. Context limits fail explicitly rather than silently truncating tool history.

The automated tests exercise graph routing, fresh-run isolation and tool safety with controlled model replies. Running the commands above separately exercises the actual local model and MCP service; model wording may vary.

## Optional LangSmith tracing

The normal `ask` command never exports traces, even when LangSmith credentials or automatic-tracing environment variables are present. First inspect a local preview, without an account or upload:

```powershell
npm.cmd --prefix tools/commerce-agent run ask:preview -- 'Show two in-stock Audio products under $100, cheapest first.'
```

This saves `.local/last-agent-trace.json`, excluded from Git. It shows the `catalog_agent` root, `model` and `tools` nodes, and individual MCP tool spans nested under `tools`. Model spans include input/output token counts; output tokens include thinking. Only fixed span names, generated run IDs, timestamps, status, token counts and fixed provider/model metadata are retained. Prompts, final replies, reasoning, arguments, tool results, product/user identifiers, cookies, environment metadata and raw exception messages are excluded before serialization. This deliberately prevents replaying conversations or inspecting their text in LangSmith.

To enable uploads for an individual command:

1. Create a [LangSmith account and a **Tracing Only** API key](https://docs.langchain.com/langsmith/create-account-api-key) scoped to your workspace. A tracing key can submit traces but cannot read stored traces or administer the account.
2. Copy `tools/commerce-agent/langsmith.env.example` to `.local/langsmith.env` at the repository root. Set the key there; never commit it. Set `LANGSMITH_ENDPOINT` to your account's US or EU region and, if needed, `LANGSMITH_WORKSPACE_ID`. The default project is `commercepilot-learning`.
3. Run the command below. Its first successful upload creates the project if the key permits project creation. In LangSmith, open that project and inspect the trace ID printed by the command.

```powershell
npm.cmd --prefix tools/commerce-agent run ask:trace -- 'Show two in-stock Audio products under $100, cheapest first.'
```

The file overrides matching shell variables without populating `process.env`. It never turns on automatic tracing. Only the official US/EU HTTPS endpoints are accepted, and redirects are rejected. `src/tracing.ts` constructs explicit `RunTree` relationships and submits one awaited SDK batch **after** execution, including completed error/cancellation spans. Traces are not live during inference, and a forced process termination can lose the trace. There is a five-second upload deadline, no automatic retry and no disk retry queue. A failed upload prints a fixed warning and preserves the catalog answer; it may still have reached the server if the response was lost. The preview file is overwritten only by `ask:preview`.

As checked on October 22, 2026, the [Developer plan](https://www.langchain.com/pricing) includes one seat and up to 5,000 base traces per month. [Personal organizations without a payment card stop at the free trace limit](https://docs.langchain.com/langsmith/billing); adding a card enables billable excess usage. No paid subscription, cloud model or deployment is required here. Account-wide usage and current provider terms remain authoritative.

References: [custom instrumentation](https://docs.langchain.com/langsmith/annotate-code), [data masking](https://docs.langchain.com/langsmith/mask-inputs-outputs), [LLM token metadata](https://docs.langchain.com/langsmith/log-llm-trace).

## Conversations with RAM or PostgreSQL memory

These commands are a **local developer CLI**, using the existing seeded Alex/Sam accounts. They read demo credentials from the ignored root `.env`, sign in through the API with CSRF protection, and keep the resulting cookies in process memory. Credentials are never CLI arguments, model messages, graph configuration or checkpoints. The API verifies the session again before every conversation operation; the owner ID comes from that response, not the model or a supplied account ID. The CLI needs direct database access for the PostgreSQL option; it must not be shipped to shoppers or exposed as a public service.

```powershell
# RAM: closing the command loses every conversation in this process.
npm.cmd --prefix tools/commerce-agent run chat:ram -- alex

# PostgreSQL: set up the separate commerce_agent schema once, then start a chat.
npm.cmd --prefix tools/commerce-agent run memory:setup
npm.cmd --prefix tools/commerce-agent run chat:postgres -- alex
```

Try these messages in the same conversation:

```text
Show two in-stock Audio products under $100, cheapest first.
Tell me more about the second one, including its current stock.
```

The graph loads previous user/assistant messages and paired tool results using a stable `thread_id`. The model resolves references from that context and uses `get_product` to refresh facts. Checkpointing does not guarantee correct reference resolution; an ambiguous or forgotten reference should produce a clarification question. MCP still performs public catalog operations; authentication here protects conversation ownership and does not grant cart/order tools.

Commands inside the chat:

| Command | Behavior |
| --- | --- |
| `/new` | Create and select an empty conversation. |
| `/list` | List only the signed-in account's unexpired conversation IDs and status. |
| `/use <id>` | Select one of those conversations; PostgreSQL can resume it after a process restart. |
| `/delete <id>` | Delete that account's conversation and every checkpoint, blob and pending write. |
| `/exit` | End the CLI and discard login cookies. RAM history is lost; PostgreSQL history remains. |

Use `sam` instead of `alex` in a separate command to verify account isolation. Sam cannot select, continue or delete Alex's conversation even with its ID. Ownership checks run before checkpoint access, and PostgreSQL advisory locks prevent overlapping turns/deletes across CLI processes. A failed or interrupted turn is blocked rather than silently replayed; start a new conversation or delete the failed one. Public read tools are the only tools enabled in this checkpoint.

Limits are explicit: up to **four complete prior turns and 8,000 serialized UTF-8 bytes** of retained history, 20 unexpired conversations per account (20 total in a RAM process), 30 attempted turns per conversation and a fixed 24-hour lifetime. Oldest complete turns are removed together, preserving assistant/tool-call pairing. The system instruction is regenerated once per request; model rounds, failed-call fingerprints and the previous answer reset each turn. Product-ID authority is recomputed from retained sources. The existing 16 KB model-input guard and 8K model context still apply; a single oversized exchange fails instead of keeping broken tool history. Forgotten information is not stored in a hidden summary.

`MemorySaver` and `PostgresSaver` save graph checkpoints, including intermediate states. PostgreSQL uses synchronous writes so a completed turn can be loaded after restart. The latest context is bounded, but older checkpoint versions remain until deletion/expiry cleanup; the 30-turn limit also bounds versions per conversation. Checkpoints contain conversation text and public tool results, so protect the local database and its backups. The CLI disables cloud tracing; `ask:trace` remains a separate opt-in command.

Expired conversations are immediately inaccessible. PostgreSQL chat startup removes up to 100 expired conversations; physical cleanup while the CLI is closed requires the maintenance command below. A future hosted service must schedule it regularly and use a restricted database role. It does not delete existing storefront chat tables.

```powershell
npm.cmd --prefix tools/commerce-agent run memory:prune
# Local API and PostgreSQL required; test schemas are isolated and removed afterward.
npm.cmd --prefix tools/commerce-agent run test:memory
```

Read `src/conversation-context.ts` for whole-turn trimming, `src/catalog-agent.ts` for checkpoint loading/resetting, `src/conversations.ts` for authenticated access and the RAM implementation, `src/postgres-memory.ts` for ownership/locking/retention, and `src/shopper-session.ts` for API authentication. [Official LangGraph memory documentation](https://docs.langchain.com/oss/javascript/langgraph/add-memory).
