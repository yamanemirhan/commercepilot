# Reviewed site knowledge

This collection describes public site behavior. It contains no shopper records, credentials, prices or inventory snapshots. Live commerce data is read through authorized MCP tools. The documents are runtime product content, not development plans.

## Update and publish

1. Change the site and update the relevant English JSON documents in `documents/`. Check the actual UI and API behavior; update `reviewedAt`. Keep paragraphs below 1,800 characters. Links must be real local routes listed in `manifest.json`.
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/knowledge.ps1 review` **after reviewing the content**. This records an operator attestation, not an automated factual review. It fingerprints the watched source directories, including newly added or deleted files.
3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/knowledge.ps1 validate`. CI and the API Docker build run the same check. Do not regenerate the review lock automatically in CI.
4. Build and deploy the reviewed application (`docker compose up -d --build` locally). Its bundled review lock pins the source revision. Install the embedding model once with `ollama pull embeddinggemma:latest` and leave Ollama running.
5. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/knowledge.ps1 publish -Value "operator-name"`. The embedding model digest is checked before and after preparation. A PostgreSQL transaction switches the active release only after every chunk is ready. Failed publication leaves the previous release active.
6. Check `scripts/knowledge.ps1 status` and `scripts/knowledge.ps1 query -Value "Where are my orders?"`. Check representative English and Turkish questions in chat, including follow-ups and unsupported features.

Changes to `documents/` alone need a new review and publication but do not change the application source revision; an already deployed application can consume that release immediately. Changes to watched source files also need a matching application deployment. During that transition, mismatched application/knowledge versions withhold site guidance rather than serving stale instructions. The compatibility check is intentionally conservative; it also catches styling changes.

The PowerShell wrapper reads local database credentials from the ignored `.env` file without printing them. For deployment jobs, invoke `dotnet run --project tools/Commerce.Knowledge -- publish operator-name` from the repository root with `ConnectionStrings__Commerce` supplied by your secret store. `OLLAMA_URL` defaults to the loopback service and permits only loopback or `host.docker.internal`. The publishing identity is an audit label, not authentication: restrict access to the publishing job and database credentials. There is no HTTP publication endpoint.

## Retrieval and failure behavior

The local EmbeddingGemma model produces 768-dimensional multilingual vectors. PostgreSQL 18 with pgvector stores immutable releases and chunks. Query-time cosine similarity and PostgreSQL full-text ranking are combined using reciprocal rank fusion. Search is exact over at most 1,000 chunks; at this bounded corpus size there is no approximate-index recall tradeoff. At most two distinct documents enter a turn. Similarity thresholds are heuristics, not a factual guarantee; test your own queries when changing content or models.

The API checks the active release on every turn, with no cross-turn retrieval cache. Short follow-ups include the previous completed user question. Matched text is inserted as reference data, never system instructions. The current source version supersedes historical assistant claims. Source cards retain the retrieved excerpt, links, review date and release ID for that turn; old cards are historical snapshots. A source card means retrieved evidence, not proof that the generated answer used every source correctly.

No published index, a source mismatch, a model digest mismatch, an embedding failure or a 20-second retrieval timeout yields an unavailable reference result. The assistant is instructed to withhold unverifiable site claims while continuing supported commerce tasks. Empty search results are explicitly marked no-match. RAG reduces unsupported answers but cannot guarantee perfect model behavior; source review and regression evaluations remain necessary.

Publication reuses an identical content/source/model release. Removed documents are absent from the new active release. Previous releases remain in PostgreSQL for audit and rollback; no automatic retention policy is enabled. Re-publish a previously reviewed bundle with the matching application and model to roll back. Back up PostgreSQL and set a retention policy appropriate to your deployment before long-term production use. Do not delete the active release.

The Compose PostgreSQL image extends the same Alpine base with pgvector. Existing database data is preserved; back up before upgrading the base image or extension. Managed production PostgreSQL must support the `vector` extension, installed by a deployment role before applying the migration. Application and publication credentials should be separate least-privilege roles in a hosted deployment. Compose remains a local development configuration.

References: [Ollama embeddings API](https://docs.ollama.com/api/embed), [EmbeddingGemma model card](https://ai.google.dev/gemma/docs/embeddinggemma/model_card), [pgvector](https://github.com/pgvector/pgvector).
