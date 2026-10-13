using Commerce.Application;
using Commerce.Infrastructure;
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
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// Explicit, local-only bootstrap. Production migrations run as a separate deployment operation.
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Database:Initialize"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await CatalogSeed.InitializeAsync(scope.ServiceProvider.GetRequiredService<CommerceDbContext>());
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
