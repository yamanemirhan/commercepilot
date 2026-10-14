using System.Net;
using System.Net.Http.Json;
using Commerce.Application;
using Commerce.Domain;
using Commerce.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Commerce.IntegrationTests.CommerceFixture;

namespace Commerce.IntegrationTests;

public sealed class OrderTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private async Task<Product> Product(int stock = 5)
    {
        var product = new Product { Id = Guid.NewGuid(), Slug = Guid.NewGuid().ToString("N"), Name = "Test headphones",
            Description = "Integration test product", Category = "Audio", Price = 49.95m, Currency = "USD",
            Stock = stock, Artwork = "headphones", Color = "sage" };
        await Db(async db => { db.Products.Add(product); await db.SaveChangesAsync(); });
        return product;
    }
    private async Task Db(Func<CommerceDbContext, Task> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<CommerceDbContext>());
    }
    private async Task Add(HttpClient client, Product product, int quantity = 1) =>
        (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = product.Id, quantity })).EnsureSuccessStatusCode();
    private static async Task<PendingActionDto> Prepare(HttpClient client, string mode = "success") =>
        await Body<PendingActionDto>(await MutateAsync(client, HttpMethod.Post, "/api/orders/prepare", new { paymentMode = mode, total = 0.01, userId = "spoof" }));
    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, PendingActionDto action) =>
        MutateAsync(client, HttpMethod.Post, $"/api/actions/{action.Id}/confirm", new { action.SnapshotHash });
    private static async Task<PendingActionDto> Change(HttpClient client, Guid order, string kind) =>
        await Body<PendingActionDto>(await MutateAsync(client, HttpMethod.Post, $"/api/orders/{order}/prepare-{kind}", new { }));
    private static async Task<OrderDto> Get(HttpClient client, Guid id) => (await client.GetFromJsonAsync<OrderDto>($"/api/orders/{id}"))!;
    private async Task Advance(string userId, Guid id, TimeSpan duration)
    {
        fixture.Factory.Clock.Advance(duration);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOrderService>().AdvanceAsync(userId, id, default);
    }

    [Fact]
    public async Task Preparing_and_rejecting_never_creates_order_or_reserves_inventory()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var action = await Prepare(client);
        Assert.Equal(49.95m, action.Snapshot.Total);
        Assert.Equal(64, action.SnapshotHash.Length);
        Assert.Equal(TimeSpan.FromMinutes(5), action.ExpiresAt - action.CreatedAt);
        Assert.Empty((await client.GetFromJsonAsync<OrderDto[]>("/api/orders"))!);
        Assert.Contains((await client.GetFromJsonAsync<PendingActionDto[]>("/api/actions"))!, a => a.Id == action.Id);
        await Db(async db => Assert.Equal(5, (await db.Products.FindAsync(product.Id))!.Stock));
        var rejected = await Body<PendingActionDto>(await MutateAsync(client, HttpMethod.Post, $"/api/actions/{action.Id}/reject", new { }));
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, action)).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
    }

    [Fact]
    public async Task Concurrent_duplicate_confirmations_return_one_order_and_capture_once()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product, 2);
        var action = await Prepare(client);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Confirm(client, action)));
        var ids = new List<Guid?>();
        foreach (var response in results) ids.Add((await Body<PendingActionDto>(response)).OrderId);
        var id = Assert.Single(ids.Distinct())!.Value;
        var order = await Get(client, id);
        Assert.Equal("Processing", order.Status); Assert.Equal("Captured", order.PaymentStatus);
        Assert.Equal(99.90m, order.CapturedAmount); Assert.Equal(2, order.Timeline.Count);
        Assert.Equal(0, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        await Db(async db => { Assert.Equal(3, (await db.Products.FindAsync(product.Id))!.Stock); Assert.Equal(1, await db.Orders.CountAsync(o => o.UserId == user.Id)); });
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(id, (await Body<PendingActionDto>(await Confirm(client, action))).OrderId);
    }

    [Theory]
    [InlineData("price")]
    [InlineData("stock")]
    [InlineData("cart")]
    public async Task Changed_checkout_requires_fresh_approval(string change)
    {
        var (client, _) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var action = await Prepare(client);
        if (change == "cart") await Add(client, product);
        else await Db(async db => { var p = (await db.Products.FindAsync(product.Id))!; if (change == "price") p.Price++; else p.Stock--; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, action)).StatusCode);
        Assert.Equal("Stale", (await client.GetFromJsonAsync<PendingActionDto>($"/api/actions/{action.Id}"))!.Status);
        Assert.Empty((await client.GetFromJsonAsync<OrderDto[]>("/api/orders"))!);
        var fresh = await Prepare(client);
        (await Confirm(client, fresh)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Expired_superseded_and_tampered_reviews_cannot_execute()
    {
        var (client, _) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var old = await Prepare(client); var action = await Prepare(client);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, old)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, $"/api/actions/{action.Id}/confirm", new { snapshotHash = "tampered" })).StatusCode);
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Expired", (await client.GetFromJsonAsync<PendingActionDto>($"/api/actions/{action.Id}"))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, action)).StatusCode);
        await Db(async db => Assert.Equal(5, (await db.Products.FindAsync(product.Id))!.Stock));
    }

    [Fact]
    public async Task Last_item_can_be_reserved_by_only_one_shopper()
    {
        var (a, _) = await fixture.SignedInAsync(); using var first = a;
        var (b, _) = await fixture.SignedInAsync(); using var second = b;
        var product = await Product(1); await Add(a, product); await Add(b, product);
        var aa = await Prepare(a); var bb = await Prepare(b);
        var responses = await Task.WhenAll(Confirm(a, aa), Confirm(b, bb));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        await Db(async db => Assert.Equal(0, (await db.Products.FindAsync(product.Id))!.Stock));
    }

    [Fact]
    public async Task Failed_payment_preserves_cart_and_releases_stock_exactly_once()
    {
        var (client, _) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product, 2);
        var action = await Prepare(client, "failure");
        var completed = await Body<PendingActionDto>(await Confirm(client, action));
        (await Confirm(client, action)).EnsureSuccessStatusCode();
        var order = await Get(client, completed.OrderId!.Value);
        Assert.Equal("PaymentFailed", order.Status); Assert.Equal("Failed", order.PaymentStatus);
        Assert.Equal(0, order.CapturedAmount); Assert.False(order.CanCancel); Assert.False(order.CanRefund);
        Assert.Equal(2, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
        await Db(async db => Assert.Equal(5, (await db.Products.FindAsync(product.Id))!.Stock));
    }

    [Theory]
    [InlineData("success", "Refunded")]
    [InlineData("delay", "Voided")]
    public async Task Approved_cancellation_is_idempotent_and_prevents_late_capture(string mode, string payment)
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var checkout = await Prepare(client, mode);
        var id = (await Body<PendingActionDto>(await Confirm(client, checkout))).OrderId!.Value;
        var action = await Change(client, id, "cancellation");
        Assert.NotEqual("Cancelled", (await Get(client, id)).Status);
        (await Confirm(client, action)).EnsureSuccessStatusCode();
        (await Confirm(client, action)).EnsureSuccessStatusCode();
        await Advance(user.Id, id, TimeSpan.FromMinutes(10));
        var order = await Get(client, id);
        Assert.Equal("Cancelled", order.Status); Assert.Equal(payment, order.PaymentStatus);
        Assert.Equal(order.CapturedAmount, order.RefundedAmount);
        Assert.Null(order.NextTransitionAt);
        await Db(async db => Assert.Equal(5, (await db.Products.FindAsync(product.Id))!.Stock));
    }

    [Fact]
    public async Task Delayed_payment_shipping_and_refund_have_separate_durable_states()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var id = (await Body<PendingActionDto>(await Confirm(client, await Prepare(client, "delay")))).OrderId!.Value;
        var order = await Get(client, id); Assert.Equal("AwaitingPayment", order.Status); Assert.Equal("Pending", order.PaymentStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, $"/api/orders/{id}/prepare-refund", new { })).StatusCode);
        var cancellation = await Change(client, id, "cancellation");
        await Advance(user.Id, id, TimeSpan.FromSeconds(20));
        Assert.Equal("Captured", (await Get(client, id)).PaymentStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, cancellation)).StatusCode);
        await Advance(user.Id, id, TimeSpan.FromMinutes(2));
        Assert.Equal("Shipped", (await Get(client, id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, $"/api/orders/{id}/prepare-cancellation", new { })).StatusCode);
        await Advance(user.Id, id, TimeSpan.FromMinutes(2));
        order = await Get(client, id); Assert.Equal("Delivered", order.Status); Assert.True(order.CanRefund);
        var refund = await Change(client, id, "refund");
        (await Confirm(client, refund)).EnsureSuccessStatusCode();
        (await Confirm(client, refund)).EnsureSuccessStatusCode();
        order = await Get(client, id);
        Assert.Equal("Refunded", order.Status); Assert.Equal(49.95m, order.RefundedAmount);
        Assert.Equal(6, order.Timeline.Count); Assert.False(order.CanRefund);
        await Db(async db => Assert.Equal(4, (await db.Products.FindAsync(product.Id))!.Stock));
    }

    [Fact]
    public async Task Refund_window_closes_after_thirty_days()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var id = (await Body<PendingActionDto>(await Confirm(client, await Prepare(client)))).OrderId!.Value;
        await Advance(user.Id, id, TimeSpan.FromMinutes(2));
        await Advance(user.Id, id, TimeSpan.FromMinutes(2));
        fixture.Factory.Clock.Advance(TimeSpan.FromDays(31));
        // The shopper's eight-hour session also expires after this jump. Check the domain policy directly.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        Assert.False((await service.GetAsync(user.Id, id, default)).CanRefund);
        var fault = await Assert.ThrowsAsync<OrderFault>(() => service.PrepareChangeAsync(user.Id, id, "Refund", default));
        Assert.Equal(409, fault.Status);
    }

    [Fact]
    public async Task Database_prevents_rewriting_approved_terms()
    {
        var (client, _) = await fixture.SignedInAsync(); using var shopper = client;
        var product = await Product(); await Add(client, product);
        var action = await Prepare(client);
        await Db(async db =>
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"PendingActions\" SET \"SnapshotJson\" = {"rewritten"} WHERE \"Id\" = {action.Id}"));
            Assert.Equal("23514", error.SqlState);
        });
        Assert.Equal(action.SnapshotHash, (await client.GetFromJsonAsync<PendingActionDto>($"/api/actions/{action.Id}"))!.SnapshotHash);
    }

    [Fact]
    public async Task All_order_and_approval_routes_enforce_owner_and_csrf()
    {
        var (a, _) = await fixture.SignedInAsync(); using var owner = a;
        var (b, _) = await fixture.SignedInAsync(); using var other = b;
        using var anonymous = fixture.Client();
        var product = await Product(); await Add(a, product); var action = await Prepare(a);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/actions/{action.Id}/confirm", new { action.SnapshotHash })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/actions/{action.Id}/reject", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/actions/{action.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(b, action)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(b, HttpMethod.Post, $"/api/actions/{action.Id}/reject", new { })).StatusCode);
        var id = (await Body<PendingActionDto>(await Confirm(a, action))).OrderId!.Value;
        Assert.Empty((await b.GetFromJsonAsync<OrderDto[]>("/api/orders"))!);
        Assert.Empty((await b.GetFromJsonAsync<PendingActionDto[]>("/api/actions"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/orders/{id}")).StatusCode);
        foreach (var kind in new[] { "cancellation", "refund" })
            Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(b, HttpMethod.Post, $"/api/orders/{id}/prepare-{kind}", new { })).StatusCode);
    }
}
