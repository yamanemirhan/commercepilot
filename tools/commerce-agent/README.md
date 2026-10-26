# CommercePilot TypeScript shopping assistant

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

Each `ask` command starts fresh: messages and results stay in RAM only for that request. The separate `chat` commands below add conversation checkpoints and authenticated cart/order tools. RAG and web-chat integration are not yet connected to this client. Automatic LangSmith tracing is explicitly disabled before model initialization; the optional exporter below sends only selected operational metadata. The existing storefront continues using its .NET chat implementation.

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

The graph loads previous user/assistant messages and paired tool results using a stable `thread_id`. The model resolves references from that context and uses live tools to refresh facts. Checkpointing does not guarantee correct reference resolution; an ambiguous or forgotten reference should produce a clarification question. Conversation ownership and MCP permissions are separate checks: the API authenticates the shopper, and scoped MCP credentials authorize the tools described below.

Commands inside the chat:

| Command | Behavior |
| --- | --- |
| `/new` | Create and select an empty conversation. |
| `/list` | List only the signed-in account's unexpired conversation IDs and status. |
| `/use <id>` | Select one of those conversations; PostgreSQL can resume it after a process restart. |
| `/delete <id>` | Delete that account's conversation and every checkpoint, blob and pending write. |
| `/cart-edit <request>` | Enable at most one cart change for this message, such as `/cart-edit Add one of the second product to my cart.` |
| `/exit` | End the CLI and discard login cookies. RAM history is lost; PostgreSQL history remains. |

Use `sam` instead of `alex` in a separate command to verify account isolation. Sam cannot select, continue or delete Alex's conversation even with its ID. Ownership checks run before checkpoint access, and PostgreSQL advisory locks prevent overlapping turns/deletes across CLI processes. A failed or interrupted turn is blocked rather than silently replayed; start a new conversation and read the current cart before deciding whether to retry an edit. A failed answer or lost response does not prove that the cart operation failed.

Limits are explicit: up to **four complete prior turns and 8,000 serialized UTF-8 bytes** of retained history, 20 unexpired conversations per account (20 total in a RAM process), 30 attempted turns per conversation and a fixed 24-hour lifetime. Oldest complete turns are removed together, preserving assistant/tool-call pairing. The system instruction is regenerated once per request; model rounds, failed-call fingerprints and the previous answer reset each turn. Product-ID authority is recomputed from retained sources. The existing 16 KB model-input guard and 8K model context still apply; a single oversized exchange fails instead of keeping broken tool history. Forgotten information is not stored in a hidden summary.

`MemorySaver` and `PostgresSaver` save graph checkpoints, including intermediate states. PostgreSQL uses synchronous writes so a completed turn can be loaded after restart. The latest context is bounded, but older checkpoint versions remain until deletion/expiry cleanup; the 30-turn limit also bounds versions per conversation. Checkpoints contain conversation text and selected catalog/cart/order data, so protect the local database and its backups. The CLI disables cloud tracing; `ask:trace` remains a separate opt-in command.

Expired conversations are immediately inaccessible. PostgreSQL chat startup removes up to 100 expired conversations; physical cleanup while the CLI is closed requires the maintenance command below. A future hosted service must schedule it regularly and use a restricted database role. It does not delete existing storefront chat tables.

```powershell
npm.cmd --prefix tools/commerce-agent run memory:prune
# Local API and PostgreSQL required; test schemas are isolated and removed afterward.
npm.cmd --prefix tools/commerce-agent run test:memory
```

