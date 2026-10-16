using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commerce.AI;

public sealed record ModelTool(string Name, string Description, JsonElement Parameters);
public sealed record ModelFunction(string Name, JsonElement Arguments);
public sealed record ModelToolCall(ModelFunction Function);
public sealed record ModelMessage(string Role, string Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ModelToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null);
public sealed record ModelUsage(int InputTokens, int OutputTokens, long TotalNanoseconds, long LoadNanoseconds);
public sealed record ModelReply(ModelMessage Message, ModelUsage Usage);

public sealed record OllamaOptions
{
    public string Model { get; init; } = "qwen3.8:27b";
    public int ContextLength { get; init; } = 8192;
    public int MaxOutputTokens { get; init; } = 2048;
    public bool Think { get; init; } = true;
    public int MaxRequestBytes { get; init; } = 24_576;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
}

public interface IChatModel
{
    Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelTool> tools, CancellationToken cancellationToken = default);
}

public interface IStreamingChatModel : IChatModel
{
    Task<ModelReply> StreamAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelTool> tools,
        Func<string, Task> onText, CancellationToken cancellationToken = default);
}

public sealed class ModelProtocolException(string message) : Exception(message);
