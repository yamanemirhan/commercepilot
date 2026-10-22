using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Domain;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class AdminTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private async Task SetRole(string id, bool granted)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
        if (!await roles.RoleExistsAsync(IdentitySeed.AdministratorRole))
            Assert.True((await roles.CreateAsync(new(IdentitySeed.AdministratorRole))).Succeeded);
        var user = (await users.FindByIdAsync(id))!;
        var result = granted ? await users.AddToRoleAsync(user, IdentitySeed.AdministratorRole)
            : await users.RemoveFromRoleAsync(user, IdentitySeed.AdministratorRole);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Anonymous_shoppers_forged_headers_and_revoked_roles_cannot_read_admin_data()
    {
        using var anonymous = fixture.Client();
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        foreach (var path in new[] { "/api/admin/overview", $"/api/admin/turns/{Guid.NewGuid()}" })
        {
            var denied = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
            using var forged = new HttpRequestMessage(HttpMethod.Get, path);
            forged.Headers.Add("X-Role", "Administrator");
            forged.Headers.Add("X-User-Id", user.Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forged)).StatusCode);
        }
        await SetRole(user.Id, true);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/auth/session")).GetProperty("user").GetProperty("isAdmin").GetBoolean());
        var allowed = await client.GetAsync("/api/admin/overview");
        allowed.EnsureSuccessStatusCode();
        Assert.True(allowed.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/turns/{Guid.NewGuid()}")).StatusCode);
        await SetRole(user.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/overview")).StatusCode);
        await SetRole(user.Id, true);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
        var locked = (await users.FindByIdAsync(user.Id))!;
        await users.SetLockoutEnabledAsync(locked, true);
        await users.SetLockoutEndDateAsync(locked, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/overview")).StatusCode);
    }

    [Fact]
    public async Task Metrics_use_persisted_events_without_exposing_prompts_payloads_or_unknown_metadata()
    {
        var (client, user) = await fixture.SignedInAsync(); using var admin = client;
        await SetRole(user.Id, true);
        var conversationId = Guid.NewGuid(); var turnId = Guid.NewGuid(); var staleId = Guid.NewGuid(); var callId = Guid.NewGuid();
        var now = fixture.Factory.Clock.GetUtcNow();
        const string secret = "PRIVATE-PROMPT-RESULT-SECRET";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            db.Conversations.Add(new() { Id = conversationId, UserId = user.Id, CreatedAt = now, ActiveTurnId = staleId, ActiveUntil = now.AddSeconds(-1) });
            db.ChatTurns.AddRange(new ChatTurn { Id = turnId, ConversationId = conversationId, Input = secret, MessagesJson = JsonSerializer.Serialize(new[] { secret }), Status = "Completed", CreatedAt = now.AddSeconds(-10) },
                new ChatTurn { Id = staleId, ConversationId = conversationId, CreatedAt = now });
            void Add(string kind, int seconds, object data) => db.ChatEvents.Add(new() {
                ConversationId = conversationId, TurnId = turnId, Kind = kind,
                CreatedAt = now.AddSeconds(seconds), DataJson = JsonSerializer.Serialize(data) });
            Add("turn.started", -10, new { input = secret });
            Add("model.started", -9, new { });
            Add("model.completed", -8, new { durationMs = 1000, inputTokens = 100, outputTokens = 20, reasoning = secret });
            Add("tool.started", -7, new { name = "search_products", toolCallId = callId, arguments = secret });
            Add("tool.completed", -5, new { toolCallId = callId, isError = true, result = secret });
            Add("text.delta", -4, new { text = secret });
            Add("turn.completed", 0, new { text = secret });
            await db.SaveChangesAsync();
        }
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/admin/overview");
        Assert.DoesNotContain(secret, overview.GetRawText());
        var run = overview.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == turnId);
        Assert.Equal(100, run.GetProperty("inputTokens").GetInt32());
        Assert.Equal(20, run.GetProperty("outputTokens").GetInt32());
        Assert.Equal(10000, run.GetProperty("durationMs").GetDouble());
        Assert.Equal(1, run.GetProperty("toolErrors").GetInt32());
        var stale = overview.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == staleId);
        Assert.Equal("Interrupted", stale.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, stale.GetProperty("inputTokens").ValueKind);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/admin/turns/{turnId}");
        Assert.DoesNotContain(secret, detail.GetRawText());
        Assert.DoesNotContain("text.delta", detail.GetRawText());
        var tool = detail.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "tool.completed");
        Assert.Equal("search_products", tool.GetProperty("name").GetString());
        Assert.Equal(2000, tool.GetProperty("durationMs").GetDouble());
        await SetRole(user.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/turns/{turnId}")).StatusCode);
    }
}
