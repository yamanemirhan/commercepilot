using System.Net;
using System.Net.Http.Json;
using Commerce.Infrastructure;
using Commerce.Knowledge;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class KnowledgeEndpointTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    [Fact]
    public async Task Retrieval_requires_authentication_csrf_and_an_unlocked_account()
    {
        using var anonymous = fixture.Client();
        using var denied = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, "/api/knowledge/search", new { query = "addresses" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var (api, owner) = await fixture.SignedInAsync();
        using (api)
        {
            using var noCsrf = await api.PostAsJsonAsync("/api/knowledge/search", new { query = "addresses" });
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            using var scope = fixture.Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
            var current = (await users.FindByIdAsync(owner.Id))!;
            await users.SetLockoutEnabledAsync(current, true);
            await users.SetLockoutEndDateAsync(current, DateTimeOffset.UtcNow.AddHours(1));
            using var locked = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/knowledge/search", new { query = "addresses" });
            Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_queries_are_rejected(string? query)
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        using (var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/knowledge/search", new { query }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_and_body_limits_are_enforced_and_unavailable_is_explicit()
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            using var tooLong = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/knowledge/search", new { query = new string('a', 2001) });
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            var csrf = (await api.GetFromJsonAsync<CommerceFixture.Csrf>("/api/auth/csrf"))!;
            using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/knowledge/search");
            oversizedRequest.Headers.Add("X-CSRF-Token", csrf.Token);
            var largeJson = System.Text.Json.JsonSerializer.Serialize(new { query = new string('a', 17000) });
            oversizedRequest.Content = new StringContent(largeJson, System.Text.Encoding.UTF8, "application/json");
            oversizedRequest.Content.Headers.ContentLength = System.Text.Encoding.UTF8.GetByteCount(largeJson);
            using var tooLarge = await api.SendAsync(oversizedRequest);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
            using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/knowledge/search", new { query = "addresses" });
            response.EnsureSuccessStatusCode();
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            var result = (await response.Content.ReadFromJsonAsync<KnowledgeResult>())!;
            Assert.Equal("unavailable", result.Status); Assert.Empty(result.Sources);
        }
    }

    [Fact]
    public async Task Endpoint_passes_only_the_query_to_the_existing_retriever_and_returns_release_sources()
    {
        var fake = new ReviewedKnowledge();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IKnowledgeRetriever>(fake)));
        using var api = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var user = new CommerceUser { Email = email, UserName = email, DisplayName = "Knowledge test" };
        var password = "Knowledge!" + Guid.NewGuid().ToString("N") + "9";
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        using var login = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/auth/login", new { email = user.Email, password });
        login.EnsureSuccessStatusCode();
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/knowledge/search", new { query = "  addresses  ", userId = "someone-else" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("addresses", fake.Query);
        var result = (await response.Content.ReadFromJsonAsync<KnowledgeResult>())!;
        Assert.Equal(fake.Release, result.ReleaseId);
        Assert.Equal("/orders", result.Sources.Single().Links.Single().Path);
    }

    private sealed class ReviewedKnowledge : IKnowledgeRetriever
    {
        public Guid Release { get; } = Guid.NewGuid();
        public string? Query { get; private set; }
        public Task<KnowledgeResult> SearchAsync(string query, CancellationToken ct)
        {
            Query = query;
            return Task.FromResult(new KnowledgeResult("available", Release,
                [new("addresses", "Addresses", "There is no address book.", [new("Orders", "/orders")], DateTimeOffset.UtcNow)]));
        }
    }
}
