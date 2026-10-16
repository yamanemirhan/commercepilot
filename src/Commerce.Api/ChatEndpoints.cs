using System.Security.Claims;
using System.Text.Json;
using Commerce.AI;
using Commerce.Application;
using Commerce.Infrastructure;
using Commerce.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Api;

public static class ChatEndpoints
{
    public static void AddChat(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("knowledge", http =>
        {
            http.BaseAddress = new(builder.Configuration["Chat:OllamaUrl"] ?? "http://127.0.0.1:11434/");
            http.Timeout = TimeSpan.FromSeconds(20);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddScoped<IKnowledgeEmbeddings>(services => new KnowledgeEmbeddings(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("knowledge")));
        builder.Services.AddScoped<IKnowledgeRetriever, SiteKnowledge>();
        builder.Services.AddHttpClient("chat-mcp", http => http.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient("chat-model", http =>
        {
            http.BaseAddress = new(builder.Configuration["Chat:OllamaUrl"] ?? "http://127.0.0.1:11434/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IChatModel>(services => new OllamaChatModel(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("chat-model"), new()
            { Model = builder.Configuration["Chat:Model"] ?? "qwen3.8:27b" }));
        builder.Services.AddScoped<IChatToolFactory, McpChatToolFactory>();
        builder.Services.AddScoped<ChatService>();
    }
    public static void MapChat(this WebApplication app)
    {
        var group = app.MapGroup("/api/chat").RequireAuthorization().AddEndpointFilter<OrderFaultFilter>();
        group.MapGet("/conversations", async (ClaimsPrincipal user, CommerceDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Conversations.AsNoTracking().Where(c => c.UserId == Owner(user))
                .OrderByDescending(c => c.CreatedAt).Select(c => new { c.Id, c.Title, c.CreatedAt }).Take(50).ToListAsync(ct)));
        group.MapPost("/conversations", async (ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.CreateAsync(Owner(user), ct))).AddEndpointFilter<CsrfFilter>().RequireRateLimiting("chat");
        group.MapGet("/conversations/{id:guid}", async (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.ReadAsync(Owner(user), id, ct)));
        group.MapDelete("/conversations/{id:guid}", async (Guid id, ClaimsPrincipal user, ChatService chat, CommerceDbContext db, CancellationToken ct) =>
        {
            var conversation = await chat.OwnedAsync(Owner(user), id, ct);
            if (conversation.ActiveTurnId is not null) throw new OrderFault(409, "Wait for the active reply before deleting this conversation.");
            var deleted = await db.Conversations.Where(c => c.Id == id && c.UserId == Owner(user) && c.ActiveTurnId == null).ExecuteDeleteAsync(ct);
            if (deleted != 1) throw new OrderFault(409, "A reply started. Wait before deleting this conversation.");
            return Results.NoContent();
        }).AddEndpointFilter<CsrfFilter>();
        group.MapPost("/conversations/{id:guid}/messages", async (Guid id, ChatInput input, HttpContext context, ChatService chat) =>
        {
            var owner = Owner(context.User);
            var turn = await chat.BeginAsync(owner, id, input, context.RequestAborted);
            if (turn is null) return Results.Ok(await chat.ReadAsync(owner, id, context.RequestAborted));
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(context.RequestAborted);
            await chat.RunAsync(owner, turn, async e =>
            {
                await context.Response.WriteAsync($"id: {e.Sequence}\nevent: {e.Kind}\ndata: {JsonSerializer.Serialize(e, OllamaChatModel.Json)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }, context.RequestAborted);
            return Results.Empty;
        }).AddEndpointFilter<CsrfFilter>().RequireRateLimiting("chat");
        group.MapPost("/conversations/{id:guid}/reviews", async (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.ImportReviewsAsync(Owner(user), id, ct))).AddEndpointFilter<CsrfFilter>().RequireRateLimiting("chat");
        group.MapPost("/conversations/{id:guid}/cards/{cardId:guid}/decision", async (Guid id, Guid cardId, ChatDecision decision,
            ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.DecideAsync(Owner(user), id, cardId, decision, ct))).AddEndpointFilter<CsrfFilter>();
    }
    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
