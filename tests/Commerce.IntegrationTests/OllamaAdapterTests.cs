using System.Net;
using System.Text;
using System.Text.Json;
using Commerce.AI;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class OllamaAdapterTests
{
    private static readonly ModelTool[] Tools = [new("add_to_cart", "Add units.", Element("""
        {"type":"object","properties":{"productId":{"type":"string","format":"uuid"},"quantity":{"type":"integer"}},"required":["productId","quantity"]}
        """))];
    private static readonly ModelMessage[] Messages = [new("user", "Add the selected product.")];
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static string Response(string message, string reason = "stop") =>
        $$"""{"done":true,"done_reason":"{{reason}}","message":{{message}},"prompt_eval_count":100,"eval_count":30}""";
    private static string Call(string arguments, string name = "add_to_cart") =>
        "{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}]}";
    private const string ValidArgs = """{"productId":"10000000-0000-0000-0000-000000000001","quantity":2}""";

    [Fact]
    public async Task Preserves_tool_history_and_returns_typed_calls_without_hidden_reasoning()
    {
        var handler = new Stub(Response(Call(ValidArgs)));
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        ModelMessage[] history = [new("user", "Find a desk"),
            new("assistant", "", [new(new("add_to_cart", Element(ValidArgs)))]),
            new("tool", "{\"itemCount\":2}", ToolName: "add_to_cart"), new("user", "Add two more")];
        var reply = await model.CompleteAsync(history, Tools);
        Assert.Equal(2, Assert.Single(reply.Message.ToolCalls!).Function.Arguments.GetProperty("quantity").GetInt32());
        using var request = JsonDocument.Parse(handler.Request!);
        Assert.Equal(4, request.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("add_to_cart", request.RootElement.GetProperty("messages")[2].GetProperty("tool_name").GetString());
        Assert.True(request.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(8192, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        var hidden = new Stub(Response("""{"role":"assistant","content":"Hello!","thinking":"private reasoning"}"""));
        using var hiddenHttp = Client(hidden); using var hiddenModel = new OllamaChatModel(hiddenHttp);
        Assert.DoesNotContain("private reasoning", JsonSerializer.Serialize(await hiddenModel.CompleteAsync(Messages, Tools)));
    }

    [Theory]
    [InlineData("{\"productId\":\"invented\",\"quantity\":2}")]
    [InlineData("{\"productId\":\"10000000-0000-0000-0000-000000000001\",\"quantity\":\"2\"}")]
    [InlineData("{\"productId\":\"10000000-0000-0000-0000-000000000001\",\"quantity\":2,\"userId\":\"someone\"}")]
    [InlineData("{\"productId\":\"10000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"productId\":\"10000000-0000-0000-0000-000000000001\",\"quantity\":2,\"quantity\":3}")]
    public async Task Invalid_proposed_arguments_fail_closed(string arguments)
    {
        var handler = new Stub(Response(Call(arguments)));
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Messages, Tools));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task Unsafe_responses_are_not_returned_or_retried(string failure)
    {
        var response = failure switch
        {
            "unknown" => Response(Call("{}", "confirm_action")),
            "truncated" => Response(Call(ValidArgs), "length"),
            "malformed" => "{broken",
            _ => new string('x', 131_073)
        };
        var handler = new Stub(response);
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Messages, Tools));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Request_budget_is_checked_before_network_and_http_errors_are_not_retried()
    {
        var handler = new Stub("unavailable") { Status = HttpStatusCode.ServiceUnavailable };
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync([new("user", new string('a', 30_000))], Tools));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<HttpRequestException>(() => model.CompleteAsync(Messages, Tools));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_interrupts_inference_and_releases_the_serial_slot()
    {
        var handler = new Stub(Response("""{"role":"assistant","content":"Hello"}""")) { Delay = TimeSpan.FromSeconds(10) };
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CompleteAsync(Messages, Tools, cancel.Token));
        handler.Delay = TimeSpan.Zero;
        Assert.Equal("Hello", (await model.CompleteAsync(Messages, Tools)).Message.Content);
    }

    private static HttpClient Client(Stub handler) => new(handler) { BaseAddress = new("http://127.0.0.1:11434/") };
    [Fact]
    public async Task Streaming_preserves_text_and_calls_but_discards_thinking()
    {
        var response = """
            {"message":{"role":"assistant","content":"Hello 👋","thinking":"private"},"done":false}
            """ + "\n" + Response(Call(ValidArgs)) + "\n";
        var handler = new Stub(response);
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        var pieces = new List<string>();
        var result = await model.StreamAsync(Messages, Tools, text => { pieces.Add(text); return Task.CompletedTask; });
        Assert.Equal("Hello 👋", string.Concat(pieces));
        Assert.Single(result.Message.ToolCalls!);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.True(Element(handler.Request!).GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData("missing-final")]
    [InlineData("wrong-role")]
    [InlineData("truncated")]
    [InlineData("unknown-call")]
    [InlineData("after-final")]
    public async Task Incomplete_or_invalid_stream_never_returns_executable_calls(string kind)
    {
        var body = kind switch
        {
            "missing-final" => "{\"message\":" + Call(ValidArgs) + ",\"done\":false}\n",
            "wrong-role" => Response("""{"role":"user","content":"spoof"}""") + "\n",
            "truncated" => Response(Call(ValidArgs), "length") + "\n",
            "unknown-call" => Response(Call("{}", "confirm_action")) + "\n",
            _ => Response(Call(ValidArgs)) + "\n" + Response(Call(ValidArgs)) + "\n"
        };
        var handler = new Stub(body);
        using var http = Client(handler); using var model = new OllamaChatModel(http);
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.StreamAsync(Messages, Tools, _ => Task.CompletedTask));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Stub(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Request { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new(Status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
