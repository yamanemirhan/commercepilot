# Agent evaluation

A fixed, synthetic benchmark for the storefront's LangGraph inference segment. It measures tool/argument selection, reference resolution and explanation checks. It never executes proposed commerce operations. Requirements: Node.js 24+, installed npm dependencies and local Ollama with `qwen3.8:27b`. Normal evaluation needs neither the application stack nor LangSmith.

## Run and compare

From the repository root, without using the storefront's AI concurrently:

```powershell
npm.cmd --prefix tools/commerce-agent run eval:baseline
npm.cmd --prefix tools/commerce-agent run eval:concise
npm.cmd --prefix tools/commerce-agent run eval:compare
```

`baseline` loads the current .NET shopping, chat and site-guidance instructions from source. `concise` adds a two-sentence response constraint for comparison only; it does not change the application. Both use the production `runWebGraph` and `ChatOllama` configuration: temperature 0, seed 42, 8K context, 2,048 generated tokens including thinking, no automatic retry. Warm-up is excluded from measurements. Each case is an independent graph segment with fixed historical tool results where needed.

The 44 cases cover catalog filters, ordered references, bag edits, orders, review preparation, greetings, scope/ambiguity, foreign-account requests, prompt injection, uncertain writes, failures and verified/unavailable site guidance. `cases.json` contains 40 shopping cases; `rag-cases.json` adds four knowledge cases. Products, accounts, orders and results are synthetic. `tool-schemas.json` freezes the 12 input definitions, including tools requiring scoped authorization in the application.

Checks compare exact tool order/name/arguments, treating omitted schema defaults equivalently. Reply checks use required keyword groups and forbidden phrases/patterns. These are code assertions, **not an LLM judge or proof of semantic correctness**. Inspect failed answers locally and sample passing answers. Review expectations before changing tests; do not loosen assertions merely to improve the score. Assertions and expected answers never enter the model context.

Reports are saved after every case in ignored `.local/evaluations/`. They contain final public replies, validated tool proposals, timings, usage, scores and reproducibility metadata; hidden reasoning is excluded. Failed rounds have fixed error codes and missing usage. Exit `0` means all cases passed, `1` means failed cases or an interrupted evaluation, and `2` means a setup/command/export error. Interrupted reports cannot be compared or exported.

Comparison uses the latest complete report per variant, or two explicit paths. It rejects different corpus, schema, base prompt, graph/model code, evaluator/runner code, model digest, Ollama version or repetition count. It reports pass counts, critical failures (safety/recovery/site knowledge), regressions/improvements, median/p95 duration and input/output token totals. `firstTextMs` is the first public text chunk, null for tool-only replies. Sequential local timings exclude application/network overhead and do not predict production throughput.

For repeated runs, use the same count for both variants:

```powershell
npm.cmd --prefix tools/commerce-agent run eval -- baseline 3
npm.cmd --prefix tools/commerce-agent run eval -- concise 3
npm.cmd --prefix tools/commerce-agent run eval -- --compare C:\path\baseline.json C:\path\concise.json
```

A fixed seed improves repeatability but cannot guarantee identical output across hardware/runtime changes. One pass over 44 cases is a smoke benchmark, not production certification. Review regressions and important answers before promoting a candidate. Corpus/policy changes need a fresh paired baseline; old and new scores are not directly interchangeable.

## Optional LangSmith comparison

```powershell
npm.cmd --prefix tools/commerce-agent run eval:upload
```

This explicit upload is separate from inference. Configure ignored `.local/langsmith.env` with the correct US/EU endpoint. Unlike write-only tracing, it needs permission to read/create datasets, create experiments/runs/feedback and read runs. A tracing-only key may be insufficient. Keep credentials in that file, never reports or command arguments.

The exporter creates/reuses `commercepilot-fixed-<corpus-hash>` with 44 examples containing only case IDs and expected pass flags. It creates `commercepilot-baseline-<report-id>` and `commercepilot-concise-<report-id>` experiments, links runs to their examples and records `scenario_pass` feedback. In LangSmith, open **Datasets & Experiments**, select this dataset and compare both experiments. Inspect pass scores and exported timing/token fields; read questions/answers in the local corpus/report.

Only allowlisted case IDs, generated run IDs, timestamps, code/model hashes, variants, scores and bounded timing/token measurements leave the computer. Prompts, replies, tool arguments/results, reasoning, credentials and environment metadata are excluded. No cloud model or paid LLM judge is invoked. LangSmith account limits apply. Automatic tracing stays disabled during evaluation even when tracing environment variables are set.

Reports are validated against the current suite and re-scored before export. Successful uploads are read back to verify every expected run ID. There is a three-minute overall deadline, ten-second request deadlines and no automatic write retries. An ignored `upload-<report-ids>.json` receipt reserves the upload and records completion or partial failure, preventing silent duplicate exports. After failure, inspect the receipt and existing experiments before deliberately retrying. HTTP acceptance alone does not prove that the full comparison was saved.

See the official [evaluation workflow](https://github.com/langchain-ai/langsmith-sdk/blob/main/openwiki/workflows/evaluation-and-experiments.md) and [run-query API](https://docs.langchain.com/langsmith/smithdb-sdk-migration-query-runs).

## Regression and fixture maintenance

```powershell
npm.cmd --prefix tools/commerce-agent run typecheck
npm.cmd --prefix tools/commerce-agent test
# Local stack and demo credentials; discovery only:
npm.cmd --prefix tools/commerce-agent run eval:schemas
# Existing isolated/cleaned database and service fixtures:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
npm.cmd --prefix tools/commerce-agent run test:memory
npm.cmd --prefix tools/commerce-agent run test:shopping
npm.cmd --prefix tools/commerce-agent run test:reviews
```

`eval:schemas` signs in as the local demo shopper and compares scoped MCP discovery with the fixture. It executes no tools and never overwrites the fixture. After intentional API changes, review/update schemas and affected expectations together, then rerun both variants. Review knowledge cases when pages/policies change: they test answers with supplied evidence, not live retrieval relevance, publication freshness or citation rendering.

GitHub Actions runs deterministic TypeScript checks without a GPU, database or cloud key. Real-model and stack integration tests remain explicit local commands. API/PostgreSQL tests enforce actual ownership, scopes, idempotency and approval boundaries; a model's refusal is not access control. This benchmark does not test end-to-end streaming, actual tool execution, checkpoint retention, concurrent load or long-term memory.

Code: `src/evaluation-core.ts` loads/scores cases; `src/evaluation-cli.ts` runs/compares them; `src/evaluation-upload.ts` exports measurements; `tests/evaluation.test.ts` checks scoring, integrity and real SDK serialization with a controlled transport.
