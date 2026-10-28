using System.Net;
using System.Text;
using System.Text.Json;
using Commerce.AI;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class LangGraphAdapterTests
{
    private static readonly ModelMessage[] Messages = [new("user", "Hello")];
    private static string Frame(object value) => JsonSerializer.Serialize(value, OllamaChatModel.Json) + "\n";
    private static string Answer(string text = "Hello") => Frame(new { type = "delta", text }) + Frame(new
    {
        type = "result", route = "answer", reply = new ModelReply(new("assistant", text, []), new(100, 20, 0, 0))
    });
    [Fact]
    public async Task Streams_public_text_and_authenticates_only_the_internal_transport()
    {
        var handler = new Stub(Answer());
        using var http = Client(handler);
        var model = new LangGraphChatModel(http, new('a', 64));
        var text = new StringBuilder();
        var result = await model.StreamAsync(Messages, [], part => { text.Append(part); return Task.CompletedTask; });
        Assert.Equal("Hello", text.ToString()); Assert.Equal(100, result.Usage.InputTokens);
        Assert.Equal("Bearer " + new string('a', 64), handler.Authorization);
        Assert.DoesNotContain(new string('a', 64), handler.Body);
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("error")]
    [InlineData("oversized")]
    [InlineData("mismatch")]
    public async Task Failed_streams_do_not_return_a_reply_or_retry(string scenario)
    {
        var stream = scenario switch
        {
            "truncated" => Frame(new { type = "delta", text = "Partial" }),
            "extra" => Answer() + Frame(new { type = "delta", text = "unexpected" }),
            "error" => "{\"type\":\"error\"}\n",
            "oversized" => new string('x', 1_048_577),
            _ => Answer().Replace("\"content\":\"Hello\"", "\"content\":\"Different\"")
        };
        var handler = new Stub(stream); using var http = Client(handler);
        var model = new LangGraphChatModel(http, new('a', 64));
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Messages, []));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Unknown_call_in_batch_prevents_any_handoff()
    {
        var empty = JsonSerializer.Deserialize<JsonElement>("{}");
        var handler = new Stub(Frame(new { type = "result", route = "tools", reply = new ModelReply(
            new("assistant", "", [new(new("get_cart", empty)), new(new("confirm_action", empty))]), new(100, 20, 0, 0)) }));
        using var http = Client(handler); var model = new LangGraphChatModel(http, new('a', 64));
        ModelTool[] tools = [new("get_cart", "Read", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}"))];
        await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Messages, tools));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Unavailable_agent_is_not_retried_or_replaced_by_another_backend()
    {
        var handler = new Stub("") { Status = HttpStatusCode.ServiceUnavailable };
        using var http = Client(handler); var model = new LangGraphChatModel(http, new('a', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => model.CompleteAsync(Messages, []));
        Assert.Equal(1, handler.Calls);
    }
    private static HttpClient Client(Stub stub) => new(stub) { BaseAddress = new("http://agent:8080/") };
    private sealed class Stub(string output) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = "";
        public string? Authorization { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(ct); Authorization = request.Headers.Authorization?.ToString();
            return new(Status) { Content = new StringContent(output, Encoding.UTF8, "application/x-ndjson") };
        }
    }
}
