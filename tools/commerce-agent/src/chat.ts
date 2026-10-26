import { createInterface } from "node:readline/promises";
import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";
import type { Review, ReviewIntent } from "./review-contract.js";

process.env.LANGSMITH_TRACING = "false";
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGCHAIN_TRACING = "false";

async function main() {
  const [mode, account, ...extra] = process.argv.slice(2);
  if (mode === "--help") {
    console.log("Usage: npm run chat:ram -- alex | npm run chat:postgres -- alex (or sam)");
    console.log("Local developer demo: signs in through the API using ignored .env demo credentials.");
    console.log("Commands: /new, /list, /use <id>, /delete <id>, /cart-edit <request>, /exit. Catalog, own cart/orders; no trace upload.");
    console.log("Human review: /review checkout [success|failure|delay], /review cancel <order-id>, /review refund <order-id>.");
    console.log("/review, /approve <review-id>, /reject <review-id>, /sync-review. A normal chat message is never approval.");
    return;
  }
  if (!["ram", "postgres"].includes(mode ?? "") || !["alex", "sam"].includes(account ?? "") || extra.length) {
    throw new Error("Use chat:ram or chat:postgres with alex or sam. See chat --help.");
  }
  const [{ RamConversations, Conversations }, { PostgresConversations, localMemoryPool }, { ShopperSession },
    { connectShopperMcp }, { createCatalogModel }, { runCatalogAgent }, { createReviewFlow, decideReview },
    { reviewGateway }, { formatReview, reviewIntent, sameReview }] = await Promise.all([
    import("./conversations.js"), import("./postgres-memory.js"), import("./shopper-session.js"),
    import("./shopper-mcp.js"), import("./model.js"), import("./catalog-agent.js"),
    import("./review-graph.js"), import("./review-client.js"), import("./review-contract.js"),
  ]);
  const session = new ShopperSession();
  const store = mode === "ram" ? new RamConversations() : new PostgresConversations(await localMemoryPool());
  let input: ReturnType<typeof createInterface> | undefined;
  try {
    const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
    await session.login(`${account}@commercepilot.local`, env[`DEMO_${account!.toUpperCase()}_PASSWORD`] ?? "");
    if (store instanceof PostgresConversations) await store.pruneExpired();
    const conversations = new Conversations(store, () => session.owner());
    const reviews = reviewGateway(session);
    // Receipt exists only after the trusted formatter displays exact server terms in this process.
    const shown = new Map<string, Review>();
    const display = (conversationId: string, review: Review) => {
      console.log(formatReview(review)); shown.set(conversationId, review);
    };
    let selected: string | undefined;
    console.log(`Signed in as ${account}. Memory: ${mode}. Catalog + your cart/orders + reviewed site guidance. Trace mode: off.`);
    console.log("/new, /list, /use <id>, /delete <id>, /exit. Use /cart-edit <request> to enable one cart change for that turn.");
    console.log("Memory retains up to four complete turns (8 KB); conversations expire after 24 hours and allow 30 turns.");
    console.log("Reviews: /review checkout [success|failure|delay], /review cancel <order-id>, /review refund <order-id>.");
    console.log("/review displays current terms; /approve <review-id> or /reject <review-id> sends your decision. /sync-review only reads the outcome.");
    input = createInterface({ input: process.stdin, output: process.stdout });
    // One iterator supports both interactive terminals and piped learning/test transcripts.
    process.stdout.write("You> ");
    for await (const line of input) {
      const text = line.trim();
      if (text === "/exit") break;
      try {
        if (text === "/new") selected = (await conversations.create()).id;
        else if (text === "/list") console.table(await conversations.list());
        else if (text.startsWith("/use ")) {
          const id = text.slice(5).trim();
          if (!(await conversations.list()).some(row => row.id === id)) throw new Error("Conversation not found or expired.");
          selected = id;
        } else if (text.startsWith("/delete ")) {
          const id = text.slice(8).trim();
          await conversations.remove(id);
          shown.delete(id);
          if (selected === id) selected = undefined;
          console.log("Conversation and all its checkpoints deleted.");
        } else if (text === "/review" || text.startsWith("/review ")) {
          const parts = text.split(/\s+/);
          const signal = AbortSignal.timeout(30_000);
          if (parts.length === 1) {
            if (!selected) throw new Error("Review requires a selected conversation.");
            const review = await conversations.inspect(selected, async memory => {
              const state = await createReviewFlow(memory, reviews, signal).inspect();
              if (!state.review) throw new Error("Review not found in this conversation.");
              const current = await reviews.read(state.review.id, signal);
              if (!sameReview(state.review, current)) throw new Error("Review terms changed unexpectedly.");
              return current;
            });
            display(selected, review);
          } else {
            let intent: ReviewIntent;
            if (parts[1] === "checkout" && parts.length <= 3)
              intent = reviewIntent.parse({ kind: "Checkout", paymentMode: parts[2] ?? "success" });
            else if (["cancel", "refund"].includes(parts[1]!) && parts.length === 3)
              intent = reviewIntent.parse({ kind: parts[1] === "cancel" ? "Cancellation" : "Refund", orderId: parts[2] });
            else throw new Error("Review command: /review checkout [success|failure|delay], /review cancel <order-id>, /review refund <order-id>.");
            selected ??= (await conversations.create()).id;
            const existing = await conversations.inspect(selected, memory => createReviewFlow(memory, reviews, signal).inspect());
            if (existing.unfinished) throw new Error("Review already pending or unfinished. Use /review or /sync-review.");
            const review = await conversations.run(selected, memory => createReviewFlow(memory, reviews, signal).start(intent));
            display(selected, review);
          }
        } else if (text.startsWith("/approve ") || text.startsWith("/reject ")) {
          if (!selected || !shown.has(selected)) throw new Error("Review must be displayed with /review before deciding.");
          const [command, actionId, ...extra] = text.split(/\s+/);
          if (!actionId || extra.length) throw new Error("Review decision needs exactly one review ID.");
          const receipt = shown.get(selected)!;
          if (receipt.id !== actionId) throw new Error("Review ID must match the displayed review.");
          const id = selected;
          const signal = AbortSignal.timeout(30_000);
          const result = await conversations.inspect(id, memory => decideReview(createReviewFlow(memory, reviews, signal), receipt,
            actionId, command === "/approve" ? "approve" : "reject", reviews,
            () => conversations.claimReviewDecision(id, actionId), signal));
          shown.delete(id);
          if (result.review) display(id, result.review);
          else console.log("Review decision outcome is uncertain. Do not resend it. Use /sync-review to read the server outcome, or inspect the storefront.");
        } else if (text === "/sync-review") {
          if (!selected) throw new Error("Review requires a selected conversation.");
          const signal = AbortSignal.timeout(30_000);
          const review = await conversations.inspect(selected, async memory => {
            const flow = createReviewFlow(memory, reviews, signal);
            const state = await flow.inspect();
            if (!state.review) throw new Error("Review not found in this conversation.");
            const current = await reviews.read(state.review.id, signal);
            if (!sameReview(state.review, current)) throw new Error("Review terms changed unexpectedly.");
            return (state.waiting || state.verifying) && current.status !== "Pending" ? flow.sync() : current;
          });
          display(selected, review);
        } else if (text) {
          const cartWrite = text.startsWith("/cart-edit ");
          const question = cartWrite ? text.slice(11).trim() : text;
          if (!question || question.startsWith("/") || question.length > 2000) throw new Error("Use a supported command or a question of 1–2000 characters.");
          selected ??= (await conversations.create()).id;
          const signal = AbortSignal.timeout(180_000);
          const answer = await conversations.run(selected, async memory => {
            const client = await connectShopperMcp(session, signal, cartWrite);
            try {
              const model = createCatalogModel(signal);
              return await runCatalogAgent(question, client, tools => model.bindTools(tools), signal, event => {
                if (event.type === "node") console.log(`[${event.name}] ${event.phase}`);
                if (event.type === "call") console.log(`[Tool] ${event.name}`);
              }, memory, { cartWrite }, (query, requestSignal) => session.searchKnowledge(query, requestSignal));
            } finally { await client.close(); }
          });
          console.log(`CommercePilot> ${answer}`);
        }
        if (selected) console.log(`[Conversation] ${selected}`);
      } catch (error) {
        const message = error instanceof Error ? error.message : "";
        const safe = /^(Review |Conversation |RAM conversation|Your session |This conversation |The latest turn |This request exceeds|Use a supported command)/.test(message);
        console.error(safe ? message : "The operation failed. Use /new and check your cart before retrying any edit: a change may already have happened. Check session and service availability.");
      }
      process.stdout.write("You> ");
    }
  } finally {
    input?.close();
    try { await session.logout(); } catch { /* Cookies are discarded locally even if the API is unavailable. */ }
    await store.close();
  }
}
main().catch(() => {
  console.error("Chat could not start. Start the local stack, run memory:setup for PostgreSQL, and pass alex or sam. Use chat --help.");
  process.exitCode = 1;
});
