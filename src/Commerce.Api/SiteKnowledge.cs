using Commerce.Infrastructure;
using Commerce.Knowledge;

namespace Commerce.Api;

public sealed class SiteKnowledge(CommerceDbContext db, IKnowledgeEmbeddings embeddings, IWebHostEnvironment environment,
    ILogger<SiteKnowledge> logger) : IKnowledgeRetriever
{
    public static string Query(string input, string? preceding)
    {
        var normalized = input.Trim().TrimEnd('?', '.', '!').ToLowerInvariant();
        var refersBack = normalized is "which page" or "where" or "hangi sayfa" or "hangi sayfada" or "nerede" or "nasıl açabilirim"
            || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\b(it|that|there|those|these|them)\b");
        return input.Length < 100 && preceding is not null && refersBack ? preceding + "\nFollow-up: " + input : input;
    }

    public async Task<KnowledgeResult> SearchAsync(string query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "knowledge");
            // Local source runs validate source drift on every turn; deployed images validate at build time.
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../.."));
            var bundle = KnowledgeBundle.Load(directory, File.Exists(Path.Combine(root, "CommercePilot.slnx")) ? root : null);
            return await new KnowledgeRetriever(db, embeddings, bundle.ApplicationRevision).SearchAsync(query, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Site knowledge retrieval unavailable; website claims must be withheld.");
            return new("unavailable", null, []);
        }
    }
}
