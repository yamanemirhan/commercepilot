using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commerce.Knowledge;

public sealed record KnowledgeLink(string Label, string Path);
public sealed record KnowledgeDocument(string Id, string Title, string Body, string[] Keywords,
    KnowledgeLink[] Links, DateTimeOffset ReviewedAt);
public sealed record KnowledgeManifest(int SchemaVersion, string[] WatchedFiles, string[] Routes);
public sealed record KnowledgeReview(string ContentHash, string ApplicationRevision, Dictionary<string, string> Files);
public sealed record DocumentChunk(string DocumentId, string Title, string Body, string SearchText,
    KnowledgeLink[] Links, int Ordinal, DateTimeOffset ReviewedAt);

public sealed record KnowledgeBundle(string ContentHash, string ApplicationRevision, KnowledgeDocument[] Documents,
    DocumentChunk[] Chunks)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));

    public static KnowledgeBundle Load(string directory, string? repositoryRoot = null, bool review = false)
    {
        directory = Path.GetFullPath(directory);
        var manifestText = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        var manifest = JsonSerializer.Deserialize<KnowledgeManifest>(manifestText, Json) ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.SchemaVersion != 1 || manifest.WatchedFiles is not { Length: > 0 } || manifest.Routes is not { Length: > 0 })
            throw new InvalidDataException("Invalid knowledge manifest.");
        if (manifest.Routes.Any(r => !SafePath(r))) throw new InvalidDataException("Only local page routes are allowed.");
        var files = Directory.GetFiles(Path.Combine(directory, "documents"), "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 200) throw new InvalidDataException("Publish between 1 and 200 public documents.");
        var documents = new List<KnowledgeDocument>(); var parts = new List<string> { manifestText };
        foreach (var path in files)
        {
            if (new FileInfo(path).Length > 40_000) throw new InvalidDataException("Knowledge document exceeds 40 KB.");
            var text = File.ReadAllText(path); parts.Add(text);
            var doc = JsonSerializer.Deserialize<KnowledgeDocument>(text, Json) ?? throw new InvalidDataException("Invalid document.");
            if (doc.Id is null || !Regex.IsMatch(doc.Id, "^[a-z][a-z0-9-]{1,79}$") || Path.GetFileNameWithoutExtension(path) != doc.Id ||
                string.IsNullOrWhiteSpace(doc.Title) || doc.Title.Length > 160 || string.IsNullOrWhiteSpace(doc.Body) || doc.Body.Length > 20_000 ||
                doc.Keywords is null || doc.Keywords.Length > 20 || doc.Keywords.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 60) || doc.Links is null || doc.Links.Length > 10 ||
                doc.Links.Any(l => l is null || string.IsNullOrWhiteSpace(l.Label) || l.Label.Length > 100 || !manifest.Routes.Contains(l.Path)) ||
                doc.ReviewedAt > DateTimeOffset.UtcNow.AddDays(1) || doc.ReviewedAt < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
                throw new InvalidDataException($"Invalid public document: {doc.Id}.");
            documents.Add(doc);
        }
        if (documents.Select(d => d.Id).Distinct().Count() != documents.Count) throw new InvalidDataException("Duplicate document IDs.");
        var contentHash = Hash(string.Join("\n", parts));
        var lockPath = Path.Combine(directory, "review.lock.json");
        var locked = File.Exists(lockPath) ? JsonSerializer.Deserialize<KnowledgeReview>(File.ReadAllText(lockPath), Json) : null;
        Dictionary<string, string> evidence;
        if (repositoryRoot is not null)
        {
            var root = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
            evidence = new(StringComparer.Ordinal);
            var watched = manifest.WatchedFiles.SelectMany(relative =>
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Split('/', '\\').Any(p => p is ".git" or ".local" or ".."))
                    throw new InvalidDataException("Watched sources must be inside the repository.");
                return relative.EndsWith('/') ? Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules" or ".next"))
                    .Where(p => Path.GetExtension(p) is ".cs" or ".tsx" or ".ts" or ".css")
                    .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')) : new[] { relative };
            }).Distinct().Order(StringComparer.Ordinal);
            foreach (var relative in watched)
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).StartsWith('.') ||
                    relative.Split('/', '\\').Any(p => p is ".git" or ".local" or "..") || !File.Exists(full))
                    throw new InvalidDataException($"Invalid watched source: {relative}.");
                evidence.Add(relative, Hash(File.ReadAllText(full)));
            }
        }
        else evidence = locked?.Files ?? throw new InvalidDataException("Review lock is required.");
        var applicationRevision = Hash(JsonSerializer.Serialize(evidence.OrderBy(p => p.Key), Json));
        var chunks = documents.SelectMany(Chunk).ToArray();
        if (chunks.Length > 1000) throw new InvalidDataException("This knowledge collection is limited to 1,000 chunks.");
        if (review)
        {
            if (repositoryRoot is null) throw new InvalidDataException("Review requires the repository source.");
            File.WriteAllText(lockPath, JsonSerializer.Serialize(new KnowledgeReview(contentHash, applicationRevision, evidence), Json) + "\n");
        }
        else if (locked is null || locked.ContentHash != contentHash || locked.ApplicationRevision != applicationRevision)
            throw new InvalidDataException("Site knowledge is not reviewed for these sources. Update the documents, then run the review command.");
        return new(contentHash, applicationRevision, documents.ToArray(), chunks);
    }

    public static bool SafePath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith('/') && !path.StartsWith("//") &&
        !path.Contains('\\') && !path.Contains('%') && !path.Contains("..") && !path.Any(char.IsControl);

    private static IEnumerable<DocumentChunk> Chunk(KnowledgeDocument document)
    {
        var buffer = new StringBuilder(); var ordinal = 0;
        foreach (var paragraph in document.Body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (paragraph.Length > 1800) throw new InvalidDataException($"Split long paragraphs in {document.Id} before publishing.");
            if (buffer.Length + paragraph.Length + 2 > 1800)
            {
                yield return Make(buffer.ToString(), ordinal++); buffer.Clear();
            }
            if (buffer.Length > 0) buffer.Append("\n\n");
            buffer.Append(paragraph);
        }
        if (buffer.Length > 0) yield return Make(buffer.ToString(), ordinal);
        DocumentChunk Make(string body, int number) => new(document.Id, document.Title, body,
            document.Title + " " + string.Join(' ', document.Keywords) + " " + body, document.Links, number, document.ReviewedAt);
    }
}
