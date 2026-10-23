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

public sealed class McpOrderTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
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

    private static async Task<string> TokenAsync(HttpClient api, string path = "/api/auth/mcp-orders-token")
    {
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, path,
            new { userId = "someone-else", scope = "orders:confirm cart:write" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<McpAccessToken>())!;
        if (path == "/api/auth/mcp-orders-token") Assert.Equal("orders:read", token.Scope);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return token.AccessToken;
    }

    private static T Result<T>(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<Guid> CreateOrderAsync(string owner, string paymentMode)
    {
        // Test fixtures create approved orders through the real service in an isolated database.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var product = await db.Products.FirstAsync(p => p.Stock >= 10);
        Assert.Null((await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(owner, product.Id, 1, default)).Error);
        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var review = await orders.PrepareOrderAsync(owner, paymentMode, default);
        return (await orders.ConfirmAsync(owner, review.Id, review.SnapshotHash, default)).OrderId!.Value;
    }

    [Theory]
    [InlineData("get_my_orders", "anonymous")]
    [InlineData("get_order_status", "anonymous")]
    [InlineData("get_my_orders", "/api/auth/mcp-token")]
    [InlineData("get_order_status", "/api/auth/mcp-token")]
    [InlineData("get_my_orders", "/api/auth/mcp-cart-token")]
    [InlineData("get_order_status", "/api/auth/mcp-cart-token")]
    public async Task Order_tools_require_their_own_scope_for_discovery_and_invocation(string tool, string grant)
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            var token = grant == "anonymous" ? null : await TokenAsync(api, grant);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name == tool);
            var arguments = new Dictionary<string, object?>();
            if (tool == "get_order_status") arguments["orderId"] = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(tool, arguments));
        }
    }

    [Fact]
    public async Task Order_grant_requires_session_and_csrf_and_grants_no_cart_or_confirmation_access()
    {
        using var anonymous = fixture.Client();
        using var denied = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, "/api/auth/mcp-orders-token");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            using var noCsrf = await api.PostAsync("/api/auth/mcp-orders-token", null);
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            Assert.Equal(new[] { "get_my_orders", "get_order_status", "get_product", "search_products" }, tools.Select(t => t.Name).Order().ToArray());
            Assert.Empty(Assert.Single(tools, t => t.Name == "get_my_orders").JsonSchema.GetProperty("properties").EnumerateObject());
            Assert.Equal("orderId", Assert.Single(Assert.Single(tools, t => t.Name == "get_order_status").JsonSchema.GetProperty("properties").EnumerateObject()).Name);
            foreach (var tool in new[] { "get_cart", "confirm_action", "prepare_order" })
                await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(tool, new Dictionary<string, object?>()));
        }
    }

    [Fact]
    public async Task List_and_details_are_owner_bound_and_reads_do_not_advance_due_payments()
    {
        var (api, owner) = await fixture.SignedInAsync();
        var (otherApi, other) = await fixture.SignedInAsync();
        using (api) using (otherApi)
        {
            var delayedId = await CreateOrderAsync(owner.Id, "delay");
            fixture.Factory.Clock.Advance(TimeSpan.FromSeconds(1));
            var failedId = await CreateOrderAsync(owner.Id, "failure");
            var foreignId = await CreateOrderAsync(other.Id, "success");
            fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(10));
            var token = await TokenAsync(api);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var before = await DatabaseStateAsync(db);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            for (var i = 0; i < 2; i++)
            {
                var list = Result<MyOrdersResult>(await client.CallToolAsync("get_my_orders", new Dictionary<string, object?> { ["userId"] = other.Id }));
                Assert.Equal(100, list.Limit);
                Assert.Equal(new[] { failedId, delayedId }, list.Orders.Select(o => o.Id).ToArray());
                var detail = Result<OrderDto>(await client.CallToolAsync("get_order_status", new Dictionary<string, object?> { ["orderId"] = delayedId, ["userId"] = other.Id }));
                Assert.Equal("AwaitingPayment", detail.Status);
                Assert.Equal(0, detail.CapturedAmount);
                var apiOrder = await api.GetFromJsonAsync<OrderDto>($"/api/orders/{delayedId}");
                Assert.Equal(JsonSerializer.Serialize(apiOrder), JsonSerializer.Serialize(detail));
            }
            var foreign = await client.CallToolAsync("get_order_status", new Dictionary<string, object?> { ["orderId"] = foreignId });
            // Nullable fields must still be present to satisfy the advertised MCP output schema.
            var failed = await client.CallToolAsync("get_order_status", new Dictionary<string, object?> { ["orderId"] = failedId });
            Assert.False(failed.IsError is true);
            var failedJson = JsonSerializer.SerializeToElement(failed.StructuredContent);
            Assert.Equal(JsonValueKind.Null, failedJson.GetProperty("nextTransitionAt").ValueKind);
            var missing = await client.CallToolAsync("get_order_status", new Dictionary<string, object?> { ["orderId"] = Guid.NewGuid() });
            Assert.True(foreign.IsError); Assert.True(missing.IsError);
            Assert.Null(foreign.StructuredContent);
            Assert.Equal(Assert.Single(foreign.Content.OfType<TextContentBlock>()).Text, Assert.Single(missing.Content.OfType<TextContentBlock>()).Text);
            Assert.Equal(before, await DatabaseStateAsync(db));
        }
    }

    private static async Task<string> DatabaseStateAsync(CommerceDbContext db) => JsonSerializer.Serialize(new
    {
        Orders = await db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new
        {
            o.Id, o.Status, o.PaymentStatus, o.Version, o.CapturedAmount, o.RefundedAmount, o.NextTransitionAt, Events = o.Events.Count
        }).ToArrayAsync(),
        Stock = await db.Products.SumAsync(p => p.Stock),
        CartRows = await db.CartItems.CountAsync(),
        Actions = await db.PendingActions.CountAsync()
    });

    [Fact]
    public async Task Account_without_orders_receives_an_empty_successful_list()
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.Empty(Result<MyOrdersResult>(await client.CallToolAsync("get_my_orders", new Dictionary<string, object?>())).Orders);
        }
    }
}
