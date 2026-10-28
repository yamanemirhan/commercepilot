using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Commerce.AI;

// The internal graph proposes tools. ChatService retains the authenticated MCP
// loop, persistence and human decisions. Transport failures never trigger retry.
public sealed class LangGraphChatModel : IStreamingChatModel
{
    private readonly HttpClient http;
    private readonly string secret;

    public LangGraphChatModel(HttpClient http, string secret)
    {
        if (http.BaseAddress is not { Scheme: "http" } address ||
            (!address.IsLoopback && address.Host != "agent") || !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("The agent must use loopback or the internal agent service.", nameof(http));
        if (secret.Length != 64 || secret.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Configure a random 32-byte agent key.", nameof(secret));
        this.http = http; this.secret = secret;
    }

    public Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelTool> tools,
        CancellationToken cancellationToken = default) => StreamAsync(messages, tools, _ => Task.CompletedTask, cancellationToken);

    public async Task<ModelReply> StreamAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelTool> tools,
        Func<string, Task> onText, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { messages, tools }, OllamaChatModel.Json);
        if (bytes.Length > 24_576) throw new ModelProtocolException("Conversation exceeds the input budget.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "round") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new ModelProtocolException("Invalid agent stream type.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var line = new MemoryStream();
        var chunk = new byte[4096];
        var text = new StringBuilder();
        var total = 0;
        ModelReply? final = null;
        int count;
        try
        {
            while ((count = await stream.ReadAsync(chunk, ct)) > 0)
            {
                total += count;
                if (total > 1_048_576) throw new ModelProtocolException("Agent stream exceeded its byte budget.");
                for (var i = 0; i < count; i++)
                {
                    if (chunk[i] != '\n') { line.WriteByte(chunk[i]); continue; }
                    if (line.Length == 0) throw new ModelProtocolException("Empty agent frame.");
                    using var frame = JsonDocument.Parse(line.ToArray(), new() { MaxDepth = 32 });
                    line.SetLength(0);
                    if (final is not null) throw new ModelProtocolException("Unexpected data after agent completion.");
                    var root = frame.RootElement;
                    switch (root.GetProperty("type").GetString())
                    {
                        case "delta":
                            var delta = root.GetProperty("text").GetString() ?? throw new ModelProtocolException("Invalid text.");
                            text.Append(delta); await onText(delta); break;
                        case "result":
                            final = root.GetProperty("reply").Deserialize<ModelReply>(OllamaChatModel.Json)
                                ?? throw new ModelProtocolException("Missing agent reply.");
                            Validate(final, tools, text.ToString());
                            var expected = final.Message.ToolCalls is { Count: > 0 } ? "tools" : "answer";
                            if (root.GetProperty("route").GetString() != expected) throw new ModelProtocolException("Invalid graph route.");
                            break;
                        default: throw new ModelProtocolException("The agent round did not complete.");
                    }
                }
            }
            if (line.Length != 0 || final is null) throw new ModelProtocolException("Agent stream ended before completion.");
            return final;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ModelProtocolException("The agent returned an invalid stream."); }
    }

    private static void Validate(ModelReply reply, IReadOnlyList<ModelTool> tools, string text)
    {
        if (reply.Message is not { Role: "assistant" } message || message.Content != text || reply.Usage is not { } usage ||
            usage.InputTokens is <= 0 or >= 5888 || usage.OutputTokens is < 0 or >= 8192 ||
            usage.InputTokens + usage.OutputTokens >= 8192 || message.ToolCalls?.Count > 4 ||
            (message.ToolCalls is not { Count: > 0 } && string.IsNullOrWhiteSpace(text)))
            throw new ModelProtocolException("Invalid agent reply or token budget.");
        // Validate the entire batch before ChatService can execute the first call.
        foreach (var call in message.ToolCalls ?? [])
        {
            if (call?.Function is not { } function) throw new ModelProtocolException("Invalid tool call.");
            var tool = tools.SingleOrDefault(t => t.Name == function.Name)
                ?? throw new ModelProtocolException("Unavailable operation.");
            ToolArguments.Validate(tool.Parameters, function.Arguments);
        }
    }
}
