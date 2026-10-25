import { createInterface } from "node:readline/promises";
import { readFile } from "node:fs/promises";
import { parseEnv } from "node:util";

process.env.LANGSMITH_TRACING = "false";
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGCHAIN_TRACING = "false";

async function main() {
  const [mode, account, ...extra] = process.argv.slice(2);
  if (mode === "--help") {
    console.log("Usage: npm run chat:ram -- alex | npm run chat:postgres -- alex (or sam)");
    console.log("Local developer demo: signs in through the API using ignored .env demo credentials.");
    console.log("Commands: /new, /list, /use <id>, /delete <id>, /cart-edit <request>, /exit. Catalog, own cart/orders; no trace upload.");
    return;
  }
  if (!["ram", "postgres"].includes(mode ?? "") || !["alex", "sam"].includes(account ?? "") || extra.length) {
    throw new Error("Use chat:ram or chat:postgres with alex or sam. See chat --help.");
  }
  const [{ RamConversations, Conversations }, { PostgresConversations, localMemoryPool }, { ShopperSession },
    { connectShopperMcp }, { createCatalogModel }, { runCatalogAgent }] = await Promise.all([
    import("./conversations.js"), import("./postgres-memory.js"), import("./shopper-session.js"),
    import("./shopper-mcp.js"), import("./model.js"), import("./catalog-agent.js"),
  ]);
  const session = new ShopperSession();
  const store = mode === "ram" ? new RamConversations() : new PostgresConversations(await localMemoryPool());
  let input: ReturnType<typeof createInterface> | undefined;
  try {
    const env = parseEnv(await readFile(new URL("../../../.env", import.meta.url), "utf8"));
    await session.login(`${account}@commercepilot.local`, env[`DEMO_${account!.toUpperCase()}_PASSWORD`] ?? "");
    if (store instanceof PostgresConversations) await store.pruneExpired();
    const conversations = new Conversations(store, () => session.owner());
    let selected: string | undefined;
    console.log(`Signed in as ${account}. Memory: ${mode}. Catalog + your cart/orders + reviewed site guidance. Trace mode: off.`);
    console.log("/new, /list, /use <id>, /delete <id>, /exit. Use /cart-edit <request> to enable one cart change for that turn.");
    console.log("Memory retains up to four complete turns (8 KB); conversations expire after 24 hours and allow 30 turns.");
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
          if (selected === id) selected = undefined;
          console.log("Conversation and all its checkpoints deleted.");
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
        const safe = /^(Conversation |RAM conversation|Your session |This conversation |The latest turn |This request exceeds|Use a supported command)/.test(message);
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
