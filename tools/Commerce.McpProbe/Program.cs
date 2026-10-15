using System.Text.Json;
using System.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// This client has no database connection, commerce service reference or language model.
// Commerce operations use MCP. Optional shopper login uses the local identity API.
var oauthMode = args.FirstOrDefault() == "--oauth";
if (oauthMode)
{
    args = args.Skip(1).ToArray();
    // OAuth chooses the shopper in the browser, so it has no account argument.
    if (args.Length > 0 && args[0] != "--help") args = [args[0], "browser", .. args.Skip(1)];
}
var endpoint = new Uri(oauthMode ? "http://127.0.0.1:5081/mcp/authorized" : "http://127.0.0.1:5081/mcp");
var json = new JsonSerializerOptions { WriteIndented = true };
var mutationStarted = false;
var preparationStarted = false;
try
{
    var cartMode = args.FirstOrDefault() == "--cart";
    var ordersMode = args.FirstOrDefault() == "--orders";
    var orderMode = args.FirstOrDefault() == "--order";
    var orderReadMode = ordersMode || orderMode;
    var prepareMode = args.FirstOrDefault() == "--prepare";
    var actionMode = args.FirstOrDefault() == "--action";
    var cancellationMode = args.FirstOrDefault() == "--prepare-cancellation";
    var refundMode = args.FirstOrDefault() == "--prepare-refund";
    var changeMode = cancellationMode || refundMode;
    var approvalMode = prepareMode || actionMode || changeMode;
    var addMode = args.FirstOrDefault() == "--add";
    var setMode = args.FirstOrDefault() == "--set";
    var removeMode = args.FirstOrDefault() == "--remove";
    var writeMode = addMode || setMode || removeMode;
    var productMode = args.FirstOrDefault() == "--product";
    if (args.FirstOrDefault() == "--help")
    {
        Console.WriteLine("Usage: <search text> | --product <product-id> | --cart <alex|sam> | --add <alex|sam> <product-id> <quantity> | --set <alex|sam> <product-id> <total quantity> | --remove <alex|sam> <product-id>");
        Console.WriteLine("Order reads: --orders <alex|sam> | --order <alex|sam> <order-id from get_my_orders>");
        Console.WriteLine("Reviews: --prepare <alex|sam> <success|failure|delay> | --action <alex|sam> <action-id>");
        Console.WriteLine("Order reviews: --prepare-cancellation <alex|sam> <order-id> | --prepare-refund <alex|sam> <order-id>");
        Console.WriteLine("OAuth: prefix --oauth and omit the account, e.g. --oauth --orders or --oauth --prepare-cancellation <order-id>. Sign in and allow access in the browser.");
        return 0;
    }
    if (args.FirstOrDefault()?.StartsWith("--", StringComparison.Ordinal) is true && !cartMode && !writeMode && !productMode && !orderReadMode && !approvalMode)
        throw new ArgumentException("Unknown option. Run with --help for usage.");
    var productId = Guid.Empty;
    var orderId = Guid.Empty;
    var actionId = Guid.Empty;
    if (oauthMode && !(cartMode || writeMode || orderReadMode || approvalMode))
        throw new ArgumentException("Use --oauth with a shopper command such as --orders or --cart, omitting the account name.");
    if (changeMode && (args.Length != 3 || !Guid.TryParse(args[2], out orderId) || orderId == Guid.Empty))
        throw new ArgumentException("Usage: --prepare-cancellation or --prepare-refund <alex|sam> <order UUID>");
    if (prepareMode && (args.Length != 3 || args[2] is not ("success" or "failure" or "delay")))
        throw new ArgumentException("Usage: --prepare <alex|sam> <success|failure|delay>");
    if (actionMode && (args.Length != 3 || !Guid.TryParse(args[2], out actionId) || actionId == Guid.Empty))
        throw new ArgumentException("Usage: --action <alex|sam> <action UUID from prepare_order, not an order ID>");
    if (ordersMode && args.Length != 2) throw new ArgumentException("Usage: --orders <alex|sam>");
    if (orderMode && (args.Length != 3 || !Guid.TryParse(args[2], out orderId) || orderId == Guid.Empty))
        throw new ArgumentException("Usage: --order <alex|sam> <order UUID from get_my_orders, not the CP- display number>");
    var quantity = 0;
    if ((addMode || setMode) && (args.Length != 4 || !Guid.TryParse(args[2], out productId) || productId == Guid.Empty ||
        !int.TryParse(args[3], out quantity) || quantity is < 1 or > 99))
        throw new ArgumentException("Usage: --add or --set <alex|sam> <product-id> <quantity from 1 to 99>. Use --remove to remove an item.");
    if (removeMode && (args.Length != 3 || !Guid.TryParse(args[2], out productId) || productId == Guid.Empty))
        throw new ArgumentException("Usage: --remove <alex|sam> <product-id>");
    if (productMode && (args.Length != 2 || !Guid.TryParse(args[1], out productId) || productId == Guid.Empty))
        throw new ArgumentException("Usage: --product <product-id from search_products>");
    if (addMode) Console.WriteLine($"This command adds {quantity} unit(s) of {productId} to {args[1]}'s bag. It does not place an order.");
    if (setMode) Console.WriteLine($"This command sets {productId} to exactly {quantity} unit(s) in {args[1]}'s bag.");
    if (removeMode) Console.WriteLine($"This command removes the entire {productId} line from {args[1]}'s bag.");
    if (prepareMode) Console.WriteLine("This creates a five-minute checkout review and supersedes any previous pending checkout review. It does not place an order. Confirm the exact terms yourself in the storefront.");
    if (changeMode) Console.WriteLine("This only prepares a review; it does not cancel or refund the order. An earlier pending review of the same operation will be superseded.");
    var access = cancellationMode ? ShopperAccess.CancellationPrepare : refundMode ? ShopperAccess.RefundPrepare :
        prepareMode ? ShopperAccess.CheckoutPrepare : actionMode ? ShopperAccess.ActionsRead :
        orderReadMode ? ShopperAccess.OrdersRead : writeMode ? ShopperAccess.CartWrite : ShopperAccess.CartRead;
    var token = !oauthMode && (cartMode || writeMode || orderReadMode || approvalMode)
        ? await ShopperLogin.GetTokenAsync(args.ElementAtOrDefault(1) ?? "alex", CancellationToken.None, access)
        : null;
    using var timeout = new CancellationTokenSource(oauthMode ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint,
        TransportMode = HttpTransportMode.StreamableHttp,
        OAuth = oauthMode ? OAuthBrowser.Options(access) : null
    }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));

    Console.WriteLine($"Connected to {endpoint}");
    Console.WriteLine("\n1. tools/list: discover the server's tools and their input schemas.");
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    foreach (var tool in tools)
    {
        Console.WriteLine($"\n{tool.Name}: {tool.Description}");
        Console.WriteLine(JsonSerializer.Serialize(tool.JsonSchema, json));
    }

    if (approvalMode)
    {
        var approvalTool = cancellationMode ? "prepare_cancellation" : refundMode ? "prepare_refund" : prepareMode ? "prepare_order" : "get_pending_action_status";
        var approvalArguments = prepareMode
            ? new Dictionary<string, object?> { ["paymentMode"] = args[2] }
            : changeMode ? new Dictionary<string, object?> { ["orderId"] = orderId }
            : new Dictionary<string, object?> { ["actionId"] = actionId };
        Console.WriteLine($"\nCalling tools/call: {approvalTool} with these arguments.");
        Console.WriteLine(JsonSerializer.Serialize(approvalArguments, json));
        // A repeat preparation replaces the earlier review. Never retry it automatically.
        preparationStarted = prepareMode || changeMode;
        var review = await client.CallToolAsync(approvalTool, approvalArguments, cancellationToken: timeout.Token);
        PrintResult(review);
        if (review.IsError is true) return 1;
        Console.WriteLine(prepareMode || changeMode
            ? "\nOpen http://localhost:3000, sign in as the same shopper, open Orders and review the pending approval. Use --action with the returned id to read its status."
            : "\nReview status was read without approving or executing it. Completed does not necessarily mean payment succeeded; inspect the resulting order.");
        return 0;
    }

    if (orderReadMode)
    {
        var orderTool = ordersMode ? "get_my_orders" : "get_order_status";
        var orderArguments = new Dictionary<string, object?>();
        if (orderMode) orderArguments["orderId"] = orderId;
        Console.WriteLine($"\nCalling tools/call: {orderTool} with these arguments.");
        Console.WriteLine(JsonSerializer.Serialize(orderArguments, json));
        var orderResult = await client.CallToolAsync(orderTool, orderArguments, cancellationToken: timeout.Token);
        PrintResult(orderResult);
        if (orderResult.IsError is true) return 1;
        Console.WriteLine("\nOrder data was read without changing orders, payments or shipping.");
        return 0;
    }

    if (productMode || addMode)
    {
        Console.WriteLine("\n2. tools/call: get_product with the exact product ID.");
        var product = await client.CallToolAsync("get_product", new Dictionary<string, object?> { ["productId"] = productId }, cancellationToken: timeout.Token);
        PrintResult(product);
        if (product.IsError is true) return 1;
        if (productMode) return 0;
    }

    if (writeMode)
    {
        var mutationTool = addMode ? "add_to_cart" : setMode ? "update_cart_item" : "remove_from_cart";
        var mutationArguments = new Dictionary<string, object?> { ["productId"] = productId };
        if (!removeMode) mutationArguments["quantity"] = quantity;
        Console.WriteLine($"\nCalling tools/call: {mutationTool} with these arguments.");
        Console.WriteLine(JsonSerializer.Serialize(mutationArguments, json));
        // Even an exact set/remove could overwrite a later edit. Never retry writes automatically.
        mutationStarted = true;
        var changed = await client.CallToolAsync(mutationTool, mutationArguments, cancellationToken: timeout.Token);
        PrintResult(changed);
        if (changed.IsError is true)
        {
            Console.WriteLine("Inspect the error and read your bag before deciding whether to try again.");
            return 1;
        }
        Console.WriteLine("\nCalling tools/call: get_cart to read the persisted bag.");
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
    if (mutationStarted) Console.Error.WriteLine("The cart change may have completed. Read your bag with --cart before trying again.");
    if (preparationStarted) Console.Error.WriteLine("The review may have been created. Check pending approvals in the storefront before preparing another; a repeat supersedes the earlier review.");
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
