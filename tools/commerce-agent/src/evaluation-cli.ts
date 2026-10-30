import { randomUUID } from "node:crypto";
import { readFile, writeFile, mkdir, rename, readdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { loadSuite, hash, variantSchema, variantPrompt, reportSchema, summary, compare, score, root } from "./evaluation-core.js";
import type { Report } from "./evaluation-core.js";
import { runWebGraph } from "./web-graph.js";
import type { WebReply, StreamModel } from "./web-graph.js";
import { createCatalogModel, modelName } from "./model.js";

const directory = new URL(".local/evaluations/", root);
async function save(report: Report) {
  await mkdir(directory, { recursive: true });
  const path = new URL(`${report.variant}-${report.id}.json`, directory);
  await writeFile(new URL(`${report.variant}-${report.id}.tmp`, directory), JSON.stringify(report, null, 2) + "\n");
  await rename(new URL(`${report.variant}-${report.id}.tmp`, directory), path);
  return fileURLToPath(path);
}
async function latest(variant: string) {
  const files = (await readdir(directory)).filter(name => name.startsWith(`${variant}-`) && name.endsWith(".json"));
  const reports = await Promise.all(files.map(async name => reportSchema.parse(JSON.parse(await readFile(new URL(name,directory), "utf8")))));
  const report = reports.filter(r => r.completed).sort((a,b) => b.createdAt.localeCompare(a.createdAt))[0];
  if (!report) throw new Error("No complete report for this variant.");
  return report;
}
async function readReport(path?: string, variant = "baseline") {
  if (!path) return latest(variant);
  const value = await readFile(path,"utf8");
  if (Buffer.byteLength(value) > 4_000_000) throw new Error("Report too large.");
  return reportSchema.parse(JSON.parse(value));
}
async function localJson(path: string) {
  const response = await fetch(`http://127.0.0.1:11434/api/${path}`, { redirect: "error", signal: AbortSignal.timeout(5000) });
  if (!response.ok) throw new Error("Local model metadata unavailable.");
  return response.json();
}

export async function main(args: string[]) {
  if (!args.length || args[0] === "--help") {
    console.log("Usage: npm run eval -- baseline|concise [repeats:1..3]");
    console.log("       npm run eval -- --compare [baseline.json concise.json]");
    console.log("       npm run eval -- --upload [baseline.json concise.json]");
    console.log("44 fixed, synthetic scenarios; real local model and production web graph; no proposed tool executes.");
    console.log("Reports stay in .local/evaluations. Upload sends only scenario IDs, measurements and scores to configured LangSmith.");
    return;
  }
  if (args[0] === "--compare" || args[0] === "--upload") {
    if (args.length !== 1 && args.length !== 3) throw new Error("Supply both report paths or neither.");
    const a = await readReport(args[1],"baseline"), b = await readReport(args[2],"concise");
    console.log(JSON.stringify(compare(a,b), null, 2));
    if (args[0] === "--upload") {
      const { uploadEvaluation } = await import("./evaluation-upload.js");
      const { loadTraceConfig } = await import("./tracing.js");
      const config = await loadTraceConfig();
      // A separate receipt reserves this export before any network mutation. A
      // partial upload is reported rather than silently duplicated on a retry.
      const receipt = new URL(`upload-${a.id}-${b.id}.json`,directory);
      await writeFile(receipt, JSON.stringify({status:"started"}), { flag: "wx" });
      try {
        const result = await uploadEvaluation([a,b],config);
        await writeFile(receipt,JSON.stringify({status:"completed",...result},null,2));
        console.log(JSON.stringify(result,null,2));
      } catch {
        await writeFile(receipt,JSON.stringify({status:"failed-or-partial",notice:"Inspect LangSmith before retrying. Local scores are unchanged."}));
        throw new Error("LangSmith upload failed or is partial; inspect the receipt.");
      }
    }
    return;
  }
  const variant = variantSchema.parse(args[0]);
  const repeats = args.length === 2 ? Number(args[1]) : 1;
  if (args.length > 2 || !Number.isInteger(repeats) || repeats < 1 || repeats > 3) throw new Error("Repeats must be 1..3.");
  const suite = await loadSuite();
  const prompt = variantPrompt(suite.prompt,variant);
  const tags = await localJson("tags") as {models:{name:string;digest:string}[]};
  const version = await localJson("version") as {version:string};
  const installed = tags.models.find(m => m.name === modelName);
  if (!installed || !/^[a-f0-9]{64}$/.test(installed.digest)) throw new Error("Install the configured local model first.");
  const report: Report = { version:1, id:randomUUID(), createdAt:new Date().toISOString(), mode:"proposal-only", variant,repeats,
    corpusHash:suite.corpusHash, schemasHash:suite.schemasHash, basePromptHash:suite.basePromptHash,
    promptHash:hash(prompt), graphHash:suite.graphHash,
    evaluatorHash:suite.evaluatorHash,
    model:modelName, modelDigest:installed.digest, ollamaVersion:version.version, completed:false, rows:[] };
  reportSchema.parse(report);
  const stop = new AbortController();
  const cancel = () => stop.abort();
  process.once("SIGINT", cancel); process.once("SIGTERM", cancel);
  const stream: StreamModel = async (messages,tools,signal) => createCatalogModel(signal).bindTools(tools).stream(messages,{signal,callbacks:[]});
  let path = await save(report);
  try {
    console.log(`Warming ${modelName}; variant=${variant}; ${suite.cases.length} scenarios x ${repeats}. No live commerce operations.`);
    await runWebGraph({messages:[{role:"user",content:"Say hello briefly."}],tools:[]},stream,
      AbortSignal.any([stop.signal,AbortSignal.timeout(180_000)]),async () => {});
    for (let repeat=1; repeat<=repeats && !stop.signal.aborted; repeat++) {
      for (const test of suite.cases) {
        if (stop.signal.aborted) break;
        const startedAt = new Date().toISOString(), start=performance.now();
        let firstTextMs: number | null = null;
        let error: Report["rows"][number]["error"] = null;
        const answers: WebReply[] = [];
        try {
          // Expected calls and text assertions NEVER enter the model context.
          await runWebGraph({messages:[{role:"system",content:prompt},...test.messages],tools:suite.tools},stream,
            AbortSignal.any([stop.signal,AbortSignal.timeout(180_000)]),async frame => {
              if (frame.type === "delta" && firstTextMs === null) firstTextMs = Math.round(performance.now()-start);
              if (frame.type === "result") answers.push(frame.reply);
            });
        } catch { error = stop.signal.aborted ? "cancelled" : "round-failed"; }
        const reply = answers[0] ?? null;
        const scores = score(test,reply,suite.tools);
        report.rows.push({caseId:test.id,category:test.category,repeat,startedAt,durationMs:Math.round(performance.now()-start),
          firstTextMs,error,reply,...scores});
        path = await save(report);
        console.log(`[${repeat}/${repeats}] ${test.id}: ${scores.passed ? "PASS" : "FAIL"} (${report.rows.at(-1)!.durationMs} ms)`);
      }
    }
    const after = await localJson("tags") as typeof tags;
    if (after.models.find(m => m.name === modelName)?.digest !== report.modelDigest) {
      report.completed = false; throw new Error("The model changed during evaluation.");
    }
    report.completed = report.rows.length === suite.cases.length * repeats && !stop.signal.aborted;
  } finally {
    path = await save(report);
    process.off("SIGINT",cancel); process.off("SIGTERM",cancel);
    console.log(JSON.stringify(summary(report),null,2)); console.log(`Report: ${path}`);
  }
  if (!report.completed || report.rows.some(r => !r.passed)) process.exitCode = 1;
}
