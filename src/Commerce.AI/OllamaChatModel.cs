using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commerce.AI;

// This adapter proposes calls; it never executes commerce operations or retries a request.
// Reuse one instance per runtime to keep GPU inference serial.
public sealed class OllamaChatModel : IStreamingChatModel, IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
    private readonly HttpClient http;
    private readonly OllamaOptions options;
    private readonly SemaphoreSlim inference = new(1, 1);

    public OllamaChatModel(HttpClient http, OllamaOptions? options = null)
    {
        this.http = http;
        this.options = options ?? new();
        if (http.BaseAddress is not { Scheme: "http" or "https" } address ||
            (!address.IsLoopback && address.Host != "host.docker.internal"))
            throw new ArgumentException("Ollama must use loopback or the Docker host gateway.", nameof(http));
        if (this.options.ContextLength is < 2048 or > 32768 || this.options.MaxOutputTokens is < 128 or > 4096 ||
            this.options.MaxOutputTokens >= this.options.ContextLength || this.options.MaxRequestBytes is < 1024 or > 65536 ||
            this.options.Timeout <= TimeSpan.Zero || this.options.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Invalid model resource limits.", nameof(options));
    }

    public Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelTool> tools, CancellationToken cancellationToken = default) => RunAsync(messages, tools, null, cancellationToken);

    public Task<ModelReply> StreamAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelTool> tools,
        Func<string, Task> onText, CancellationToken cancellationToken = default) => RunAsync(messages, tools, onText, cancellationToken);

    private async Task<ModelReply> RunAsync(IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelTool> tools, Func<string, Task>? onText, CancellationToken cancellationToken)
    {
        if (messages.Count is < 1 or > 64 || messages.Any(m => m.Role is not ("system" or "user" or "assistant" or "tool")))
            throw new ModelProtocolException("Invalid or oversized conversation.");
        if (tools.Count > 24 || tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ModelProtocolException("Invalid tool catalog.");
        var request = new
        {
            model = options.Model, messages, stream = onText is not null, think = options.Think, keep_alive = "10m",
            tools = tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } }),
            options = new { num_ctx = options.ContextLength, num_predict = options.MaxOutputTokens, temperature = 0, seed = 42 }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (bytes.Length > options.MaxRequestBytes)
            throw new ModelProtocolException("Conversation exceeds the input budget. Start a shorter context; history was not silently truncated.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        await inference.WaitAsync(deadline.Token);
        try
        {
            using var body = new ByteArrayContent(bytes);
            body.Headers.ContentType = new("application/json");
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = body };
            using var response = await http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            // Bound bytes before deserialization, including chunked responses without Content-Length.
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            if (onText is not null)
            {
                var text = new System.Text.StringBuilder();
                var calls = new List<ModelToolCall>();
                JsonElement? final = null;
                using var line = new MemoryStream();
                var input = new byte[4096];
                var total = 0;
                int count;
                while ((count = await stream.ReadAsync(input, deadline.Token)) > 0)
                {
                    total += count;
                    if (total > 1_048_576) throw new ModelProtocolException("Stream exceeded its byte budget.");
                    for (var i = 0; i < count; i++)
                    {
                        if (input[i] != '\n') { line.WriteByte(input[i]); continue; }
                        if (line.Length == 0) continue;
                        using var frame = JsonDocument.Parse(line.ToArray());
                        line.SetLength(0);
                        var value = frame.RootElement;
                        if (final is not null) throw new ModelProtocolException("Unexpected data after model completion.");
                        if (value.TryGetProperty("error", out _)) throw new ModelProtocolException("Model stream failed.");
                        if (value.TryGetProperty("message", out var part))
                        {
                            var messagePart = part.Deserialize<ModelMessage>(Json)!;
                            if (messagePart is null || messagePart.Role != "assistant") throw new ModelProtocolException("Invalid streamed role.");
                            if (!string.IsNullOrEmpty(messagePart.Content))
                            {
                                text.Append(messagePart.Content);
                                await onText(messagePart.Content);
                            }
                            calls.AddRange(messagePart.ToolCalls ?? []);
                            if (calls.Count > 4) throw new ModelProtocolException("Too many proposed calls.");
                        }
                        if (value.TryGetProperty("done", out var finished) && finished.ValueKind == JsonValueKind.True)
                            final = value.Clone();
                    }
                }
                if (final is not { } last || line.Length != 0) throw new ModelProtocolException("Model stream ended before completion.");
                var assembled = new { done = true, done_reason = last.GetProperty("done_reason").GetString(),
                    message = new ModelMessage("assistant", text.ToString(), calls),
                    prompt_eval_count = Number(last, "prompt_eval_count"), eval_count = Number(last, "eval_count"),
                    total_duration = Long(last, "total_duration"), load_duration = Long(last, "load_duration") };
                buffer.Write(JsonSerializer.SerializeToUtf8Bytes(assembled, Json));
            }
            else
            {
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + read > 131_072) throw new ModelProtocolException("Model response exceeds the output budget.");
                buffer.Write(chunk, 0, read);
            }
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("done_reason", out var reason) || reason.GetString() != "stop")
                throw new ModelProtocolException("Model response was incomplete; no proposed calls may execute.");
            if (!root.TryGetProperty("message", out var message)) throw new ModelProtocolException("Model response has no message.");
            var result = message.Deserialize<ModelMessage>(Json) ?? throw new ModelProtocolException("Empty model response.");
            if (result.Role != "assistant" || result.Content is null || (result.ToolCalls?.Count ?? 0) > 4)
                throw new ModelProtocolException("Invalid assistant message.");
            if (string.IsNullOrWhiteSpace(result.Content) && (result.ToolCalls?.Count ?? 0) == 0)
                throw new ModelProtocolException("Assistant returned neither text nor a proposed call.");
            foreach (var call in result.ToolCalls ?? [])
            {
                if (call?.Function is null) throw new ModelProtocolException("Missing tool call function.");
                var tool = tools.SingleOrDefault(t => t.Name == call.Function?.Name)
                    ?? throw new ModelProtocolException("Model proposed an unavailable tool.");
                ToolArguments.Validate(tool.Parameters, call.Function!.Arguments);
            }
            var usage = new ModelUsage(Number(root, "prompt_eval_count"), Number(root, "eval_count"),
                Long(root, "total_duration"), Long(root, "load_duration"));
            if (usage.InputTokens <= 0 || usage.OutputTokens < 0 ||
                usage.InputTokens >= options.ContextLength - options.MaxOutputTokens - 256 ||
                usage.InputTokens + usage.OutputTokens >= options.ContextLength)
                throw new ModelProtocolException("Model exhausted its context; no proposed calls may execute.");
            // Unknown fields, including hidden thinking, are intentionally discarded.
            return new(result, usage);
        }
        catch (JsonException) { throw new ModelProtocolException("Model returned malformed JSON."); }
        catch (InvalidOperationException) { throw new ModelProtocolException("Model returned an invalid response shape."); }
        finally { inference.Release(); }
    }

    private static int Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
    private static long Long(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
    public void Dispose() => inference.Dispose();
}
