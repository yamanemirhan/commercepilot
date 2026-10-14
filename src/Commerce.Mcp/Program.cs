using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;

namespace Commerce.Mcp;

public sealed class McpProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
        builder.Services.AddProblemDetails();
        builder.Services.AddDbContext<CommerceDbContext>(options => options.UseNpgsql(
            builder.Configuration.GetConnectionString("Commerce")
                ?? throw new InvalidOperationException("ConnectionStrings__Commerce is required.")));
        builder.Services.AddScoped<ICatalogService, CatalogService>();
        builder.Services.AddScoped<ICartService, CartService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDataProtection().SetApplicationName("CommercePilot")
            .PersistKeysToDbContext<CommerceDbContext>();
        builder.Services.AddSingleton<McpAccessTokens>();
        builder.Services.AddAuthentication(McpAccessTokens.Scheme)
            .AddScheme<AuthenticationSchemeOptions, McpAuthenticationHandler>(McpAccessTokens.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(McpAccessTokens.CartRead,
                policy => policy.RequireAuthenticatedUser().RequireClaim("scope", McpAccessTokens.CartRead));
            options.AddPolicy(McpAccessTokens.CartWrite,
                policy => policy.RequireAuthenticatedUser().RequireClaim("scope", McpAccessTokens.CartWrite));
        });

        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithTools<CatalogTools>()
            .WithTools<CartTools>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // This endpoint is for native MCP clients. Browser-origin requests are not enabled.
            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { detail = "Browser-origin requests are not enabled for this MCP endpoint." });
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            // Invalid credentials must not silently fall back to public access.
            if (context.Request.Headers.ContainsKey("Authorization") && context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next();
        });
        app.UseAuthorization();
        app.MapGet("/health/ready", async (CommerceDbContext db, CancellationToken ct) =>
        {
            // Check the migrated catalog, not just whether a database connection can be opened.
            await db.Products.AsNoTracking().Select(p => p.Id).Take(1).ToListAsync(ct);
            return Results.Ok(new { status = "ready" });
        });
        app.MapMcp("/mcp");
        app.Run();
    }
}
