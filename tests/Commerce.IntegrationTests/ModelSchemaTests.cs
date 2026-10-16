using System.Net.Http.Headers;
using System.Text.Json;
using Commerce.AI;
using Commerce.Infrastructure;
using Commerce.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class ModelSchemaTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    [Fact]
    public async Task Scoped_discovery_exports_all_model_contracts_without_identity_arguments()
    {
        var (api, user) = await fixture.SignedInAsync();
        using (api)
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var connection = scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Database.GetConnectionString();
            using var factory = new WebApplicationFactory<McpProgram>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Commerce", connection);
                builder.UseSetting("Logging:LogLevel:Default", "Warning");
                builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(fixture.Factory.Clock));
            });
            var schemas = new Dictionary<string, ModelTool>();
            foreach (var grant in Enum.GetValues<McpAccessGrant>())
            {
                var token = scope.ServiceProvider.GetRequiredService<McpAccessTokens>().Issue(user, grant);
                using var http = factory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                await using var transport = new HttpClientTransport(new()
                {
                    Endpoint = new(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
                }, http);
                await using var client = await McpClient.CreateAsync(transport);
                foreach (var tool in await client.ListToolsAsync())
                    schemas[tool.Name] = new(tool.Name, tool.Description ?? "", tool.JsonSchema);
            }
            Assert.Equal(12, schemas.Count);
            Assert.DoesNotContain(schemas.Keys, name => name.Contains("confirm") || name.Contains("reject"));
            foreach (var schema in schemas.Values)
            {
                var properties = schema.Parameters.GetProperty("properties");
                Assert.False(properties.TryGetProperty("userId", out _));
                Assert.False(properties.TryGetProperty("approved", out _));
                Assert.False(properties.TryGetProperty("price", out _));
            }
            // Opt-in export for local evaluation. Contains schemas only, never credentials or user data.
            if (Environment.GetEnvironmentVariable("MODEL_SCHEMA_OUTPUT") is { Length: > 0 } path)
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(schemas.Values.OrderBy(s => s.Name),
                    new JsonSerializerOptions(OllamaChatModel.Json) { WriteIndented = true }));
        }
    }
}
