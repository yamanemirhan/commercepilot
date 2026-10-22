import { PostgresConversations, localMemoryPool } from "./postgres-memory.js";

async function main() {
  const [command, ...extra] = process.argv.slice(2);
  if (!["setup", "prune"].includes(command ?? "") || extra.length) throw new Error("Expected setup or prune.");
  const store = new PostgresConversations(await localMemoryPool());
  try {
    if (command === "setup") {
      await store.setup();
      console.log("Conversation ownership table and LangGraph checkpoint tables are ready in commerce_agent.");
    } else console.log(`Removed ${await store.pruneExpired()} expired conversations (maximum 100 per run).`);
  } finally { await store.close(); }
}
main().catch(() => {
  console.error("Memory maintenance failed. Check the local database and .env; run memory:setup before memory:prune.");
  process.exitCode = 1;
});
