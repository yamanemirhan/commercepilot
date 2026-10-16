using System.Net.Http.Headers;
using System.Text.Json;
using Commerce.AI;
using Commerce.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Commerce.Api;

public sealed record ChatToolResult(bool IsError, JsonElement Data);
public interface IChatToolSession : IAsyncDisposable
{
    IReadOnlyList<ModelTool> Tools { get; }
    Task<ChatToolResult> CallAsync(ModelFunction call, CancellationToken ct);
}
public interface IChatToolFactory
{
    Task<IChatToolSession> OpenAsync(CommerceUser user, CancellationToken ct);
}

// Each connection uses an owner-bound, short-lived fixed grant. No token reaches the model/browser.
public sealed class McpChatToolFactory(McpAccessTokens tokens, IHttpClientFactory clients, IConfiguration config) : IChatToolFactory
{
    public async Task<IChatToolSession> OpenAsync(CommerceUser user, CancellationToken ct)
    {
        var session = new Session();
        try
        {
            foreach (var grant in new[] { McpAccessGrant.CartWrite, McpAccessGrant.OrdersRead,
                McpAccessGrant.CheckoutPrepare, McpAccessGrant.CancellationPrepare, McpAccessGrant.RefundPrepare })
            {
                var http = clients.CreateClient("chat-mcp");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Issue(user, grant).AccessToken);
                var transport = new HttpClientTransport(new()
                {
                    Endpoint = new(config["Chat:McpUrl"] ?? "http://127.0.0.1:5081/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                }, http);
                session.Resources.Add((http, transport));
                var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                session.Clients.Add(client);
                foreach (var tool in await client.ListToolsAsync(cancellationToken: ct))
                {
                    if (!ChatPolicy.ToolNames.Contains(tool.Name)) continue;
                    session.Registry.TryAdd(tool.Name, (new(tool.Name, tool.Description ?? "", tool.JsonSchema), client));
                }
            }
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private sealed class Session : IChatToolSession
    {
        public Dictionary<string, (ModelTool Tool, McpClient Client)> Registry { get; } = [];
        public List<(HttpClient Http, HttpClientTransport Transport)> Resources { get; } = [];
        public List<McpClient> Clients { get; } = [];
        public IReadOnlyList<ModelTool> Tools => Registry.Values.Select(t => t.Tool).ToArray();
        public async Task<ChatToolResult> CallAsync(ModelFunction call, CancellationToken ct)
        {
            if (!Registry.TryGetValue(call.Name, out var entry)) throw new ModelProtocolException("Unavailable tool.");
            ToolArguments.Validate(entry.Tool.Parameters, call.Arguments);
            var result = await entry.Client.CallToolAsync(call.Name,
                call.Arguments.Deserialize<Dictionary<string, object?>>(OllamaChatModel.Json), cancellationToken: ct);
            var data = result.IsError == true || result.StructuredContent is null
                ? JsonSerializer.SerializeToElement(new { message = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)) })
                : JsonSerializer.SerializeToElement(result.StructuredContent, OllamaChatModel.Json);
            if (data.GetRawText().Length > 100_000) throw new ModelProtocolException("Tool result is too large.");
            return new(result.IsError == true, data);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients) await client.DisposeAsync();
            foreach (var (http, transport) in Resources) { await transport.DisposeAsync(); http.Dispose(); }
        }
    }
}
