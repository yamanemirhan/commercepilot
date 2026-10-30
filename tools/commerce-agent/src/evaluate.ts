// Run model evaluation locally first. Only a separate explicit --upload command
// can export allowlisted measurements, never automatic model or graph traces.
process.env.LANGSMITH_TRACING = "false";
process.env.LANGCHAIN_TRACING_V2 = "false";
process.env.LANGCHAIN_TRACING = "false";
const { main } = await import("./evaluation-cli.js");
main(process.argv.slice(2)).catch(() => {
  console.error("Evaluation command failed. Check arguments, local services or LangSmith configuration; raw errors are withheld.");
  process.exitCode = 2;
});
