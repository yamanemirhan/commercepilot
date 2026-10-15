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

public sealed class McpOrderChangeTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private WebApplicationFactory<McpProgram> Factory()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Database.GetConnectionString();
        return new WebApplicationFactory<McpProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("ConnectionStrings:Commerce", connection);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(fixture.Factory.Clock));
        });
    }

    private static async Task<string> Token(HttpClient api, string path)
    {
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post, path, new { scope = "orders:confirm" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<McpAccessToken>())!.AccessToken;
    }
    private static HttpClientTransport Transport(HttpClient http, string? token)
    {
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
    }
    private static PendingActionDto Review(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        return JsonSerializer.Deserialize<PendingActionDto>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private async Task<Guid> Order(string owner, bool delivered)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var product = await db.Products.FirstAsync(p => p.Stock > 5);
        await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(owner, product.Id, 1, default);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var review = await service.PrepareOrderAsync(owner, "success", default);
        var id = (await service.ConfirmAsync(owner, review.Id, review.SnapshotHash, default)).OrderId!.Value;
        if (delivered)
        {
            for (var i = 0; i < 2; i++)
            {
                fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(3));
                await service.AdvanceAsync(owner, id, default);
            }
        }
        return id;
    }
    private async Task<string> State()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.Status, o.PaymentStatus, o.RefundedAmount, o.CapturedAmount, o.Version, Events = o.Events.Count }).ToArrayAsync(),
            Stock = await db.Products.SumAsync(p => p.Stock), Cart = await db.CartItems.CountAsync()
        });
    }

    [Theory]
    [InlineData("prepare_cancellation", "/api/auth/mcp-cancellation-token", false, "Cancellation")]
    [InlineData("prepare_refund", "/api/auth/mcp-refund-token", true, "Refund")]
    public async Task Own_eligible_order_produces_review_only_and_foreign_ids_do_not_leak(string tool, string grant, bool delivered, string kind)
    {
        var (api, user) = await fixture.SignedInAsync(); using var shopper = api;
        var (otherApi, other) = await fixture.SignedInAsync(); using var second = otherApi;
        var id = await Order(user.Id, delivered); var foreignId = await Order(other.Id, delivered);
        var before = await State();
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, await Token(api, grant));
        await using var client = await McpClient.CreateAsync(transport);
        var args = new Dictionary<string, object?> { ["orderId"] = id, ["userId"] = other.Id, ["amount"] = 999999, ["approved"] = true };
        var first = Review(await client.CallToolAsync(tool, args));
        var newer = Review(await client.CallToolAsync(tool, args));
        Assert.Equal(kind, first.Kind); Assert.Equal("Pending", newer.Status); Assert.Equal(id, newer.OrderId);
        Assert.NotEqual(first.Id, newer.Id);
        var old = Review(await client.CallToolAsync("get_pending_action_status", new Dictionary<string, object?> { ["actionId"] = first.Id }));
        Assert.Equal("Superseded", old.Status); Assert.Equal(first.SnapshotHash, old.SnapshotHash);
        var order = (await api.GetFromJsonAsync<OrderDto>($"/api/orders/{id}"))!;
        Assert.Equal(order.Total, newer.Snapshot.Total);
        var foreign = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["orderId"] = foreignId });
        var missing = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["orderId"] = Guid.NewGuid() });
        Assert.True(foreign.IsError); Assert.True(missing.IsError);
        Assert.Equal(Assert.Single(foreign.Content.OfType<TextContentBlock>()).Text, Assert.Single(missing.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(before, await State());
        foreach (var forbidden in new[] { "confirm_action", "reject_action", "prepare_order", delivered ? "prepare_cancellation" : "prepare_refund" })
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(forbidden, args));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("/api/auth/mcp-orders-token")]
    [InlineData("/api/auth/mcp-checkout-token")]
    [InlineData("/api/auth/mcp-cart-token")]
    public async Task Ordinary_grants_cannot_prepare_cancellation_or_refund(string grant)
    {
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, grant == "anonymous" ? null : await Token(api, grant));
        await using var client = await McpClient.CreateAsync(transport);
        foreach (var tool in new[] { "prepare_cancellation", "prepare_refund" })
        {
            Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name == tool);
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(tool, new Dictionary<string, object?> { ["orderId"] = Guid.NewGuid() }));
        }
    }

    [Theory]
    [InlineData("prepare_cancellation", "/api/auth/mcp-cancellation-token", true)]
    [InlineData("prepare_refund", "/api/auth/mcp-refund-token", false)]
    public async Task Ineligible_orders_and_unprotected_credential_requests_are_rejected(string tool, string grant, bool delivered)
    {
        using var anonymous = fixture.Client();
        using var noSession = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, grant);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        var (api, user) = await fixture.SignedInAsync(); using var shopper = api;
        using var noCsrf = await api.PostAsync(grant, null); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var id = await Order(user.Id, delivered); var before = await State();
        using var factory = Factory(); using var http = factory.CreateClient();
        await using var transport = Transport(http, await Token(api, grant));
        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["orderId"] = id });
        Assert.True(result.IsError);
        Assert.Contains("OrderPreparationFailed", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(before, await State());
    }
}
