using System.Net;
using System.Net.Http.Json;
using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Commerce.IntegrationTests.CommerceFixture;

namespace Commerce.IntegrationTests;

public sealed class ShoppingTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private static readonly Guid Forma = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Pocket = Guid.Parse("10000000-0000-0000-0000-000000000006");

    [Fact]
    public async Task Anonymous_cart_access_and_login_without_csrf_are_rejected()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cart")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/cart/items", new { productId = Forma, quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", new { email = "x@example.test", password = "Unknown!12345" })).StatusCode);
    }

    [Fact]
    public async Task Session_cookies_are_secure_and_logout_removes_access()
    {
        var (client, user) = await fixture.SignedInAsync();
        using (client)
        {
            var session = await client.GetFromJsonAsync<Session>("/api/auth/session");
            Assert.Equal(user.Id, session!.User!.Id);
            using var csrf = await client.GetAsync("/api/auth/csrf");
            Assert.True(csrf.Headers.CacheControl?.NoStore);
            using var fresh = fixture.Client();
            using var tokenResponse = await fresh.GetAsync("/api/auth/csrf");
            var cookie = string.Join(";", tokenResponse.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
            Assert.Contains("httponly", cookie);
            Assert.Contains("secure", cookie);
            Assert.Contains("samesite=strict", cookie);
            using var logout = await MutateAsync(client, HttpMethod.Post, "/api/auth/logout");
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.Null((await client.GetFromJsonAsync<Session>("/api/auth/session"))!.User);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cart")).StatusCode);
        }
    }

    [Fact]
    public async Task Other_accounts_cannot_read_update_or_delete_a_shoppers_cart()
    {
        var (alex, alexUser) = await fixture.SignedInAsync();
        var (sam, _) = await fixture.SignedInAsync();
        using (alex) using (sam)
        {
            (await MutateAsync(alex, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity = 2 })).EnsureSuccessStatusCode();
            var other = await sam.GetFromJsonAsync<CartDto>($"/api/cart?userId={alexUser.Id}");
            Assert.Empty(other!.Items);
            Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(sam, HttpMethod.Put, $"/api/cart/items/{Forma}", new { quantity = 3, userId = alexUser.Id })).StatusCode);
            (await MutateAsync(sam, HttpMethod.Delete, $"/api/cart/items/{Forma}")).EnsureSuccessStatusCode();
            Assert.Equal(2, (await alex.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            (await MutateAsync(sam, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity = 1, userId = alexUser.Id })).EnsureSuccessStatusCode();
            Assert.Equal(1, (await sam.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
            Assert.Equal(2, (await alex.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_quantities_do_not_mutate_the_cart(int quantity)
    {
        var (client, _) = await fixture.SignedInAsync();
        using (client)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity })).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
        }
    }

    [Fact]
    public async Task Out_of_stock_and_over_stock_requests_are_rejected_without_reserving_inventory()
    {
        var (client, _) = await fixture.SignedInAsync();
        using (client)
        {
            var unavailable = Guid.Parse("10000000-0000-0000-0000-000000000031");
            Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = unavailable, quantity = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity = 9 })).StatusCode);
            (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity = 8 })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = Forma, quantity = 1 })).StatusCode);
            var cart = await client.GetFromJsonAsync<CartDto>("/api/cart");
            Assert.Equal(8, cart!.ItemCount);
            Assert.Equal(8, cart.Items[0].AvailableStock);
        }
    }

    [Fact]
    public async Task Concurrent_additions_are_not_lost()
    {
        var (client, _) = await fixture.SignedInAsync();
        using (client)
        {
            var csrf = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf");
            var requests = Enumerable.Range(0, 6).Select(async _ =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/api/cart/items") { Content = JsonContent.Create(new { productId = Forma, quantity = 1 }) };
                message.Headers.Add("X-CSRF-Token", csrf!.Token);
                using var response = await client.SendAsync(message);
                response.EnsureSuccessStatusCode();
            });
            await Task.WhenAll(requests);
            Assert.Equal(6, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        }
    }

    [Fact]
    public async Task Prices_are_server_owned_and_quantity_updates_and_removal_persist()
    {
        var (client, user) = await fixture.SignedInAsync();
        using (client)
        {
            (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = Pocket, quantity = 2, unitPrice = 0.01, total = 0.02 })).EnsureSuccessStatusCode();
            var cart = await client.GetFromJsonAsync<CartDto>("/api/cart");
            Assert.Equal(118m, cart!.Total);
            (await MutateAsync(client, HttpMethod.Put, $"/api/cart/items/{Pocket}", new { quantity = 3 })).EnsureSuccessStatusCode();
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            Assert.Equal(3, (await db.CartItems.AsNoTracking().SingleAsync(i => i.UserId == user.Id)).Quantity);
            Assert.Equal(177m, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.Total);
            (await MutateAsync(client, HttpMethod.Delete, $"/api/cart/items/{Pocket}")).EnsureSuccessStatusCode();
            (await MutateAsync(client, HttpMethod.Delete, $"/api/cart/items/{Pocket}")).EnsureSuccessStatusCode();
            Assert.Empty((await client.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
        }
    }

    [Fact]
    public async Task Csrf_tokens_from_another_session_cannot_authorize_cart_mutations()
    {
        var (client, _) = await fixture.SignedInAsync();
        using (client) using (var stranger = fixture.Client())
        {
            var token = await stranger.GetFromJsonAsync<Csrf>("/api/auth/csrf");
            using var missing = await client.PostAsJsonAsync("/api/cart/items", new { productId = Forma, quantity = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/cart/items") { Content = JsonContent.Create(new { productId = Forma, quantity = 1 }) };
            message.Headers.Add("X-CSRF-Token", token!.Token);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(message)).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<CartDto>("/api/cart"))!.Items);
        }
    }

    private sealed record Session(SessionUser? User);
    private sealed record SessionUser(string Id, string Email, string DisplayName);
}
