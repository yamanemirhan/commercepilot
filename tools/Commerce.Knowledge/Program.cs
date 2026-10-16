using System.Text.Json;
using Commerce.Infrastructure;
using Commerce.Knowledge;
using Microsoft.EntityFrameworkCore;

try
{
    var command = args.FirstOrDefault() ?? "validate";
    var root = Environment.GetEnvironmentVariable("KNOWLEDGE_REPOSITORY") ?? Directory.GetCurrentDirectory();
    var directory = Path.Combine(root, "knowledge");
    var bundle = KnowledgeBundle.Load(directory, root, command == "review");
    if (command is "review" or "validate")
    {
        Console.WriteLine($"{command}: {bundle.Documents.Length} documents, {bundle.Chunks.Length} chunks; sources {bundle.ApplicationRevision[..12]}.");
        return 0;
    }
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Commerce")
        ?? throw new InvalidOperationException("ConnectionStrings__Commerce is required. Use scripts/knowledge.ps1 for local development.");
    await using var db = new CommerceDbContext(new DbContextOptionsBuilder<CommerceDbContext>().UseNpgsql(connection).Options);
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://127.0.0.1:11434/"), Timeout = TimeSpan.FromSeconds(90) };
    var embeddings = new KnowledgeEmbeddings(http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    if (command == "publish")
    {
        var actor = args.ElementAtOrDefault(1) ?? throw new InvalidDataException("Usage: publish <operator-name>");
        var release = await new KnowledgePublisher(db, embeddings, TimeProvider.System).PublishAsync(bundle, actor, timeout.Token);
        Console.WriteLine($"Published {release.Id}: {release.ChunkCount} chunks; model {release.ModelIdentity}; sources {release.ApplicationRevision[..12]}.");
    }
    else if (command == "status")
    {
        var release = await (from index in db.KnowledgeIndexes join version in db.KnowledgeReleases on index.ActiveReleaseId equals version.Id
            where index.Name == "site" select version).AsNoTracking().SingleOrDefaultAsync(timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { release, matchesReviewedSources = release?.ApplicationRevision == bundle.ApplicationRevision,
            matchesDocuments = release?.ContentHash == bundle.ContentHash }, KnowledgeBundle.Json));
    }
    else if (command == "query")
    {
        var query = string.Join(' ', args.Skip(1));
        Console.WriteLine(JsonSerializer.Serialize(await new KnowledgeRetriever(db, embeddings, bundle.ApplicationRevision).SearchAsync(query, timeout.Token), KnowledgeBundle.Json));
    }
    else throw new InvalidDataException("Commands: review, validate, publish <operator>, status, query <text>.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Knowledge operation failed ({ex.GetType().Name}): {ex.Message}");
    return 1;
}
