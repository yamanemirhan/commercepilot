using System.Text.Json;
using System.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// This client has no database connection, commerce service reference or language model.
// Catalog/cart operations use MCP. Optional shopper login uses the local identity API.
var endpoint = new Uri("http://127.0.0.1:5081/mcp");
var json = new JsonSerializerOptions { WriteIndented = true };
var mutationStarted = false;
try
{
    var cartMode = args.FirstOrDefault() == "--cart";
    var addMode = args.FirstOrDefault() == "--add";
    var productMode = args.FirstOrDefault() == "--product";
    if (args.FirstOrDefault() == "--help")
    {
        Console.WriteLine("Usage: <search text> | --product <product-id> | --cart <alex|sam> | --add <alex|sam> <product-id> <quantity>");
        return 0;
    }
    if (args.FirstOrDefault()?.StartsWith("--", StringComparison.Ordinal) is true && !cartMode && !addMode && !productMode)
        throw new ArgumentException("Unknown option. Run with --help for usage.");
    var productId = Guid.Empty;
    var quantity = 0;
    if (addMode && (args.Length != 4 || !Guid.TryParse(args[2], out productId) || productId == Guid.Empty ||
        !int.TryParse(args[3], out quantity) || quantity is < 1 or > 99))
        throw new ArgumentException("Usage: --add <alex|sam> <product-id> <quantity from 1 to 99>");
    if (productMode && (args.Length != 2 || !Guid.TryParse(args[1], out productId) || productId == Guid.Empty))
        throw new ArgumentException("Usage: --product <product-id from search_products>");
    if (addMode) Console.WriteLine($"This command adds {quantity} unit(s) of {productId} to {args[1]}'s bag. It does not place an order.");
    var token = cartMode || addMode
        ? await ShopperLogin.GetTokenAsync(args.ElementAtOrDefault(1) ?? "alex", CancellationToken.None, allowCartWrite: addMode)
        : null;
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

    if (productMode || addMode)
    {
        Console.WriteLine("\n2. tools/call: get_product with the exact product ID.");
        var product = await client.CallToolAsync("get_product", new Dictionary<string, object?> { ["productId"] = productId }, cancellationToken: timeout.Token);
        PrintResult(product);
        if (product.IsError is true) return 1;
        if (productMode) return 0;

        Console.WriteLine($"\n3. tools/call: add_to_cart, productId={productId}, quantity={quantity}.");
        // Additive mutations are sent once. Never automatically retry after a timeout/disconnect.
        mutationStarted = true;
        var added = await client.CallToolAsync("add_to_cart", new Dictionary<string, object?>
        {
            ["productId"] = productId, ["quantity"] = quantity
        }, cancellationToken: timeout.Token);
        PrintResult(added);
        if (added.IsError is true)
        {
            Console.WriteLine("Inspect the error and read your bag before deciding whether to try again.");
            return 1;
        }
        Console.WriteLine("\n4. tools/call: get_cart to read the persisted bag.");
        var saved = await client.CallToolAsync("get_cart", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        PrintResult(saved);
        return saved.IsError is true ? 1 : 0;
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

    PrintResult(result);
    if (result.IsError is true) return 1;
    Console.WriteLine("\nThe tool returned database results. No AI model was involved.");
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine($"MCP request failed: {exception.Message}");
    if (mutationStarted) Console.Error.WriteLine("The add may have completed. Read your bag with --cart before trying to add again.");
    Console.Error.WriteLine("Check Docker Desktop, then run: docker compose --profile mcp up --build -d mcp");
    return 1;
}

void PrintResult(CallToolResult result)
{
    Console.WriteLine($"\nTool result (isError: {result.IsError is true}):");
    if (result.StructuredContent is { } structured)
        Console.WriteLine(JsonSerializer.Serialize(structured, json));
    else
        foreach (var text in result.Content.OfType<TextContentBlock>()) Console.WriteLine(text.Text);
}
