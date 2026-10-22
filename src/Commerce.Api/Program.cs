using Commerce.Application;
using Commerce.Api;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<CommerceDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Commerce")
        ?? throw new InvalidOperationException("ConnectionStrings__Commerce is required.")));
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<McpAccessTokens>();
builder.AddChat();
builder.AddCommerceOAuth();
builder.Services.AddScoped<IOrderService, OrderService>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<DemoOrderWorker>();
builder.Services.AddIdentityCore<CommerceUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<CommerceDbContext>().AddSignInManager();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "commercepilot.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "commercepilot.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
});
builder.Services.AddDataProtection().SetApplicationName("CommercePilot")
    .PersistKeysToDbContext<CommerceDbContext>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("chat", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("oauth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    options.AddPolicy("mcp-token", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();
app.UseExceptionHandler();
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/admin")) context.Response.Headers.CacheControl = "no-store";
    return next();
});
app.UseStatusCodePages();
app.Use((context, next) => app.Services.GetRequiredService<OAuthRequestLimits>().InvokeAsync(context, next));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/chat"))
    {
        if (context.Request.ContentLength > 16_384) { context.Response.StatusCode = 413; return; }
        var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 16_384;
    }
    if (context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path.StartsWithSegments("/api/cart")
        || context.Request.Path.StartsWithSegments("/api/orders") || context.Request.Path.StartsWithSegments("/api/actions")
        || context.Request.Path.StartsWithSegments("/api/chat"))
        context.Response.Headers.CacheControl = "no-cache, no-store";
    await next();
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// Explicit, local-only bootstrap. Production migrations run as a separate deployment operation.
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Database:Initialize"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await CatalogSeed.InitializeAsync(scope.ServiceProvider.GetRequiredService<CommerceDbContext>());
    await IdentitySeed.InitializeAsync(scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>(), builder.Configuration);
    await IdentitySeed.InitializeAdministratorAsync(scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(), builder.Configuration);
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

var catalog = app.MapGroup("/api").WithTags("Catalog");
catalog.MapGet("/products", async (ICatalogService service, CancellationToken ct,
    string? search = null, string? category = null, decimal? minPrice = null,
    decimal? maxPrice = null, string sort = "featured", int page = 1, int pageSize = 12, bool inStock = false) =>
{
    var query = new CatalogQuery(search, category, minPrice, maxPrice, sort, page, pageSize, inStock);
    var errors = query.Validate();
    return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await service.SearchAsync(query, ct));
});
catalog.MapGet("/products/{id:guid}", async (Guid id, ICatalogService service, CancellationToken ct) =>
    await service.FindAsync(id, ct) is { } product ? Results.Ok(product) : Results.NotFound());
catalog.MapGet("/categories", async (ICatalogService service, CancellationToken ct) =>
    Results.Ok(await service.CategoriesAsync(ct)));
app.MapShoppingEndpoints();
app.MapOrderEndpoints();
app.MapChat();
app.MapAdmin();
app.MapCommerceOAuth();
await using (var oauthScope = app.Services.CreateAsyncScope())
    await oauthScope.ServiceProvider.GetRequiredService<CommerceOAuth>().RegisterClientsAsync(
        oauthScope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>());
app.Run();

public partial class Program;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database is unavailable.");
    }
}
