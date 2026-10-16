using System.Text.Json;
using System.Diagnostics;
using Commerce.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Commerce.ModelProbe;

internal static class ModelProbeProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help")
        {
            Console.WriteLine("Usage: --catalog <English request> | --evaluate <corpus.json> <MCP schemas.json> <report.json> [repeats:1..5]");
            Console.WriteLine("Requires local Ollama and MCP on ports 11434 and 5081. Model: OLLAMA_MODEL or qwen3.8:27b.");
            Console.WriteLine("Catalog mode executes only public catalog reads. Evaluation never executes proposed tools.");
            return 0;
        }
        try
        {
            if (args[0] is not ("--catalog" or "--evaluate") || args.Length < 2)
                throw new ArgumentException("Unknown or incomplete command. Use --help.");
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { BaseAddress = new("http://127.0.0.1:11434/"), Timeout = Timeout.InfiniteTimeSpan };
            var options = new OllamaOptions { Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3.8:27b" };
            using var model = new OllamaChatModel(http, options);
            if (args[0] == "--evaluate")
            {
                if (args.Length is < 4 or > 5) throw new ArgumentException("Provide corpus, MCP schema export, report path, and optional repeats.");
                var repeats = args.Length == 5 ? int.Parse(args[4]) : 2;
                if (repeats is < 1 or > 5) throw new ArgumentException("Repeats must be 1..5.");
                var schemas = JsonSerializer.Deserialize<ModelTool[]>(await File.ReadAllTextAsync(args[2], cancel.Token), OllamaChatModel.Json)
                    ?? throw new ArgumentException("Empty MCP schema export.");
                if (schemas.Length != 12) throw new ArgumentException("Expected all 12 CommercePilot MCP tools for offline evaluation.");
                return await Evaluation.RunAsync(args[1], args[3], repeats, model, http, options, schemas, cancel.Token);
            }
            using var mcpHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new("http://127.0.0.1:5081/mcp"), TransportMode = HttpTransportMode.StreamableHttp
            }, mcpHttp);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancel.Token);
            var discovered = await client.ListToolsAsync(cancellationToken: cancel.Token);
            var tools = discovered.Select(t => new ModelTool(t.Name, t.Description ?? "", t.JsonSchema)).ToArray();
            Console.WriteLine($"Discovered {tools.Length} MCP tool schemas. Model: {options.Model}.");
            if (args.Length != 2) throw new ArgumentException("Quote the full English catalog request.");
            var publicTools = tools.Where(t => t.Name is "search_products" or "get_product").ToArray();
            var messages = new List<ModelMessage>
            {
                new("system", ShoppingInstructions.SystemMessage + "\nThis command only supports public catalog reads. Do not offer to add items, access accounts, or prepare orders here; direct those requests to the storefront."),
                new("user", args[1])
            };
            for (var round = 0; round < 4; round++)
            {
                var elapsed = Stopwatch.StartNew();
                var reply = await model.CompleteAsync(messages, publicTools, cancel.Token);
                Console.WriteLine($"Model round {round + 1}: {elapsed.Elapsed.TotalSeconds:F2}s, load {reply.Usage.LoadNanoseconds / 1e9:F2}s, {reply.Usage.InputTokens} input / {reply.Usage.OutputTokens} output tokens.");
                messages.Add(reply.Message);
                if (reply.Message.ToolCalls is not { Count: > 0 })
                {
                    Console.WriteLine(reply.Message.Content);
                    return 0;
                }
                foreach (var call in reply.Message.ToolCalls)
                {
                    // This executable has no shopper credentials. Never widen this read-only allowlist.
                    if (call.Function.Name is not ("search_products" or "get_product"))
                        throw new ModelProtocolException("Only catalog reads are allowed by this probe.");
                    Console.WriteLine($"MCP -> {call.Function.Name} {call.Function.Arguments.GetRawText()}");
                    var parameters = call.Function.Arguments.Deserialize<Dictionary<string, object?>>(OllamaChatModel.Json)!;
                    var result = await client.CallToolAsync(call.Function.Name, parameters, cancellationToken: cancel.Token);
                    var data = JsonSerializer.Serialize(new { isError = result.IsError == true, result.StructuredContent,
                        text = result.StructuredContent is null ? result.Content.OfType<TextContentBlock>().Select(c => c.Text) : null }, OllamaChatModel.Json);
                    messages.Add(new("tool", data, ToolName: call.Function.Name));
                }
            }
            throw new ModelProtocolException("Tool round limit reached; no further calls were made.");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ModelProtocolException or
            JsonException or ArgumentException or IOException or FormatException)
        {
            Console.Error.WriteLine($"Model probe stopped: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
