using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Application;
using Commerce.Infrastructure;
using Commerce.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class McpApprovalTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
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

    private static HttpClientTransport Transport(HttpClient http, string? token)
    {
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
    }

    private static async Task<string> TokenAsync(HttpClient api, string path = "/api/auth/mcp-checkout-token")
    {
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, path,
            new { userId = "someone-else", scope = "orders:confirm cart:write" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<McpAccessToken>())!;
        if (path == "/api/auth/mcp-checkout-token") Assert.Equal("orders:prepare actions:read", token.Scope);
        if (path == "/api/auth/mcp-actions-token") Assert.Equal("actions:read", token.Scope);
        return token.AccessToken;
    }

    private static PendingActionDto Result(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        return JsonSerializer.Deserialize<PendingActionDto>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<PendingActionDto> Prepare(McpClient client, string mode = "success") =>
        Result(await client.CallToolAsync("prepare_order", new Dictionary<string, object?> { ["paymentMode"] = mode }));

    private static async Task<PendingActionDto> Read(McpClient client, Guid id) =>
        Result(await client.CallToolAsync("get_pending_action_status", new Dictionary<string, object?> { ["actionId"] = id }));

    private async Task<(Guid Id, decimal Price)> AddAsync(string owner)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var product = await db.Products.FirstAsync(p => p.Stock >= 10);
        Assert.Null((await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(owner, product.Id, 1, default)).Error);
        return (product.Id, product.Price);
    }

    private async Task<string> CommerceStateAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        return JsonSerializer.Serialize(new
        {
            Products = await db.Products.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Stock, p.Price }).ToArrayAsync(),
            Cart = await db.CartItems.AsNoTracking().OrderBy(c => c.UserId).ThenBy(c => c.ProductId).Select(c => new { c.UserId, c.ProductId, c.Quantity }).ToArrayAsync(),
            Orders = await db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.Status, o.PaymentStatus, o.CapturedAmount, o.RefundedAmount, Events = o.Events.Count }).ToArrayAsync()
        });
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("/api/auth/mcp-token")]
    [InlineData("/api/auth/mcp-cart-token")]
    [InlineData("/api/auth/mcp-orders-token")]
    [InlineData("/api/auth/mcp-actions-token")]
    public async Task Preparation_requires_explicit_scope_even_if_the_tool_name_is_known(string grant)
    {
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        var token = grant == "anonymous" ? null : await TokenAsync(api, grant);
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, token);
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "prepare_order");
        await Assert.ThrowsAnyAsync<McpException>(async () => await Prepare(client));
        if (grant != "/api/auth/mcp-actions-token")
        {
            Assert.DoesNotContain(tools, t => t.Name == "get_pending_action_status");
            await Assert.ThrowsAnyAsync<McpException>(async () => await Read(client, Guid.NewGuid()));
        }
        else Assert.Contains(tools, t => t.Name == "get_pending_action_status");
    }

    [Theory]
    [InlineData("/api/auth/mcp-checkout-token")]
    [InlineData("/api/auth/mcp-actions-token")]
    public async Task Grants_require_session_and_csrf(string path)
    {
        using var anonymous = fixture.Client();
        using var denied = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, path);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        using var noCsrf = await api.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await TokenAsync(api, path);
    }

    [Fact]
    public async Task Preparation_uses_own_bag_and_server_prices_without_executing_checkout()
    {
        var (api, owner) = await fixture.SignedInAsync(); using var shopper = api;
        var product = await AddAsync(owner.Id);
        var before = await CommerceStateAsync();
        using var factory = Factory(); using var http = factory.CreateClient();
        var token = await TokenAsync(api);
        await using var transport = Transport(http, token);
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "get_pending_action_status", "get_product", "prepare_order", "search_products" }, tools.Select(t => t.Name).Order().ToArray());
        Assert.Equal("paymentMode", Assert.Single(Assert.Single(tools, t => t.Name == "prepare_order").JsonSchema.GetProperty("properties").EnumerateObject()).Name);
        Assert.Equal("actionId", Assert.Single(Assert.Single(tools, t => t.Name == "get_pending_action_status").JsonSchema.GetProperty("properties").EnumerateObject()).Name);
        var review = Result(await client.CallToolAsync("prepare_order", new Dictionary<string, object?>
        {
            ["paymentMode"] = "success", ["userId"] = "someone-else", ["total"] = 0.01, ["confirmed"] = true
        }));
        Assert.Equal("Pending", review.Status);
        Assert.Equal("Checkout", review.Kind);
        Assert.Null(review.OrderId);
        Assert.Equal(product.Price, review.Snapshot.Total);
        Assert.Equal(product.Id, Assert.Single(review.Snapshot.Items).ProductId);
        Assert.Equal(TimeSpan.FromMinutes(5), review.ExpiresAt - review.CreatedAt);
        Assert.Contains((await api.GetFromJsonAsync<PendingActionDto[]>("/api/actions"))!, a => a.Id == review.Id);
        var persisted = await Read(client, review.Id);
        Assert.Equal(review.Id, persisted.Id);
        Assert.Equal(review.Status, persisted.Status);
        Assert.Equal(review.SnapshotHash, persisted.SnapshotHash);
        Assert.Equal(JsonSerializer.Serialize(review.Snapshot), JsonSerializer.Serialize(persisted.Snapshot));
        // PostgreSQL timestamps retain microseconds; .NET timestamps also contain sub-microsecond ticks.
        Assert.InRange(Math.Abs((review.CreatedAt - persisted.CreatedAt).Ticks), 0, 9);
        Assert.InRange(Math.Abs((review.ExpiresAt - persisted.ExpiresAt).Ticks), 0, 9);
        foreach (var forbidden in new[] { "confirm_action", "reject_action", "prepare_cancellation", "prepare_refund", "add_to_cart" })
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(forbidden, new Dictionary<string, object?> { ["actionId"] = review.Id }));
        // Knowing the snapshot hash and carrying an MCP credential cannot authorize HTTP confirmation.
        using var bearerOnly = fixture.Client();
        bearerOnly.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var denied = await CommerceFixture.MutateAsync(bearerOnly, HttpMethod.Post, $"/api/actions/{review.Id}/confirm", new { review.SnapshotHash });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var noCsrf = await api.PostAsJsonAsync($"/api/actions/{review.Id}/confirm", new { review.SnapshotHash });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal(before, await CommerceStateAsync());
    }

    [Fact]
    public async Task Read_only_status_is_owner_bound_and_tracks_human_decisions()
    {
        var (api, owner) = await fixture.SignedInAsync(); using var shopper = api;
        var (otherApi, other) = await fixture.SignedInAsync(); using var otherShopper = otherApi;
        await AddAsync(owner.Id); await AddAsync(other.Id);
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, await TokenAsync(api));
        await using var client = await McpClient.CreateAsync(transport);
        var first = await Prepare(client);
        var second = await Prepare(client, "failure");
        Assert.NotEqual(first.Id, second.Id);
        var superseded = await Read(client, first.Id);
        Assert.Equal("Superseded", superseded.Status);
        Assert.Equal(first.SnapshotHash, superseded.SnapshotHash);
        Assert.Equal(JsonSerializer.Serialize(first.Snapshot), JsonSerializer.Serialize(superseded.Snapshot));
        using var rejected = await CommerceFixture.MutateAsync(api, HttpMethod.Post, $"/api/actions/{second.Id}/reject");
        rejected.EnsureSuccessStatusCode();
        var third = await Prepare(client, "failure");
        using var confirmed = await CommerceFixture.MutateAsync(api, HttpMethod.Post, $"/api/actions/{third.Id}/confirm", new { third.SnapshotHash });
        confirmed.EnsureSuccessStatusCode();
        using var foreignResponse = await CommerceFixture.MutateAsync(otherApi, HttpMethod.Post, "/api/orders/prepare", new { paymentMode = "success" });
        foreignResponse.EnsureSuccessStatusCode();
        var foreignReview = (await foreignResponse.Content.ReadFromJsonAsync<PendingActionDto>())!;
        using var readHttp = factory.CreateClient();
        await using var readTransport = Transport(readHttp, await TokenAsync(api, "/api/auth/mcp-actions-token"));
        await using var reader = await McpClient.CreateAsync(readTransport);
        var before = await CommerceStateAsync();
        Assert.Equal("Rejected", (await Read(reader, second.Id)).Status);
        var completed = await Read(reader, third.Id);
        Assert.Equal("Completed", completed.Status);
        Assert.NotNull(completed.OrderId);
        var order = (await api.GetFromJsonAsync<OrderDto>($"/api/orders/{completed.OrderId}"))!;
        Assert.Equal("PaymentFailed", order.Status);
        var foreign = await reader.CallToolAsync("get_pending_action_status", new Dictionary<string, object?> { ["actionId"] = foreignReview.Id, ["userId"] = other.Id });
        var missing = await reader.CallToolAsync("get_pending_action_status", new Dictionary<string, object?> { ["actionId"] = Guid.NewGuid() });
        Assert.True(foreign.IsError); Assert.True(missing.IsError);
        Assert.Null(foreign.StructuredContent);
        Assert.Equal(Assert.Single(foreign.Content.OfType<TextContentBlock>()).Text, Assert.Single(missing.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(before, await CommerceStateAsync());
    }

    [Fact]
    public async Task Expiry_is_reported_without_execution_and_expired_review_cannot_be_confirmed()
    {
        var (api, owner) = await fixture.SignedInAsync(); using var shopper = api;
        await AddAsync(owner.Id);
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, await TokenAsync(api));
        await using var client = await McpClient.CreateAsync(transport);
        var review = await Prepare(client);
        var before = await CommerceStateAsync();
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(6));
        // The old bearer credential also expired; obtain a new read-only credential.
        using var readHttp = factory.CreateClient();
        await using var readTransport = Transport(readHttp, await TokenAsync(api, "/api/auth/mcp-actions-token"));
        await using var reader = await McpClient.CreateAsync(readTransport);
        Assert.Equal("Expired", (await Read(reader, review.Id)).Status);
        Assert.Equal(before, await CommerceStateAsync());
        using var denied = await CommerceFixture.MutateAsync(api, HttpMethod.Post, $"/api/actions/{review.Id}/confirm", new { review.SnapshotHash });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(before, await CommerceStateAsync());
    }

    [Fact]
    public async Task Invalid_outcome_empty_bag_and_unavailable_stock_return_tool_errors_without_reviews()
    {
        var (api, owner) = await fixture.SignedInAsync(); using var shopper = api;
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, await TokenAsync(api));
        await using var client = await McpClient.CreateAsync(transport);
        foreach (var mode in new[] { "unsupported", "success" })
        {
            var result = await client.CallToolAsync("prepare_order", new Dictionary<string, object?> { ["paymentMode"] = mode });
            Assert.True(result.IsError);
            Assert.Contains("OrderPreparationFailed:", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        }
        var product = await AddAsync(owner.Id);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var row = (await db.Products.FindAsync(product.Id))!;
        row.Stock = 0;
        await db.SaveChangesAsync();
        var before = await CommerceStateAsync();
        var unavailable = await client.CallToolAsync("prepare_order", new Dictionary<string, object?> { ["paymentMode"] = "success" });
        Assert.True(unavailable.IsError);
        Assert.Contains("unavailable", Assert.Single(unavailable.Content.OfType<TextContentBlock>()).Text);
        Assert.False(await db.PendingActions.AnyAsync(a => a.UserId == owner.Id));
        Assert.Equal(before, await CommerceStateAsync());
    }
}
