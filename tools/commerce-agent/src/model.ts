import { ChatOllama } from "@langchain/ollama";

export const modelName = "qwen3.8:27b";

export function createCatalogModel(signal: AbortSignal): ChatOllama {
  return new ChatOllama({
    baseUrl: "http://127.0.0.1:11434",
    model: modelName,
    checkOrPullModel: false,
    think: true,
    temperature: 0,
    seed: 42,
    numCtx: 8192,
    numPredict: 2048,
    keepAlive: "10m",
    maxRetries: 0,
    maxConcurrency: 1,
    // Abort even if Ollama stalls before producing its first response chunk.
    fetch: (input, init) => fetch(input, {
      ...init,
      signal: AbortSignal.any([signal, ...(init?.signal ? [init.signal] : [])]),
    }),
  });
}
