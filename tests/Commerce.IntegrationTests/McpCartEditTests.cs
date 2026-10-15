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

public sealed class McpCartEditTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
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
        return new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, http);
    }

    private static async Task<string> TokenAsync(HttpClient api, bool write = true)
    {
        using var response = await CommerceFixture.MutateAsync(api, HttpMethod.Post,
            write ? "/api/auth/mcp-cart-token" : "/api/auth/mcp-token");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<McpAccessToken>())!.AccessToken;
    }

    private static CartDto Cart(CallToolResult result)
    {
        Assert.False(result.IsError is true);
        Assert.NotNull(result.StructuredContent);
        return JsonSerializer.Deserialize<CartDto>(JsonSerializer.Serialize(result.StructuredContent), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Theory]
    [InlineData("update_cart_item", false)]
    [InlineData("update_cart_item", true)]
    [InlineData("remove_from_cart", false)]
    [InlineData("remove_from_cart", true)]
    public async Task Anonymous_and_read_credentials_cannot_discover_or_invoke_edits(string tool, bool signedIn)
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var carts = scope.ServiceProvider.GetRequiredService<ICartService>();
            var product = await scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Products.FirstAsync(p => p.Stock >= 2);
            Assert.Null((await carts.AddAsync(user.Id, product.Id, 2, default)).Error);
            var token = signedIn ? await TokenAsync(api, write: false) : null;
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            Assert.DoesNotContain(await client.ListToolsAsync(), t => t.Name == tool);
            var arguments = new Dictionary<string, object?> { ["productId"] = product.Id };
            if (tool == "update_cart_item") arguments["quantity"] = 1;
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync(tool, arguments));
            Assert.Equal(2, (await api.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        }
    }

    [Fact]
    public async Task Repeated_set_and_remove_only_affect_the_authenticated_bag_and_never_inventory_or_orders()
    {
        var (api, owner) = await fixture.SignedInAsync();
        var (otherApi, other) = await fixture.SignedInAsync();
        using (api) using (otherApi)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var carts = scope.ServiceProvider.GetRequiredService<ICartService>();
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Stock >= 3);
            Assert.Null((await carts.AddAsync(owner.Id, product.Id, 2, default)).Error);
            Assert.Null((await carts.AddAsync(other.Id, product.Id, 3, default)).Error);
            var ordersBefore = await db.Orders.CountAsync();
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            var update = Assert.Single(tools, t => t.Name == "update_cart_item");
            var remove = Assert.Single(tools, t => t.Name == "remove_from_cart");
            Assert.Equal(new[] { "productId", "quantity" }, update.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal("productId", Assert.Single(remove.JsonSchema.GetProperty("properties").EnumerateObject()).Name);
            var arguments = new Dictionary<string, object?> { ["productId"] = product.Id, ["quantity"] = 1, ["userId"] = other.Id, ["price"] = 0 };
            for (var i = 0; i < 2; i++)
            {
                var cart = Cart(await client.CallToolAsync("update_cart_item", arguments));
                Assert.Equal(1, cart.ItemCount);
                Assert.Equal(product.Price, cart.Total);
                Assert.Equal(1, (await api.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            }
            arguments.Remove("quantity");
            for (var i = 0; i < 2; i++) Assert.Empty(Cart(await client.CallToolAsync("remove_from_cart", arguments)).Items);
            Assert.Empty((await api.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
            Assert.Equal(3, (await otherApi.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            Assert.Equal(product.Stock, await db.Products.Where(p => p.Id == product.Id).Select(p => p.Stock).SingleAsync());
            Assert.Equal(ordersBefore, await db.Orders.CountAsync());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_total_quantities_leave_the_existing_item_unchanged(int quantity)
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var product = await scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Products.FirstAsync(p => p.Stock >= 2);
            Assert.Null((await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(user.Id, product.Id, 2, default)).Error);
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var result = await client.CallToolAsync("update_cart_item", new Dictionary<string, object?> { ["productId"] = product.Id, ["quantity"] = quantity });
            Assert.True(result.IsError);
            Assert.Contains(result.Content.OfType<TextContentBlock>(), block => block.Text.Contains("InvalidQuantity"));
            Assert.Equal(2, (await api.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        }
    }

    [Fact]
    public async Task Update_requires_existing_item_and_current_stock_but_unavailable_items_can_be_removed()
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.Stock >= 2 && p.Stock < 99);
            var token = await TokenAsync(api);
            using var factory = Factory(); using var http = factory.CreateClient();
            await using var transport = Transport(http, token);
            await using var client = await McpClient.CreateAsync(transport);
            var arguments = new Dictionary<string, object?> { ["productId"] = product.Id, ["quantity"] = 1 };
            var missing = await client.CallToolAsync("update_cart_item", arguments);
            Assert.True(missing.IsError);
            Assert.Contains(missing.Content.OfType<TextContentBlock>(), b => b.Text.Contains("ProductNotFound"));
            Assert.Null((await scope.ServiceProvider.GetRequiredService<ICartService>().AddAsync(user.Id, product.Id, 2, default)).Error);
            await db.Products.Where(p => p.Id == product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 0));
            var unavailable = await client.CallToolAsync("update_cart_item", arguments);
            Assert.True(unavailable.IsError);
            Assert.Contains(unavailable.Content.OfType<TextContentBlock>(), b => b.Text.Contains("InsufficientStock"));
            Assert.Equal(2, (await api.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            arguments.Remove("quantity");
            Assert.Empty(Cart(await client.CallToolAsync("remove_from_cart", arguments)).Items);
            Assert.Equal(0, await db.Products.Where(p => p.Id == product.Id).Select(p => p.Stock).SingleAsync());
        }
    }
}
