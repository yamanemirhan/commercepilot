using System.Text.Json;
using System.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// This client has no database connection, commerce service reference or language model.
// Catalog/cart reads use MCP. Optional shopper login uses the local identity API.
var endpoint = new Uri("http://127.0.0.1:5081/mcp");
var json = new JsonSerializerOptions { WriteIndented = true };
try
{
    var cartMode = args.FirstOrDefault() == "--cart";
    var token = cartMode ? await ShopperLogin.GetTokenAsync(args.ElementAtOrDefault(1) ?? "alex", CancellationToken.None) : null;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint,
        TransportMode = HttpTransportMode.StreamableHttp
    }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

    Console.WriteLine($"Connected to {endpoint}");
    Console.WriteLine("\n1. tools/list: discover the server's tools and their input schemas.");
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    foreach (var tool in tools)
    {
        Console.WriteLine($"\n{tool.Name}: {tool.Description}");
        Console.WriteLine(JsonSerializer.Serialize(tool.JsonSchema, json));
    }

    var arguments = cartMode ? new Dictionary<string, object?>() : new Dictionary<string, object?>
    {
        ["search"] = args.Length > 0 ? args[0] : "wireless",
        ["pageSize"] = 5,
        ["inStock"] = true
    };
    var toolName = cartMode ? "get_cart" : "search_products";
    Console.WriteLine($"\n2. tools/call: call {toolName} with these arguments.");
    Console.WriteLine(JsonSerializer.Serialize(arguments, json));
    var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);

    Console.WriteLine($"\n3. Tool result (isError: {result.IsError is true}):");
    if (result.StructuredContent is { } structured)
        Console.WriteLine(JsonSerializer.Serialize(structured, json));
    else
        foreach (var text in result.Content.OfType<TextContentBlock>()) Console.WriteLine(text.Text);
    if (result.IsError is true) return 1;
    Console.WriteLine("\nThe tool returned database results. No AI model was involved.");
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"MCP request failed: {exception.Message}");
    Console.Error.WriteLine("Check Docker Desktop, then run: docker compose --profile mcp up --build -d mcp");
    return 1;
}
