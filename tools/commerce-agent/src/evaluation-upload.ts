import { randomUUID } from "node:crypto";
import { Client, RunTree } from "langsmith";
import { loadSuite, reportSchema, compare, score, variantPrompt, hash } from "./evaluation-core.js";
import type { Report } from "./evaluation-core.js";
import type { TraceConfig } from "./tracing.js";

const finite = (value: unknown) => typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 240_000;
export function safeMeasurements(value: Record<string, unknown>) {
  const result: Record<string, unknown> = {};
  for (const name of ["passed", "toolMatch", "explanation"])
    if (typeof value[name] === "boolean") result[name] = value[name];
  for (const name of ["durationMs", "firstTextMs", "inputTokens", "outputTokens"])
    if (finite(value[name])) result[name] = value[name];
  return result;
}
function safeInputs(value: Record<string,unknown>) {
  return typeof value.caseId === "string" && /^\d{2}-[a-z-]+$/.test(value.caseId) ? {caseId:value.caseId} : {};
}
function safeMetadata(value: Record<string,unknown>) {
  const result: Record<string,unknown> = {source:"commercepilot-evaluation",privacy:"measurements-only"};
  for (const field of ["corpusHash","schemasHash","promptHash","graphHash","evaluatorHash","modelDigest"])
    if (typeof value[field] === "string" && /^[a-f0-9]{64}$/.test(value[field])) result[field] = value[field];
  if (value.variant === "baseline" || value.variant === "concise") result.variant = value.variant;
  if (Number.isInteger(value.repeat) && Number(value.repeat) >= 1 && Number(value.repeat) <= 3) result.repeat = value.repeat;
  return result;
}
export function measurements(report: Report) {
  return report.rows.map(row => ({caseId:row.caseId,repeat:row.repeat,startedAt:row.startedAt,
    values:safeMeasurements({passed:row.passed,toolMatch:row.toolMatch,explanation:row.explanation,
      durationMs:row.durationMs, firstTextMs:row.firstTextMs,
      inputTokens:row.reply?.usage.inputTokens,outputTokens:row.reply?.usage.outputTokens})}));
}

