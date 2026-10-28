// Disable automatic export before loading LangChain. Authenticated chat content
// is never sent to LangSmith; the CLI's opt-in sanitized tracing is separate.
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGSMITH_TRACING = "false";
delete process.env.LANGSMITH_API_KEY;
delete process.env.LANGCHAIN_API_KEY;
const { startWebServer } = await import("./web-http.js");
startWebServer();
