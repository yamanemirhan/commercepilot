namespace Commerce.Domain;

public sealed class KnowledgeRelease
{
    public Guid Id { get; set; }
    public string ContentHash { get; set; } = "";
    public string ApplicationRevision { get; set; } = "";
    public string ModelIdentity { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public string PublishedBy { get; set; } = "";
    public int ChunkCount { get; set; }
}

public sealed class KnowledgeIndex
{
    public string Name { get; set; } = "site";
    public Guid ActiveReleaseId { get; set; }
}

public sealed class KnowledgeChunk
{
    public Guid Id { get; set; }
    public Guid ReleaseId { get; set; }
    public string DocumentId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string SearchText { get; set; } = "";
    public string LinksJson { get; set; } = "[]";
    public int Ordinal { get; set; }
    public DateTimeOffset ReviewedAt { get; set; }
    public float[] Embedding { get; set; } = [];
}
