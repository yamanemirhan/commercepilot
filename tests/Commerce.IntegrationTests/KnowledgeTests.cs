using System.Text.Json;
using Commerce.Infrastructure;
using Commerce.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class KnowledgeTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private static KnowledgeBundle Bundle(string version, string body, string revision = "revision") => new(
        KnowledgeBundle.Hash(version), KnowledgeBundle.Hash(revision), [],
        [new("addresses", "Addresses", body, "addresses " + body, [new("Orders", "/orders")], 0, DateTimeOffset.UtcNow)]);

    [Fact]
    public async Task Publication_is_atomic_versioned_idempotent_and_retrieval_rejects_stale_sources_or_model()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var embeddings = new FakeEmbeddings();
        var publisher = new KnowledgePublisher(db, embeddings, TimeProvider.System);
        var first = Bundle("v1", "There is no address book.");
        var release = await publisher.PublishAsync(first, "test", default);
        Assert.Equal(release.Id, (await publisher.PublishAsync(first, "test", default)).Id);
        var search = new KnowledgeRetriever(db, embeddings, first.ApplicationRevision);
        Assert.Contains("no address book", (await search.SearchAsync("addresses", default)).Sources.Single().Excerpt);
        embeddings.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(Bundle("v2", "New guidance"), "test", default));
        Assert.Equal(release.Id, (await db.KnowledgeIndexes.AsNoTracking().SingleAsync()).ActiveReleaseId);
        embeddings.Fail = false;
        var replacement = await publisher.PublishAsync(Bundle("v2", "Use the order detail destination."), "test", default);
        Assert.NotEqual(release.Id, replacement.Id);
        Assert.DoesNotContain("address book", (await search.SearchAsync("addresses", default)).Sources.Single().Excerpt);
        Assert.Equal("unavailable", (await new KnowledgeRetriever(db, embeddings, "outdated").SearchAsync("addresses", default)).Status);
        embeddings.Identity = "embeddinggemma:latest@" + new string('b', 64);
        Assert.Equal("unavailable", (await search.SearchAsync("addresses", default)).Status);
        embeddings.Identity = "embeddinggemma:latest@" + new string('a', 64);
        var newerSources = await publisher.PublishAsync(Bundle("v2", "Use the order detail destination.", "new-revision"), "test", default);
        Assert.NotEqual(replacement.Id, newerSources.Id);
        Assert.Equal("unavailable", (await search.SearchAsync("addresses", default)).Status);
        // Explicit rollback reactivates the original reviewed release without rebuilding embeddings.
        await publisher.PublishAsync(first, "test", default);
        Assert.Equal(release.Id, (await search.SearchAsync("addresses", default)).ReleaseId);
    }

    [Fact]
    public void Review_detects_new_changed_deleted_sources_and_changed_documents_and_rejects_external_links()
    {
        var root = Path.Combine(Path.GetTempPath(), "commerce-knowledge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "web"));
        var docs = Path.Combine(root, "knowledge"); Directory.CreateDirectory(Path.Combine(docs, "documents"));
        try
        {
            File.WriteAllText(Path.Combine(root, "web", "page.tsx"), "orders");
            File.WriteAllText(Path.Combine(docs, "manifest.json"), JsonSerializer.Serialize(new KnowledgeManifest(1, ["web/"], ["/orders"]), KnowledgeBundle.Json));
            var document = new KnowledgeDocument("orders", "Orders", "Use the order list.", ["orders"], [new("Orders", "/orders")], DateTimeOffset.UtcNow);
            var docPath = Path.Combine(docs, "documents", "orders.json");
            File.WriteAllText(docPath, JsonSerializer.Serialize(document, KnowledgeBundle.Json));
            var reviewed = KnowledgeBundle.Load(docs, root, true);
            Assert.Equal(reviewed.ContentHash, KnowledgeBundle.Load(docs, root).ContentHash);
            File.WriteAllText(Path.Combine(root, "web", "new.tsx"), "new route");
            Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Load(docs, root));
            File.Delete(Path.Combine(root, "web", "new.tsx"));
            File.WriteAllText(Path.Combine(root, "web", "page.tsx"), "changed route");
            Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Load(docs, root));
            KnowledgeBundle.Load(docs, root, true);
            File.Delete(Path.Combine(root, "web", "page.tsx"));
            Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Load(docs, root));
            File.WriteAllText(docPath, JsonSerializer.Serialize(document with { Body = "Changed guidance." }, KnowledgeBundle.Json));
            Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Load(docs));
            File.WriteAllText(docPath, JsonSerializer.Serialize(document with { Links = [new("Unsafe", "https://example.com")] }, KnowledgeBundle.Json));
            Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Load(docs, root, true));
        }
        finally { Directory.Delete(root, true); } // Only this test's unique temporary directory.
    }

    [Fact]
    public async Task No_match_is_not_presented_as_verified_evidence()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var embeddings = new FakeEmbeddings();
        var bundle = Bundle("unrelated", "Address book is unavailable.");
        await new KnowledgePublisher(db, embeddings, TimeProvider.System).PublishAsync(bundle, "test", default);
        embeddings.QueryOrthogonal = true;
        var result = await new KnowledgeRetriever(db, embeddings, bundle.ApplicationRevision).SearchAsync("volcano", default);
        Assert.Equal("no-match", result.Status); Assert.Empty(result.Sources);
    }

    [Fact]
    public void Topic_changes_do_not_retrieve_previous_question_as_evidence()
    {
        Assert.Equal("Show two Audio products.", Commerce.Api.SiteKnowledge.Query("Show two Audio products.", "Where are my orders?"));
        Assert.Contains("adreslerim", Commerce.Api.SiteKnowledge.Query("hangi sayfada?", "adreslerim var mı"));
        Assert.Contains("addresses", Commerce.Api.SiteKnowledge.Query("Where is it?", "Is there an addresses page?"));
    }

    private sealed class FakeEmbeddings : IKnowledgeEmbeddings
    {
        public bool Fail { get; set; }
        public bool QueryOrthogonal { get; set; }
        public string Identity { get; set; } = "embeddinggemma:latest@" + new string('a', 64);
        public Task<string> IdentityAsync(CancellationToken ct) => Task.FromResult(Identity);
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("Simulated failure.");
            return Task.FromResult(inputs.Select(_ => { var vector = new float[768]; vector[QueryOrthogonal ? 1 : 0] = 1; return vector; }).ToArray());
        }
    }
}
