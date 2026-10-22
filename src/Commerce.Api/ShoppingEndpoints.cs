using System.Security.Claims;
using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Api;

public static class ShoppingEndpoints
{
    public static void MapShoppingEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Identity");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        auth.MapGet("/session", async (HttpContext context, UserManager<CommerceUser> users) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await users.GetUserAsync(context.User);
            return Results.Ok(new { user = user is null ? null : new { user.Id, user.Email, user.DisplayName,
                isAdmin = !await users.IsLockedOutAsync(user) && await users.IsInRoleAsync(user, IdentitySeed.AdministratorRole) } });
        });
        auth.MapPost("/login", async (LoginRequest request, SignInManager<CommerceUser> signIn, UserManager<CommerceUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
                string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
                return Results.Problem("Enter a valid email and password.", statusCode: 400);
            var user = await users.FindByEmailAsync(request.Email.Trim());
            var result = user is null ? SignInResult.Failed :
                await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.Ok(new { user = new { user!.Id, user.Email, user.DisplayName,
                isAdmin = await users.IsInRoleAsync(user, IdentitySeed.AdministratorRole) } }) :
                Results.Problem("Sign-in failed. Check your details or try again later.", statusCode: 401);
        }).AddEndpointFilter<CsrfFilter>().RequireRateLimiting("login");
        auth.MapPost("/logout", async (SignInManager<CommerceUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>();

        // Only the signed-in user's identity and the fixed read-only scope can be issued.
        auth.MapPost("/mcp-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        // A separate, explicit grant for cart edits. Read credentials never gain write access.
        auth.MapPost("/mcp-cart-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.CartWrite));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        auth.MapPost("/mcp-orders-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.OrdersRead));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        // Preparation grants no permission to confirm or reject a review.
        auth.MapPost("/mcp-checkout-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.CheckoutPrepare));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        auth.MapPost("/mcp-actions-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.ActionsRead));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        auth.MapPost("/mcp-cancellation-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.CancellationPrepare));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");
        auth.MapPost("/mcp-refund-token", async (HttpContext context, UserManager<CommerceUser> users, McpAccessTokens tokens) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            return Results.Ok(tokens.Issue(user, McpAccessGrant.RefundPrepare));
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().RequireRateLimiting("mcp-token");

        var cart = app.MapGroup("/api/cart").WithTags("Cart").RequireAuthorization();
        cart.MapGet("/", async (ClaimsPrincipal principal, ICartService carts, CancellationToken ct) =>
            Results.Ok(await carts.GetAsync(Owner(principal), ct)));
        cart.MapPost("/items", async (AddCartItemRequest request, ClaimsPrincipal principal, ICartService carts, CancellationToken ct) =>
            ToResult(await carts.AddAsync(Owner(principal), request.ProductId, request.Quantity, ct)))
            .AddEndpointFilter<CsrfFilter>();
        cart.MapPut("/items/{productId:guid}", async (Guid productId, QuantityRequest request, ClaimsPrincipal principal, ICartService carts, CancellationToken ct) =>
            ToResult(await carts.SetQuantityAsync(Owner(principal), productId, request.Quantity, ct)))
            .AddEndpointFilter<CsrfFilter>();
        cart.MapDelete("/items/{productId:guid}", async (Guid productId, ClaimsPrincipal principal, ICartService carts, CancellationToken ct) =>
            Results.Ok(await carts.RemoveAsync(Owner(principal), productId, ct)))
            .AddEndpointFilter<CsrfFilter>();
    }

    private static string Owner(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated user ID is missing.");

    private static IResult ToResult(CartResult result) => result.Error is null ? Results.Ok(result.Cart) :
        Results.Problem(result.Message, statusCode: result.Error switch
        {
            CartError.ProductNotFound => 404,
            CartError.InsufficientStock => 409,
            _ => 400
        }, extensions: new Dictionary<string, object?> { ["code"] = result.Error.ToString() });
}

public sealed record LoginRequest(string Email, string Password);
public sealed record AddCartItemRequest(Guid ProductId, int Quantity);
public sealed record QuantityRequest(int Quantity);

public sealed class CsrfFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem("Your security token is missing or expired. Refresh and try again.", statusCode: 400);
        }
        return await next(context);
    }
}
