import assert from "node:assert/strict";
import { test } from "node:test";
import { randomUUID } from "node:crypto";
import { loadSuite, canonical, rawConstant, score, compare, reportSchema, variantPrompt, hash } from "../src/evaluation-core.js";
import type { Report, EvalCase } from "../src/evaluation-core.js";
import type { WebReply } from "../src/web-graph.js";
import { toMessages, webRequest } from "../src/web-graph.js";
import { measurements, safeMeasurements, uploadEvaluation } from "../src/evaluation-upload.js";
import { validateTraceConfig } from "../src/tracing.js";

const privateText = "PRIVATE-answer-user@example.test-cookie-secret";
const reply = (content = "Hello", calls: EvalCase["expectedCalls"] = []): WebReply => ({
  message: {role:"assistant", content, tool_calls:calls.map(c => ({function:c}))},
  usage:{inputTokens:100,outputTokens:20,totalNanoseconds:1000,loadNanoseconds:0},
});
async function fixture(): Promise<Report> {
  const suite = await loadSuite();
  return {version:1,id:randomUUID(),createdAt:new Date().toISOString(),mode:"proposal-only",variant:"baseline",repeats:1,
    corpusHash:suite.corpusHash,schemasHash:suite.schemasHash,basePromptHash:suite.basePromptHash,
    promptHash:hash(suite.prompt),graphHash:suite.graphHash,evaluatorHash:suite.evaluatorHash,
    model:"qwen3.8:27b",modelDigest:"a".repeat(64),ollamaVersion:"test",completed:true,
    rows:suite.cases.map(c => {
      const answer = reply(privateText,c.expectedCalls);
      return {caseId:c.id,repeat:1,category:c.category,startedAt:new Date().toISOString(),durationMs:100,
        firstTextMs:50,error:null,reply:answer,...score(c,answer,suite.tools)};
    })};
}

test("fixed suite uses production instructions and paired histories within the real request budget", async () => {
  const suite = await loadSuite();
  assert.equal(suite.cases.length,44); assert.equal(suite.tools.length,12);
  assert.ok(suite.prompt.startsWith("You are CommercePilot"));
  assert.ok(suite.prompt.includes("Respond in English"));
  assert.equal(new Set(suite.cases.map(c => c.id)).size,44);
  for (const c of suite.cases) {
    const request = webRequest.parse({messages:[{role:"system",content:suite.prompt},...c.messages],tools:suite.tools});
    assert.ok(Buffer.byteLength(JSON.stringify(request)) <= 24576,c.id);
    assert.doesNotThrow(() => toMessages(request.messages),c.id);
    assert.ok(c.expectedCalls.every(call => suite.tools.some(t => t.name === call.name)),c.id);
  }
  assert.deepEqual(JSON.parse(canonical({z:[{b:2,a:1}],a:null})),{a:null,z:[{a:1,b:2}]});
  assert.equal(rawConstant('X = """\r\n    Hello\r\n    world\r\n    """;',"X"),"Hello\nworld");
  assert.throws(() => rawConstant('X = "new format";',"X"),/format changed/);
});

test("scoring rejects wrong IDs, extra calls/parameters and accepts schema defaults", async () => {
  const suite = await loadSuite(), c = suite.cases[0]!;
  assert.equal(score(c,reply("",c.expectedCalls),suite.tools).passed,true);
  assert.equal(score(c,reply("",[...c.expectedCalls,...c.expectedCalls]),suite.tools).passed,false);
  const wrong = structuredClone(c.expectedCalls); wrong[0]!.arguments.maxPrice = 200;
  assert.equal(score(c,reply("",wrong),suite.tools).passed,false);
  wrong[0]!.arguments = {...c.expectedCalls[0]!.arguments,userId:"other-shopper"};
  assert.equal(score(c,reply("",wrong),suite.tools).passed,false);
  const defaults = structuredClone(c.expectedCalls); defaults[0]!.arguments.pageSize = 20;
  const schema = suite.tools.find(t => t.name === "search_products")!.parameters.properties as Record<string,{default?:unknown}>;
  defaults[0]!.arguments.pageSize = schema.pageSize!.default;
  assert.equal(score(c,reply("",defaults),suite.tools).passed,true);
  const reference = suite.cases.find(c => c.id.startsWith("09-"))!;
  const calls = structuredClone(reference.expectedCalls); calls[0]!.arguments.productId = randomUUID();
  assert.equal(score(reference,reply("",calls),suite.tools).passed,false);
});

test("a fluent reply does not pass missing evidence, unsafe paths or a missing model result", async () => {
  const suite = await loadSuite(), c = suite.cases.find(c => c.id === "43-rag-unavailable")!;
  assert.equal(score(c,reply("I cannot verify that page."),suite.tools).passed,true);
  assert.equal(score(c,reply("I cannot verify that page. Go to /profile."),suite.tools).passed,false);
  assert.equal(score(c,reply("Everything is ready!"),suite.tools).passed,false);
  assert.equal(score(c,null,suite.tools).passed,false);
  assert.equal(score({...c,requiredTextGroups:[],forbiddenPatterns:["\\b\\d+ years?\\b"]},reply("3 years"),suite.tools).passed,false);
});

