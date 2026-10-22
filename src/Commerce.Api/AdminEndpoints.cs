using System.Text.Json;
using Commerce.Domain;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Api;

public sealed class AdministratorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<CommerceUser>>();
        var user = await users.GetUserAsync(context.HttpContext.User);
        // Re-read role and lockout on every poll: stale cookies cannot retain revoked access.
        if (user is null || await users.IsLockedOutAsync(user) || !await users.IsInRoleAsync(user, IdentitySeed.AdministratorRole))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(context);
    }
}

public sealed record AdminEvent(long Sequence, DateTimeOffset At, string Kind, string? Name,
    Guid? ToolCallId, bool? IsError, double? DurationMs, long? InputTokens, long? OutputTokens, string? ErrorType);
public sealed record AdminRun(Guid Id, string UserId, string DisplayName, string Status, DateTimeOffset CreatedAt,
    double? DurationMs, int ToolCalls, int ToolErrors, int ModelCalls, long? InputTokens, long? OutputTokens);

public static class AdminEndpoints
{
    private static readonly string[] EventKinds = ["turn.started", "model.started", "model.completed", "tool.started", "tool.completed", "turn.completed", "turn.failed"];

    public static void MapAdmin(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization().AddEndpointFilter<AdministratorFilter>();
        group.MapGet("/overview", OverviewAsync);
        group.MapGet("/turns/{id:guid}", async (Guid id, CommerceDbContext db, CancellationToken ct) =>
        {
            if (!await db.ChatTurns.AnyAsync(t => t.Id == id, ct)) return Results.NotFound();
            var events = await db.ChatEvents.AsNoTracking().Where(e => e.TurnId == id && EventKinds.Contains(e.Kind))
                .OrderBy(e => e.Sequence).Take(201).ToListAsync(ct);
            return Results.Ok(new { events = Redact(events.Take(200)), truncated = events.Count > 200 });
        });
    }

    private static async Task<IResult> OverviewAsync(CommerceDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);
        var turns = await (from turn in db.ChatTurns.AsNoTracking()
                           join conversation in db.Conversations on turn.ConversationId equals conversation.Id
                           join user in db.Users on conversation.UserId equals user.Id
                           where turn.CreatedAt >= since
                           orderby turn.CreatedAt descending
                           select new { turn.Id, conversation.UserId, user.DisplayName, turn.Status, turn.CreatedAt,
                               conversation.ActiveTurnId, conversation.ActiveUntil }).Take(201).ToListAsync(ct);
        var selected = turns.Take(200).ToArray();
        var ids = selected.Select(t => t.Id).ToArray();
        var rawEvents = await db.ChatEvents.AsNoTracking().Where(e => ids.Contains(e.TurnId) && EventKinds.Contains(e.Kind))
            .OrderByDescending(e => e.Sequence).Take(5001).ToListAsync(ct);
        var byTurn = rawEvents.Take(5000).GroupBy(e => e.TurnId).ToDictionary(g => g.Key, g => Redact(g.OrderBy(e => e.Sequence)).ToArray());
        var runs = selected.Select(turn =>
        {
            var events = byTurn.GetValueOrDefault(turn.Id) ?? [];
            var status = turn.Status == "Running" && (turn.ActiveTurnId != turn.Id || turn.ActiveUntil <= now || turn.ActiveUntil is null)
                ? "Interrupted" : turn.Status;
            var end = events.LastOrDefault(e => e.Kind is "turn.completed" or "turn.failed");
            var modelEvents = events.Where(e => e.Kind == "model.completed").ToArray();
            double? duration = status == "Running" ? (now - turn.CreatedAt).TotalMilliseconds
                : end is null ? null : (end.At - turn.CreatedAt).TotalMilliseconds;
            return new AdminRun(turn.Id, turn.UserId, turn.DisplayName, status, turn.CreatedAt,
                duration is null ? null : Math.Max(0, duration.Value), events.Count(e => e.Kind == "tool.started"),
                events.Count(e => e.Kind == "tool.completed" && e.IsError == true), events.Count(e => e.Kind == "model.started"),
                modelEvents.Length == 0 ? null : modelEvents.Sum(e => e.InputTokens ?? 0),
                modelEvents.Length == 0 ? null : modelEvents.Sum(e => e.OutputTokens ?? 0));
        }).ToArray();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(100)
            .Select(u => new { u.Id, u.DisplayName, u.Email, locked = u.LockoutEnabled && u.LockoutEnd > now,
                conversations = db.Conversations.Count(c => c.UserId == u.Id) }).ToArrayAsync(ct);
        var eventsAll = byTurn.Values.SelectMany(v => v).ToArray();
        var toolStats = eventsAll.Where(e => e.Kind == "tool.started" && e.Name is not null).GroupBy(e => e.Name)
            .Select(g => new { name = g.Key, calls = g.Count(),
                errors = eventsAll.Count(e => e.Kind == "tool.completed" && e.Name == g.Key && e.IsError == true) })
            .OrderByDescending(t => t.calls).ToArray();
        return Results.Ok(new { generatedAt = now, windowStart = since, limit = 200,
            truncated = turns.Count > 200 || rawEvents.Count > 5000, usersTotal = await db.Users.CountAsync(ct), users, runs,
            tools = toolStats, pendingReviews = await db.PendingActions.CountAsync(a => a.Status == "Pending" && a.ExpiresAt > now, ct) });
    }

    // Explicit metadata allowlist. Never return prompts, message text, arguments, results, cookies or reasoning.
    private static IEnumerable<AdminEvent> Redact(IEnumerable<ChatEvent> events)
    {
        var started = new Dictionary<Guid, (DateTimeOffset At, string? Name)>();
        foreach (var entry in events)
        {
            using var json = JsonDocument.Parse(entry.DataJson);
            var data = json.RootElement;
            var name = data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && ChatPolicy.ToolNames.Contains(n.GetString()!) ? n.GetString() : null;
            Guid? callId = data.TryGetProperty("toolCallId", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetGuid(out var id) ? id : null;
            double? duration = Number(data, "durationMs");
            if (entry.Kind == "tool.started" && callId is { } startedId) started[startedId] = (entry.CreatedAt, name);
            if (entry.Kind == "tool.completed" && callId is { } completedId && started.TryGetValue(completedId, out var start))
            {
                name ??= start.Name;
                duration = Math.Max(0, (entry.CreatedAt - start.At).TotalMilliseconds);
            }
            var errorType = data.TryGetProperty("errorType", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() is "ModelProtocolException" or "HttpRequestException" or "OperationCanceledException" or "TaskCanceledException"
                    ? error.GetString() : "UnexpectedFailure" : null;
            yield return new(entry.Sequence, entry.CreatedAt, entry.Kind, name, callId,
                data.TryGetProperty("isError", out var isError) && isError.ValueKind is JsonValueKind.True or JsonValueKind.False ? isError.GetBoolean() : null,
                duration, (long?)Number(data, "inputTokens"), (long?)Number(data, "outputTokens"), errorType);
        }
    }

    private static double? Number(JsonElement data, string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 && number <= 1_000_000_000 ? number : null;
}
