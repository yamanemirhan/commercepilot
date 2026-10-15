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

public sealed class McpCartTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
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

    private static HttpClientTransport Transport(HttpClient http, string? token = null)
    {
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, http);
    }

    private static T Result<T>(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string> TokenAsync(HttpClient api, bool write = true)
    {
        // Client-supplied identity/scope/price fields must never influence the issued grant.
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post,
            write ? "/api/auth/mcp-cart-token" : "/api/auth/mcp-token",
            new { userId = "someone-else", scope = "orders:confirm", price = 0 });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<McpAccessToken>())!;
        Assert.Equal(write ? "cart:read cart:write" : "cart:read", token.Scope);
        return token.AccessToken;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_and_read_only_clients_cannot_discover_or_call_add(bool signedIn)
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        {
            var token = signedIn ? await TokenAsync(api, write: false) : null;
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name == "add_to_cart");
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync("add_to_cart",
                new Dictionary<string, object?> { ["productId"] = "10000000-0000-0000-0000-000000000001", ["quantity"] = 1 }));
            var cart = (await api.GetFromJsonAsync<CartDto>("/api/cart"))!;
            Assert.Empty(cart.Items);
        }
    }

    [Fact]
    public async Task Write_credential_adds_only_to_its_owner_at_database_prices_without_order_or_reservation()
    {
        var (api, owner) = await fixture.SignedInAsync();
        var (otherApi, other) = await fixture.SignedInAsync();
        using (api) using (otherApi)
        {
            var token = await TokenAsync(api);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Stock >= 3);
            var ordersBefore = await db.Orders.CountAsync();
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var listed = await client.ListToolsAsync();
            Assert.Equal(6, listed.Count);
            var tool = Assert.Single(listed, t => t.Name == "add_to_cart");
            Assert.Equal(new[] { "productId", "quantity" }, tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
            var detail = Result<ProductDto>(await client.CallToolAsync("get_product", new Dictionary<string, object?> { ["productId"] = product.Id }));
            Assert.Equal(product.Price, detail.Price);
            for (var expected = 1; expected <= 2; expected++)
            {
                var cart = Result<CartDto>(await client.CallToolAsync("add_to_cart", new Dictionary<string, object?>
                {
                    ["productId"] = detail.Id, ["quantity"] = 1, ["userId"] = other.Id, ["unitPrice"] = 0, ["price"] = 0
                }));
                Assert.Equal(expected, cart.ItemCount);
                Assert.Equal(product.Price, Assert.Single(cart.Items).UnitPrice);
                Assert.Equal(product.Price * expected, cart.Total);
            }
            var saved = Result<CartDto>(await client.CallToolAsync("get_cart", new Dictionary<string, object?>()));
            Assert.Equal(2, saved.ItemCount);
            Assert.Equal(2, (await api.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            Assert.Empty((await otherApi.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
            Assert.Equal(product.Stock, await db.Products.Where(p => p.Id == product.Id).Select(p => p.Stock).SingleAsync());
            Assert.Equal(ordersBefore, await db.Orders.CountAsync());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_quantities_return_tool_errors_without_changing_cart(int quantity)
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var result = await client.CallToolAsync("add_to_cart", new Dictionary<string, object?>
            {
                ["productId"] = "10000000-0000-0000-0000-000000000001", ["quantity"] = quantity
            });
            Assert.True(result.IsError);
            Assert.Contains(result.Content.OfType<TextContentBlock>(), block => block.Text.Contains("InvalidQuantity"));
            Assert.Empty((await api.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
        }
    }

    [Fact]
    public async Task Missing_products_and_insufficient_stock_return_errors_without_mutating_the_bag()
    {
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            var token = await TokenAsync(api);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Stock > 0 && p.Stock < 99);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.True((await client.CallToolAsync("get_product", new Dictionary<string, object?> { ["productId"] = Guid.NewGuid() })).IsError);
            foreach (var (id, quantity, error) in new[]
            {
                (Guid.NewGuid(), 1, "ProductNotFound"), (product.Id, product.Stock + 1, "InsufficientStock")
            })
            {
                var result = await client.CallToolAsync("add_to_cart", new Dictionary<string, object?> { ["productId"] = id, ["quantity"] = quantity });
                Assert.True(result.IsError);
                Assert.Contains(result.Content.OfType<TextContentBlock>(), block => block.Text.Contains(error));
            }
            Assert.Empty((await api.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
        }
    }

    [Fact]
    public async Task Write_grant_requires_session_and_csrf_and_read_grant_cannot_be_upgraded_by_body()
    {
        using var anonymous = fixture.Client();
        using var denied = await CommerceFixture.MutateAsync(anonymous, HttpMethod.Post, "/api/auth/mcp-cart-token");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var (api, _) = await fixture.SignedInAsync();
        using (api)
        {
            using var noCsrf = await api.PostAsync("/api/auth/mcp-cart-token", null);
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            await TokenAsync(api, write: false);
            await TokenAsync(api, write: true);
        }
    }
}
