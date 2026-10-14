using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Commerce.Application;
using Commerce.Infrastructure;
using Commerce.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class McpTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private WebApplicationFactory<McpProgram> Factory()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Database.GetConnectionString();
        return new WebApplicationFactory<McpProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Commerce", connection);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(fixture.Factory.Clock));
        });
    }

    private static HttpClientTransport Transport(HttpClient http) => new(new HttpClientTransportOptions
    {
        Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
    }, http);

    [Fact]
    public async Task Discovery_exposes_only_public_search_with_schema_and_no_owner_argument()
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http);
        await using var client = await McpClient.CreateAsync(transport);
        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal("search_products", tool.Name);
        var names = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("search", names); Assert.Contains("pageSize", names);
        Assert.DoesNotContain("userId", names); Assert.DoesNotContain("catalog", names);
        Assert.DoesNotContain("cancellationToken", names);
    }

    [Fact]
    public async Task Real_mcp_search_matches_database_filters_and_order_without_mutating_stock()
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http);
        await using var client = await McpClient.CreateAsync(transport);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var stock = await db.Products.SumAsync(p => p.Stock);
        var expected = await scope.ServiceProvider.GetRequiredService<ICatalogService>().SearchAsync(
            new(Category: "Audio", MaxPrice: 100, Sort: "price-asc", PageSize: 2, InStock: true), default);
        var result = await client.CallToolAsync("search_products", new Dictionary<string, object?>
        {
            ["category"] = "Audio", ["maxPrice"] = 100, ["sort"] = "price-asc", ["pageSize"] = 2, ["inStock"] = true
        });
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        var page = JsonSerializer.Deserialize<CatalogPage>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(expected.Total, page.Total);
        Assert.Equal(expected.Items.Select(p => p.Id), page.Items.Select(p => p.Id));
        Assert.Equal(expected.Items.Select(p => p.Price), page.Items.Select(p => p.Price));
        Assert.Equal(stock, await db.Products.SumAsync(p => p.Stock));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    public async Task Invalid_limits_return_a_tool_error_instead_of_an_unbounded_query(int limit)
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http);
        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync("search_products", new Dictionary<string, object?> { ["pageSize"] = limit });
        Assert.True(result.IsError);
        Assert.Contains(result.Content.OfType<TextContentBlock>(), block => block.Text.Contains("between 1 and 48"));
    }

    [Fact]
    public async Task Confirmation_is_not_callable_even_when_the_client_guesses_its_name()
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http);
        await using var client = await McpClient.CreateAsync(transport);
        await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync("confirm_action", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task Browser_origins_and_untrusted_hosts_are_rejected()
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        using var origin = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        origin.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(origin)).StatusCode);
        using var host = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        host.Headers.Host = "untrusted.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(host)).StatusCode);
    }

    private static async Task<string> IssueAsync(HttpClient api)
    {
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, "/api/auth/mcp-token");
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var token = (await response.Content.ReadFromJsonAsync<McpAccessToken>())!;
        Assert.Equal(McpAccessTokens.CartRead, token.Scope);
        return token.AccessToken;
    }

    [Fact]
    public async Task Issuing_a_credential_requires_a_session_and_its_csrf_token()
    {
        using var anonymous = fixture.Client();
        using var denied = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, "/api/auth/mcp-token");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            using var noCsrf = await api.PostAsync("/api/auth/mcp-token", null);
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            await IssueAsync(api);
        }
    }

    [Fact]
    public async Task Anonymous_call_cannot_bypass_hidden_cart_tool()
    {
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http);
        await using var client = await McpClient.CreateAsync(transport);
        await AssertCartDeniedAsync(client);
    }

    private static async Task AssertCartDeniedAsync(McpClient client)
    {
        try
        {
            var result = await client.CallToolAsync("get_cart", new Dictionary<string, object?>());
            Assert.True(result.IsError);
            Assert.Null(result.StructuredContent);
        }
        catch (McpException) { /* The SDK may represent authorization denial as a protocol error. */ }
    }

    [Fact]
    public async Task Each_credential_reads_only_its_owner_even_with_a_forged_owner_argument()
    {
        var (alex, owner) = await fixture.SignedInAsync();
        var (sam, other) = await fixture.SignedInAsync();
        using (alex) using (sam)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var productId = await db.Products.Where(p => p.Stock > 2).Select(p => p.Id).FirstAsync();
            await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(owner.Id, productId, 2, default);
            var alexToken = await IssueAsync(alex);
            var samToken = await IssueAsync(sam);
            using var factory = Factory();
            foreach (var (token, count, forgedOwner) in new[] { (alexToken, 2, other.Id), (samToken, 0, owner.Id) })
            {
                using var http = factory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                await using var transport = Transport(http);
                await using var client = await McpClient.CreateAsync(transport);
                var listed = await client.ListToolsAsync();
                Assert.Equal(2, listed.Count);
                var tool = Assert.Single(listed, t => t.Name == "get_cart");
                Assert.Empty(tool.JsonSchema.GetProperty("properties").EnumerateObject());
                var result = await client.CallToolAsync("get_cart", new Dictionary<string, object?> { ["userId"] = forgedOwner });
                Assert.False(result.IsError is true);
                var cart = JsonSerializer.Deserialize<CartDto>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(count, cart.ItemCount);
            }
        }
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("security-stamp")]
    [InlineData("locked-out")]
    [InlineData("deleted")]
    public async Task Invalid_credentials_are_rejected_before_protocol_execution(string failure)
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        {
            var token = await IssueAsync(api);
            using var scope = fixture.Factory.Services.CreateScope();
            if (failure == "tampered") token = "invalid-" + token;
            if (failure == "expired") fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(6));
            if (failure == "security-stamp")
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
                Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.Id))!)).Succeeded);
            }
            if (failure == "locked-out")
                await scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Users.Where(u => u.Id == user.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(u => u.LockoutEnabled, true)
                        .SetProperty(u => u.LockoutEnd, fixture.Factory.Clock.GetUtcNow().AddHours(1)));
            if (failure == "deleted")
                await scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
            if (failure is "audience" or "issuer")
            {
                var format = TokenFormat(scope.ServiceProvider);
                var ticket = format.Unprotect(token)!;
                var identity = (ClaimsIdentity)ticket.Principal.Identity!;
                var type = failure == "audience" ? "aud" : "iss";
                identity.RemoveClaim(identity.FindFirst(type)!);
                identity.AddClaim(new Claim(type, "another-service"));
                token = format.Protect(ticket);
            }
            using var factory = Factory(); using var http = factory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.PostAsync("/mcp", null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static TicketDataFormat TokenFormat(IServiceProvider services) => new(
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector("CommercePilot.McpAccessToken", "v1", McpAccessTokens.Audience));

    [Fact]
    public async Task Valid_identity_without_cart_scope_cannot_discover_or_call_cart()
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            var token = await IssueAsync(api);
            using var scope = fixture.Factory.Services.CreateScope();
            var format = TokenFormat(scope.ServiceProvider);
            var ticket = format.Unprotect(token)!;
            var identity = (ClaimsIdentity)ticket.Principal.Identity!;
            identity.RemoveClaim(identity.FindFirst("scope")!);
            using var factory = Factory(); using var http = factory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", format.Protect(ticket));
            await using var transport = Transport(http);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.Equal("search_products", Assert.Single(await client.ListToolsAsync()).Name);
            await AssertCartDeniedAsync(client);
        }
    }
}
