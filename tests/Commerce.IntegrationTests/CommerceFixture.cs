using System.Net.Http.Json;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class CommerceFixture : IAsyncLifetime
{
    private readonly string database = $"commercepilot_test_{Guid.NewGuid():N}";
    private string adminConnection = "";
    private bool databaseCreated;
    public CommerceFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        adminConnection = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException("Use scripts/test.ps1 to provide the local PostgreSQL connection.");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
        await command.ExecuteNonQueryAsync();
        databaseCreated = true;
        var connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
        await using var db = new CommerceDbContext(new DbContextOptionsBuilder<CommerceDbContext>().UseNpgsql(connection).Options);
        await CatalogSeed.InitializeAsync(db);
        Factory = new CommerceFactory(connection);
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
    });

    public async Task<(HttpClient Client, CommerceUser User)> SignedInAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CommerceUser>>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var password = $"Test!{Guid.NewGuid():N}A9";
        var user = new CommerceUser { Email = email, UserName = email, DisplayName = "Test shopper" };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(error => error.Description)));
        var client = Client();
        using var login = await MutateAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("commercepilot.session=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        return (client, user);
    }

    public static async Task<HttpResponseMessage> MutateAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var csrf = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-CSRF-Token", csrf!.Token);
        if (body is not null) message.Content = JsonContent.Create(body);
        return await client.SendAsync(message);
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (!databaseCreated) return;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        // Only the unique, fixture-created database is removed. The developer's database is never used.
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }

    public sealed record Csrf(string Token);
}

public sealed class CommerceFactory(string connection) : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Commerce", connection);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
