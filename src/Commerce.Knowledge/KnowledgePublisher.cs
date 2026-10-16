using System.Text.Json;
using Commerce.Domain;
using Commerce.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Knowledge;

// This service is used only by the operator CLI. No shopper route can publish knowledge.
public sealed class KnowledgePublisher(CommerceDbContext db, IKnowledgeEmbeddings embeddings, TimeProvider clock)
{
    public async Task<KnowledgeRelease> PublishAsync(KnowledgeBundle bundle, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 100) throw new InvalidDataException("Provide an operator identity (1–100 characters).");
        var identity = await embeddings.IdentityAsync(ct);
        var existing = await db.KnowledgeReleases.AsNoTracking().SingleOrDefaultAsync(r => r.ContentHash == bundle.ContentHash && r.ApplicationRevision == bundle.ApplicationRevision && r.ModelIdentity == identity, ct);
        var prepared = new List<KnowledgeChunk>();
        if (existing is null)
        {
            foreach (var batch in bundle.Chunks.Chunk(16))
            {
                var vectors = await embeddings.EmbedAsync(batch.Select(c => $"title: {c.Title} | text: {c.Body}").ToArray(), ct);
                prepared.AddRange(batch.Select((c, index) => new KnowledgeChunk { Id = Guid.NewGuid(), DocumentId = c.DocumentId,
                    Title = c.Title, Body = c.Body, SearchText = c.SearchText, LinksJson = JsonSerializer.Serialize(c.Links, KnowledgeBundle.Json),
                    Ordinal = c.Ordinal, ReviewedAt = c.ReviewedAt, Embedding = vectors[index] }));
            }
        }
        if (identity != await embeddings.IdentityAsync(ct)) throw new InvalidOperationException("Embedding model changed during publication. Nothing was activated.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(73102791)", ct);
        existing = await db.KnowledgeReleases.SingleOrDefaultAsync(r => r.ContentHash == bundle.ContentHash && r.ApplicationRevision == bundle.ApplicationRevision && r.ModelIdentity == identity, ct);
        if (existing is null)
        {
            existing = new() { Id = Guid.NewGuid(), ContentHash = bundle.ContentHash, ApplicationRevision = bundle.ApplicationRevision,
                ModelIdentity = identity, PublishedAt = clock.GetUtcNow(), PublishedBy = actor, ChunkCount = prepared.Count };
            db.KnowledgeReleases.Add(existing);
            foreach (var chunk in prepared) { chunk.ReleaseId = existing.Id; db.KnowledgeChunks.Add(chunk); }
            await db.SaveChangesAsync(ct);
        }
        var indexRow = await db.KnowledgeIndexes.SingleOrDefaultAsync(i => i.Name == "site", ct);
        if (indexRow is null) db.KnowledgeIndexes.Add(new() { ActiveReleaseId = existing.Id });
        else indexRow.ActiveReleaseId = existing.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return existing;
    }
}
