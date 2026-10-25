using Commerce.Infrastructure;
using Commerce.Knowledge;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Api;

public static class KnowledgeEndpoints
{
    public static void MapKnowledge(this WebApplication app)
    {
        app.MapPost("/api/knowledge/search", async (KnowledgeQuery request, HttpContext context,
            UserManager<CommerceUser> users, IKnowledgeRetriever knowledge, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2000)
                return Results.Problem("Query must contain 1–2000 characters.", statusCode: 400);
            // Reuse the storefront's reviewed release, model identity and source-drift checks.
            return Results.Ok(await knowledge.SearchAsync(request.Query.Trim(), ct));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("knowledge");
    }

    public sealed record KnowledgeQuery(string? Query);
}