Read `src/conversation-context.ts` for whole-turn trimming, `src/catalog-agent.ts` for checkpoint loading/resetting, `src/conversations.ts` for authenticated access and the RAM implementation, `src/postgres-memory.ts` for ownership/locking/retention, and `src/shopper-session.ts` for API authentication. [Official LangGraph memory documentation](https://docs.langchain.com/oss/javascript/langgraph/add-memory).

## Authenticated cart and order tools

Normal `chat:ram` and `chat:postgres` messages can search products, read the signed-in shopper's cart, list their recent orders, and retrieve an order's status. `ask` remains an anonymous catalog-only command. Try `Show my cart.`, `Show my recent orders.`, and `What is the status of the first order?`.

| Tool group | API credential endpoint | Scope | Exposed tools |
| --- | --- | --- | --- |
| Catalog | None | Public | `search_products`, `get_product` |
| Cart reading | `/api/auth/mcp-token` | `cart:read` | `get_cart` |
| Explicit cart edit | `/api/auth/mcp-cart-token` | `cart:read cart:write` | `get_cart`, `add_to_cart`, `update_cart_item`, `remove_from_cart` |
| Order reading | `/api/auth/mcp-orders-token` | `orders:read` | `get_my_orders`, `get_order_status` |

The local CLI enables cart writes **only for a message beginning with `/cart-edit `**. The model cannot enable this permission by suggesting a tool, quoting an earlier request or receiving instructions inside tool data. It sees only the allowlisted schemas for the current turn. Cart edits add units, set an exact quantity or remove a line; they do not reserve inventory, place orders or charge money. Checkout, cancellation/refund preparation and confirmation are not model tools; explicit human-review commands are described below.

`src/shopper-session.ts` obtains short-lived first-party credentials through cookie authentication and CSRF-protected POSTs. It checks the returned scope and expiry, caches credentials only in RAM, and clears them with the session. A cached grant is reused only when at least 185 seconds remain for the three-minute turn. This uses the existing local credential mechanism; external OAuth/PKCE behavior is unchanged.

`src/shopper-mcp.ts` opens separate public, cart and order connections; `src/mcp.ts` puts each bearer credential in its own HTTP transport, using `/mcp/authorized` for private access. The dispatcher checks that the account has not changed before each call. It never puts credentials or identity into model messages, tool arguments, graph state or LangSmith traces. Redirects, automatic anonymous fallback and application-level call retries are disabled. Credential errors stop the operation; they do not broaden permissions.

`src/catalog-agent.ts` rejects extra arguments and ungrounded IDs before execution. A cart-edit turn starts with a deterministic `get_cart` call before the first model invocation; update/remove IDs must appear in that fresh result. This avoids relying on the model to remember a prerequisite read. Both the graph and dispatcher allow at most one attempted cart mutation per turn; the dispatcher reserves that attempt before network I/O so a timeout cannot trigger a duplicate add. This is not exactly-once delivery across distinct user requests: after an uncertain outcome, inspect the cart before issuing another edit. The .NET services remain authoritative for ownership, stock, prices and quantities.

`src/shopping-tools.ts` projects private results to selected fields, excluding addresses, owner IDs and transport metadata. Results retain at most ten cart/order items, five recent orders and five latest timeline events, with explicit flags when more data exists. Raw private-tool error text is replaced with a fixed failure message. These projections keep context bounded without hiding that a displayed list is partial.

```powershell
npm.cmd --prefix tools/commerce-agent run test:shopping
```

This opt-in integration test needs the local stack. It verifies server-side scope enforcement, own-cart add/update/remove, unchanged Sam data and rejection of Alex's order ID by Sam. It creates one previously absent cart line and one cancelled synthetic order, then removes exactly those fixtures. It performs no checkout, payment or stock transition. Unit tests additionally cover extra-argument rejection, per-turn write permissions, duplicate mutation prevention, grant validation, private-result projection and credential-free checkpoints.

## Reviewed site knowledge

Authenticated `chat:ram` and `chat:postgres` conversations also expose `search_site_knowledge`. This is an **application tool calling the existing .NET retrieval API**, not an MCP server tool. LangChain supplies its schema to the model and LangGraph dispatches the selected call in the tools node. Catalog/cart/order calls continue to use MCP. `ask`, `ask:trace` and `mcp` remain catalog-only.

Try these in one conversation:

```text
Do I have saved addresses in this store?
Which page can I use to see the delivery destination?
Does this store offer a lifetime warranty?
```

The model rewrites follow-ups into self-contained search queries. `ShopperSession.searchKnowledge` calls `POST /api/knowledge/search` with its verified session and CSRF token. The endpoint rejects locked accounts, empty or over-2,000-character queries, applies a 16 KB request-body limit and allows 20 requests per minute per account. Responses are not HTTP-cacheable. No publication endpoint or direct database access is added for retrieval.

The API reuses `SiteKnowledge` and `KnowledgeRetriever`: the same active PostgreSQL/pgvector release, local EmbeddingGemma model, hybrid vector/text search and reviewed-source checks as the storefront. Missing/stale releases, model mismatches and retrieval timeouts return `unavailable`; an empty search returns `no-match`. A new publication is used on the next search without restarting the CLI. The client bounds responses to 32 KB, validates at most two sources with 1,800-character excerpts, and strips undocumented fields.

Source data is an untrusted tool result, never a system instruction or an authorization grant. The model emits `[site:document-id]` citations; the application checks them against the latest retrieval in this turn and renders approved local links. Unknown citations, uncited answers after retrieval and model-generated Markdown/HTML or external links are replaced with a fixed safe response. `no-match` and `unavailable` also produce a fixed refusal instead of displaying unsupported generated claims. For a mixed request that already attempted a cart change, the fallback tells the shopper to read the cart before retrying.

Prior knowledge tool messages are replaced by a stale-evidence marker when building the next turn. Historical answers remain conversation context, but cannot provide current citations. Old checkpoint versions retain their historical snapshots until normal deletion/expiry cleanup. Prompts require a fresh search for site questions and forbid using public documents as personal account data. Retrieval relevance, whether the model chooses to search, and whether a cited passage actually supports every sentence remain model-dependent; citation checks alone do not prove factual accuracy.

Update reviewed documents under `knowledge/documents`, then follow the existing [review, validate, deploy and publish workflow](../../knowledge/README.md). Source code changes require a matching reviewed application build; document-only updates can be published without restarting an already compatible application. Never publish unreviewed site claims. This mechanism handles public site knowledge, not long-term personal conversation memory.

Read `src/site-knowledge.ts` (schema/citations), `src/shopper-session.ts` (authenticated API access), `src/catalog-agent.ts` (tool dispatch/history refresh), and `src/Commerce.Api/KnowledgeEndpoints.cs` at the repository root (retrieval boundary). Cloud tracing remains disabled for the authenticated CLI.

## Human review with durable pause and resume

The developer CLI has a separate LangGraph review workflow: `prepare -> human_review (interrupt) -> verify -> end`. Preparation calls an existing scoped MCP tool. The interrupt node is side-effect free, because LangGraph restarts that node on resume. Confirmation/rejection occurs only in the trusted CLI command handler, through the existing cookie + CSRF HTTP endpoints. `Command({ resume: "refresh" })` only reads the server outcome; resuming a graph cannot place an order. If the server still reports `Pending`, the graph waits again. See [LangGraph interrupt semantics](https://github.com/langchain-ai/docs/blob/main/src/oss/langgraph/interrupts.mdx).

On an existing installation, run the idempotent schema setup once before using persistent reviews:

```powershell
npm.cmd --prefix tools/commerce-agent run memory:setup
npm.cmd --prefix tools/commerce-agent run chat:postgres -- alex
```

| Command | Behavior |
| --- | --- |
| `/review checkout [success\|failure\|delay]` | Prepare a five-minute checkout review; payment outcome defaults to `success`, and all payment/shipping is simulated. |
| `/review cancel <order-id>` | Prepare cancellation for an eligible owned order UUID. |
| `/review refund <order-id>` | Prepare a full demo refund for an eligible owned order UUID. |
| `/review` | Fetch and display the current conversation's exact server terms, including every item, quantity, unit price, stock, total, destination, expiry and snapshot hash. |
| `/approve <review-id>` | Execute exactly the displayed review through the authoritative API. |
| `/reject <review-id>` | Reject exactly the displayed review. |
| `/sync-review` | Read the server status and finish a waiting graph if the review is terminal; never resend a decision. |

A normal message such as `yes` cannot execute or reject anything. Natural-language checkout requests receive command guidance; the model is not given preparation, confirmation or rejection tools. The displayed-review receipt is kept only in this CLI process, so after reconnecting with `/use <conversation-id>`, run `/review` before deciding. Review terms are not summarized by the model or fed into ordinary chat history. The existing storefront still uses its own review cards and buttons; connecting these workflows to the web chat is a separate integration.

Review checkpoints use `review:<conversation-id>` in the same saver as the owning conversation. Owner checks and the existing conversation lock protect display, preparation, decisions, recovery and deletion. A paused graph releases its lock; later operations acquire it again. The conversation list's `ready` status means no active execution, not that its review is completed. Ordinary chat can continue while a review waits, but later cart/order changes may make the review stale. Review preparation counts toward the 30-turn cap; display, decision and read-only recovery do not consume another model turn.

Before a decision HTTP request, `commerce_agent.review_decisions` atomically reserves its action ID. A lost response, process crash or stale response cannot cause the CLI to automatically repeat that request. A reservation deliberately remains even if the request never reached the API: use `/sync-review`, inspect the storefront or let the review expire, then prepare fresh terms. This conservative policy is not a general exactly-once transport guarantee; the server's snapshot checks, owner locks and idempotent completed-action response remain authoritative. `Completed` means the review executed, not that demo payment succeeded.

PostgreSQL restores paused reviews and decision reservations after restart. RAM mode retains both only until that process exits. Terminal/expired reviews cannot execute, and stale snapshots are rejected by the API. Failed preparation is never replayed automatically. A failed read in the verification node can be retried read-only. A failed/crashed ordinary conversation remains blocked for new model turns; review inspection/recovery is still available while the conversation is unexpired. After reviewing the outcome, start a new conversation if needed.

Deleting/expiring a CLI conversation removes its ordinary checkpoints, companion review checkpoints and decision reservations. It does **not** delete or reject the server's commerce review/order; pending reviews expire on their own. Review checkpoints contain exact approval terms, including the demo destination, but no cookies or credentials. This uses the existing 24-hour conversation retention and five-minute review lifetime.

`src/review-contract.ts` validates/prints exact terms, `src/review-client.ts` handles scoped MCP preparation, and `src/review-graph.ts` implements interrupt/resume and the separate decision controller. `src/shopper-session.ts` owns cookie/CSRF HTTP calls. `src/conversations.ts` and `src/postgres-memory.ts` enforce ownership, locking, decision reservations and cleanup.

```powershell
npm.cmd --prefix tools/commerce-agent run test:reviews
```

The opt-in integration suite creates an isolated temporary shopper and checkpoint schema. It verifies reconnect, account isolation, lost-decision-response recovery, rejection, expiry, stale checkout, cancellation and refund. Checkout uses simulated payment failure to preserve stock; eligible change-order fixtures have no scheduled worker transitions. Fixture records are cleaned up without modifying existing shoppers' carts or orders.
