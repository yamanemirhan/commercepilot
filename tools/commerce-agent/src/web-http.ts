import { createServer } from "node:http";
import { timingSafeEqual } from "node:crypto";
import { once } from "node:events";
import { runWebGraph } from "./web-graph.js";
import type { StreamModel, WebFrame } from "./web-graph.js";
import { createCatalogModel } from "./model.js";

export function createWebServer(secret: string, model: StreamModel) {
  if (!/^[a-f0-9]{64}$/.test(secret)) throw new Error("AGENT_SHARED_KEY must be 32 random bytes encoded as hex.");
  const authorization = Buffer.from(`Bearer ${secret}`);
  let busy = false;
  const server = createServer({ maxHeaderSize: 8192, requestTimeout: 15_000, headersTimeout: 10_000 }, async (req, res) => {
    res.setHeader("Cache-Control", "no-store");
    const supplied = Buffer.from(req.headers.authorization ?? "");
    if (req.headers.origin !== undefined || supplied.length !== authorization.length || !timingSafeEqual(supplied, authorization)) {
      res.writeHead(401).end(); return;
    }
    if (req.method === "GET" && req.url === "/health") { res.writeHead(200).end("ready"); return; }
    if (req.method !== "POST" || req.url !== "/round") { res.writeHead(404).end(); return; }
    if (req.headers["content-type"]?.split(";")[0] !== "application/json") { res.writeHead(415).end(); return; }
    // Bound resource use across requests, not just across calls on one model instance.
    if (busy) { res.writeHead(503).end(); return; }
    busy = true;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 180_000);
    const closed = () => controller.abort();
    res.once("close", closed);
    controller.signal.addEventListener("abort", () => { if (!res.writableEnded) res.destroy(); }, { once: true });
    try {
      let size = 0;
      const chunks: Buffer[] = [];
      for await (const chunk of req) {
        size += chunk.length;
        if (size > 24_576) { res.writeHead(413).end(); return; }
        chunks.push(chunk);
      }
      const input: unknown = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      let outputBytes = 0;
      async function send(frame: WebFrame) {
        controller.signal.throwIfAborted();
        const line = JSON.stringify(frame) + "\n";
        outputBytes += Buffer.byteLength(line);
        if (outputBytes > 1_048_576) throw new Error("Output budget exceeded.");
        if (!res.headersSent) res.writeHead(200, { "Content-Type": "application/x-ndjson" });
        if (!res.write(line)) await once(res, "drain", { signal: controller.signal });
      }
      await runWebGraph(input, model, controller.signal, send);
      res.end();
    } catch {
      // Never log request bodies, model content, headers or provider exceptions.
      if (!res.destroyed) {
        if (!res.headersSent) res.writeHead(502, { "Content-Type": "application/x-ndjson" });
        res.end('{"type":"error","message":"Agent round unavailable."}\n');
      }
    } finally {
      clearTimeout(timer); res.off("close", closed); busy = false;
    }
  });
  return server;
}

export function startWebServer() {
  const endpoint = process.env.OLLAMA_URL ?? "http://127.0.0.1:11434";
  const server = createWebServer(process.env.AGENT_SHARED_KEY ?? "", async (messages, tools, signal) =>
    createCatalogModel(signal, endpoint).bindTools(tools).stream(messages, { signal, callbacks: [] }));
  server.listen(8080, "0.0.0.0", () => console.log("Internal chat agent listening on port 8080; tracing disabled."));
}