test("comparison detects regressions and rejects partial, duplicate, mismatched or invalid repetition reports", async () => {
  const a = await fixture(), b = structuredClone(a);
  a.rows[0]!.passed = true; b.rows[0]!.passed = false;
  assert.deepEqual(compare(a,b).regressions,[`${a.rows[0]!.caseId}:1`]);
  b.completed = false; assert.throws(() => compare(a,b),/complete/); b.completed = true;
  b.rows[1] = structuredClone(b.rows[0]!); assert.throws(() => compare(a,b),/coverage|identities/);
  const changed = structuredClone(a); changed.modelDigest = "b".repeat(64);
  assert.throws(() => compare(a,changed),/modelDigest/);
  const invalid = structuredClone(a); invalid.rows[0]!.repeat = 2;
  assert.throws(() => compare(a,invalid),/coverage/);
  invalid.rows[0]!.firstTextMs = 101;
  assert.throws(() => reportSchema.parse(invalid));
});

test("measurement projection excludes answers, tools, environment data and non-finite metrics", async () => {
  const report = await fixture();
  assert.ok(!JSON.stringify(measurements(report)).includes(privateText));
  assert.deepEqual(safeMeasurements({passed:true,durationMs:Infinity,firstTextMs:-1,inputTokens:100,
    content:privateText,arguments:{token:privateText},environment:privateText}),{passed:true,inputTokens:100});
});

test("exports refuse tampered scores and stale evaluator hashes before any network request", async () => {
  const a = await fixture(), b = structuredClone(a);
  const config = validateTraceConfig({LANGSMITH_API_KEY:"fake-test-key"});
  let requests = 0;
  const transport: typeof fetch = async () => { requests++; throw new Error("Unexpected network"); };
  b.rows[0]!.passed = !b.rows[0]!.passed;
  await assert.rejects(uploadEvaluation([a,b],config,transport),/Stored scores/);
  a.evaluatorHash = b.evaluatorHash = "b".repeat(64);
  await assert.rejects(uploadEvaluation([a,b],config,transport),/current fixed suite/);
  assert.equal(requests,0);
});

for (const existing of [true,false]) test(`real LangSmith SDK exports only measurements (${existing ? "existing" : "new"} dataset)`, async () => {
  const a = await fixture(), b = structuredClone(a), suite = await loadSuite();
  b.id = randomUUID(); b.variant = "concise"; b.promptHash = hash(variantPrompt(suite.prompt,b.variant));
  const datasetId = randomUUID();
  let examples: {id:string;inputs:{caseId:string};outputs:{passed:boolean}}[] = suite.cases.map(c => ({id:randomUUID(),inputs:{caseId:c.id},outputs:{passed:true}}));
  const projects: string[] = [], batches: {post:Record<string,unknown>[]}[] = [], bodies: string[] = [];
  let feedback = 0;
  const config = validateTraceConfig({LANGSMITH_API_KEY:"fake-test-key",LANGSMITH_ENDPOINT:"https://eu.api.smith.langchain.com"});
  const result = await uploadEvaluation([a,b],config,async (input,init) => {
    const url = new URL(String(input)), body = init?.body ? await new Response(init.body).text() : "";
    bodies.push(body); assert.equal(init?.redirect,"error");
    const json = (data:unknown) => new Response(JSON.stringify(data),{headers:{"Content-Type":"application/json"}});
    if (url.pathname === "/datasets" && (!init?.method || init.method === "GET")) return json(existing ? [{id:datasetId,name:"fixed"}] : []);
    if (url.pathname === "/datasets" && init?.method === "POST") return json({id:datasetId,name:"fixed"});
    if (url.pathname === "/info") return json({instance_flags:{dataset_examples_multipart_enabled:true}});
    if (url.pathname.endsWith(`/datasets/${datasetId}/examples`) && init?.method === "POST") {
      assert.ok(init.body instanceof FormData);
      examples = [];
      for (const [name,value] of init.body.entries()) if (name.endsWith(".inputs")) {
        assert.ok(value instanceof Blob);
        const inputs = JSON.parse(await value.text()); assert.deepEqual(Object.keys(inputs),["caseId"]);
        examples.push({id:name.slice(0,-7),inputs,outputs:{passed:true}});
      }
      assert.equal(examples.length,44); return json({example_ids:examples.map(e => e.id)});
    }
    if (url.pathname.startsWith("/examples/")) return json(examples.find(e => url.pathname.endsWith(e.id)));
    if (url.pathname === "/examples") return json(examples);
    if (url.pathname === "/sessions" && init?.method === "POST") {
      const data = JSON.parse(body); assert.equal(data.reference_dataset_id,datasetId);
      const id = randomUUID(); projects.push(id); return json({id,name:data.name});
    }
    if (url.pathname === "/runs/batch") { batches.push(JSON.parse(body)); return json({}); }
    if (url.pathname === "/feedback") { feedback++; assert.equal(JSON.parse(body).key,"scenario_pass"); return json({id:randomUUID()}); }
    if (url.pathname.startsWith("/sessions/") && init?.method === "PATCH") return json({id:projects.at(-1)});
    if (url.pathname === "/api/v2/runs/query") return json({items:batches.at(-1)!.post.map(r => ({id:r.id})),next_cursor:null});
    throw new Error(`Unexpected test request: ${init?.method} ${url.pathname}`);
  });
  assert.equal(result.region,"EU"); assert.equal(result.experiments.length,2);
  assert.equal(feedback,88); assert.equal(batches.length,2);
  const serialized = bodies.join("\n");
  for (const secret of [privateText,config.apiKey,suite.prompt,"LANGCHAIN_PRIVATE_TEST"])
    assert.ok(!serialized.includes(secret));
  for (const batch of batches) for (const run of batch.post) {
    assert.ok(examples.some(e => e.id === run.reference_example_id));
    assert.deepEqual(Object.keys(run.inputs as object),["caseId"]);
    assert.ok(!("runtime" in (run.extra as object)));
  }
});