export async function uploadEvaluation(raw: Report[], config: TraceConfig, transport: typeof fetch = fetch) {
  const reports = raw.map(r => reportSchema.parse(r));
  if (reports.length !== 2) throw new Error("Upload a pair of experiments.");
  compare(reports[0]!,reports[1]!);
  const suite = await loadSuite();
  // Re-score locally; do not trust stored booleans or export an arbitrary JSON report.
  for (const report of reports) {
    if (report.corpusHash !== suite.corpusHash || report.schemasHash !== suite.schemasHash ||
      report.basePromptHash !== suite.basePromptHash || report.graphHash !== suite.graphHash ||
      report.evaluatorHash !== suite.evaluatorHash ||
      report.promptHash !== hash(variantPrompt(suite.prompt,report.variant))) throw new Error("Report does not match the current fixed suite.");
    for (const row of report.rows) {
      const test = suite.cases.find(c => c.id === row.caseId);
      if (!test || row.category !== test.category) throw new Error("Unknown scenario.");
      const checked = score(test,row.reply,suite.tools);
      if (checked.passed !== row.passed || checked.toolMatch !== row.toolMatch || checked.explanation !== row.explanation)
        throw new Error("Stored scores differ from the evaluator.");
    }
  }
  const deadline = AbortSignal.timeout(180_000);
  const client = new Client({apiKey:config.apiKey,apiUrl:config.endpoint,workspaceId:config.workspaceId ?? "",
    autoBatchTracing:false,omitTracedRuntimeInfo:true,disablePromptCache:true,debug:false,tracingSamplingRate:1,
    callerOptions:{maxRetries:0,maxConcurrency:1},timeout_ms:10_000,
    hideInputs:safeInputs,hideOutputs:safeMeasurements,hideMetadata:safeMetadata,
    fetchImplementation:(input,init) => transport(input,{...init,redirect:"error",
      signal:AbortSignal.any([deadline,...(init?.signal ? [init.signal] : [])])})});
  const datasetName = `commercepilot-fixed-${suite.corpusHash.slice(0,16)}`;
  const exists = await client.hasDataset({datasetName});
  const dataset = exists ? await client.readDataset({datasetName}) : await client.createDataset(datasetName,
    {description:"Fixed synthetic CommercePilot scenarios. Only case identifiers and expected pass flags are stored; prompts and answers stay local."});
  const examples = new Map<string,string>();
  function acceptExample(item: {id:string;inputs:Record<string,unknown>;outputs?:Record<string,unknown> | null}) {
    const id = item.inputs.caseId;
    if (typeof id !== "string" || Object.keys(item.inputs).length !== 1 ||
      !suite.cases.some(c => c.id === id) || examples.has(id) ||
      item.outputs?.passed !== true || Object.keys(item.outputs).length !== 1)
      throw new Error("Dataset differs from the fixed suite.");
    examples.set(id,item.id);
  }
  if (!exists) {
    const uploads = suite.cases.map(c => ({id:randomUUID(),dataset_id:dataset.id,inputs:{caseId:c.id},outputs:{passed:true}}));
    const stored = await client.createExamples(uploads);
    for (const item of stored) {
      if (!uploads.some(sent => sent.id === item.id && sent.inputs.caseId === item.inputs.caseId))
        throw new Error("Uploaded example identity differs.");
      acceptExample(item);
    }
  } else {
    for await (const item of client.listExamples({datasetId:dataset.id,limit:45,includeAttachments:false})) acceptExample(item);
  }
  if (examples.size !== suite.cases.length) throw new Error("Dataset is incomplete; inspect it before retrying.");
  const experiments: {name:string;id:string}[] = [];
  for (const report of reports) {
    const name = `commercepilot-${report.variant}-${report.id}`;
    const metadata = safeMetadata(report);
    const project = await client.createProject({projectName:name,referenceDatasetId:dataset.id,
      description:"Proposal-only local evaluation; scores are code assertions, not an LLM judge or a production readiness guarantee.",
      metadata,numExamples:suite.cases.length,numRepetitions:report.repeats,evaluatorKeys:["scenario_pass"]});
    experiments.push({name,id:project.id});
    const runs = measurements(report).map(row => {
      const start = Date.parse(row.startedAt), end = start + Number(row.values.durationMs);
      const tree = new RunTree({name:"commercepilot_scenario",run_type:"chain",inputs:{},start_time:start,client,project_name:name,replicas:[]});
      return {id:tree.id,trace_id:tree.trace_id,dotted_order:tree.dotted_order,name:"commercepilot_scenario",run_type:"chain" as const,
        session_name:name,reference_example_id:examples.get(row.caseId)!,inputs:{caseId:row.caseId},outputs:row.values,
        start_time:row.startedAt,end_time:new Date(end).toISOString(),extra:{metadata:{...metadata,repeat:row.repeat}}};
    });
    await client.batchIngestRuns({runCreates:runs});
    for (const run of runs) await client.createFeedback({runId:run.id,sessionId:project.id,key:"scenario_pass",
      score:run.outputs.passed ? 1 : 0,feedbackSourceType:"api",extendTraceRetention:false});
    await client.updateProject(project.id,{endTime:new Date().toISOString(),metadata});
    // Acceptance is not read-back verification. Confirm every case was indexed.
    const indexed = new Set<string>();
    for (let attempt=0; attempt<3; attempt++) {
      indexed.clear();
      for await (const run of client.runs.query({project_ids:[project.id],page_size:runs.length+1,is_root:true,
        min_start_time:report.createdAt}, {maxRetries:0,signal:deadline})) {
        if (run.id) indexed.add(run.id);
        if (indexed.size > runs.length) break;
      }
      if (indexed.size === runs.length && runs.every(run => indexed.has(run.id))) break;
      // Read-only indexing retries never resubmit runs, examples or feedback.
      if (attempt < 2) await new Promise(resolve => setTimeout(resolve,1000*(attempt+1)));
    }
    if (indexed.size !== runs.length || runs.some(run => !indexed.has(run.id)))
      throw new Error("Experiment accepted but read-back incomplete; inspect LangSmith before retrying.");
  }
  return {datasetName,datasetId:dataset.id,experiments,region:config.endpoint === "https://eu.api.smith.langchain.com" ? "EU" : "US"};
}
