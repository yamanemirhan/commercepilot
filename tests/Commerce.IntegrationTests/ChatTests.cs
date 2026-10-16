using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.AI;
using Commerce.Api;
using Commerce.Application;
using Commerce.Domain;
using Commerce.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Commerce.IntegrationTests.CommerceFixture;

namespace Commerce.IntegrationTests;

public sealed class ChatTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, OllamaChatModel.Json);
    private static ModelReply Say(string text) => new(new("assistant", text), new(100, 20, 0, 0));
    private static ModelReply Call(string name, object args) => new(new("assistant", "", [new(new(name, Data(args)))]), new(100, 20, 0, 0));
    private static ChatService Service(IServiceProvider services, FakeModel model, FakeTools tools) =>
        ActivatorUtilities.CreateInstance<ChatService>(services, model, tools);
    private static async Task<Guid> Create(HttpClient client)
    {
        var response = await MutateAsync(client, HttpMethod.Post, "/api/chat/conversations", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task History_is_persistent_owned_and_csrf_protected_and_duplicate_turn_is_not_replayed()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var (other, _) = await fixture.SignedInAsync(); using var outsider = other;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/chat/conversations", new { })).StatusCode);
        var id = await Create(client);
        var model = new FakeModel(Say("Hello! What can I help you find?")); var tools = new FakeTools();
        var input = new ChatInput(Guid.NewGuid(), "Hello");
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var chat = Service(scope.ServiceProvider, model, tools);
            var turn = await chat.BeginAsync(user.Id, id, input, default);
            await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        }
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        Assert.Equal("Completed", read.GetProperty("turns")[0].GetProperty("status").GetString());
        Assert.Contains("Hello!", read.GetRawText());
        var duplicate = await MutateAsync(client, HttpMethod.Post, $"/api/chat/conversations/{id}/messages", input);
        duplicate.EnsureSuccessStatusCode();
        Assert.Equal("application/json", duplicate.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, model.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/chat/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(other, HttpMethod.Post, $"/api/chat/conversations/{id}/messages", new ChatInput(Guid.NewGuid(), "Hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(other, HttpMethod.Delete, $"/api/chat/conversations/{id}")).StatusCode);
        (await MutateAsync(client, HttpMethod.Delete, $"/api/chat/conversations/{id}")).EnsureSuccessStatusCode();
        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<CommerceDbContext>();
        Assert.False(await db.ChatTurns.AnyAsync(t => t.ConversationId == id));
        Assert.False(await db.ChatEvents.AnyAsync(t => t.ConversationId == id));
    }

    [Fact]
    public async Task Active_reply_excludes_concurrent_sends_and_delete_and_expired_lease_recovers_without_execution()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var model = new FakeModel(); var chat = Service(scope.ServiceProvider, model, new());
        await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Hello"), default);
        var blocked = await Assert.ThrowsAsync<OrderFault>(() => chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Again"), default));
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Delete, $"/api/chat/conversations/{id}")).StatusCode);
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        Assert.Equal("Interrupted", read.GetProperty("turns")[0].GetProperty("status").GetString());
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Tool_result_order_and_followup_references_survive_new_service_scope()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
        var page = await catalog.SearchAsync(new CatalogQuery(PageSize: 2), default);
        var tools = new FakeTools { Result = new(false, Data(page)) };
        var model = new FakeModel(Call("search_products", new { }), Say("Here are two options."));
        var chat = Service(scope.ServiceProvider, model, tools);
        var turn = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Find two products"), default);
        await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        await using var second = fixture.Factory.Services.CreateAsyncScope();
        var next = new FakeModel(Call("get_product", new { productId = page.Items[1].Id }), Say("Here is the second one."));
        var nextTools = new FakeTools { Result = new(false, Data(page.Items[1])) };
        var followup = Service(second.ServiceProvider, next, nextTools);
        var secondTurn = await followup.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Tell me about the second one"), default);
        await followup.RunAsync(user.Id, secondTurn!, _ => Task.CompletedTask, default);
        Assert.Single(nextTools.Executed);
        var context = JsonSerializer.Serialize(next.Contexts[0]);
        Assert.Contains(page.Items[1].Id.ToString(), context);
        Assert.Contains("Here are two options", context);
        Assert.Equal("Completed", secondTurn!.Status);
    }

    [Fact]
    public async Task Invented_reference_and_model_confirmation_never_execute_tools()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var model = new FakeModel(Call("get_product", new { productId = Guid.NewGuid() }), Say("Which product do you mean?"));
        var tools = new FakeTools(); var chat = Service(scope.ServiceProvider, model, tools);
        var turn = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "That one"), default);
        await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        Assert.Empty(tools.Executed);
        model.Replies.Enqueue(Call("confirm_action", new { }));
        turn = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "yes approve it"), default);
        await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        Assert.Equal("Failed", turn!.Status); Assert.Empty(tools.Executed);
    }

    [Fact]
    public async Task Lost_write_response_is_recorded_and_never_retried()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tools = new FakeTools { Fail = true };
        var model = new FakeModel(Call("prepare_order", new { paymentMode = "success" }));
        var chat = Service(scope.ServiceProvider, model, tools);
        var input = new ChatInput(Guid.NewGuid(), "Prepare checkout");
        var turn = await chat.BeginAsync(user.Id, id, input, default);
        await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        Assert.Equal("Failed", turn!.Status); Assert.Single(tools.Executed);
        Assert.Null(await chat.BeginAsync(user.Id, id, input, default));
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        Assert.Contains("outcomeUncertain\":true", read.GetRawText());
        model.Replies.Enqueue(Call("prepare_order", new { paymentMode = "success" }));
        model.Replies.Enqueue(Say("Please check the previous review."));
        var followup = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Did it work?"), default);
        await chat.RunAsync(user.Id, followup!, _ => Task.CompletedTask, default);
        Assert.Single(tools.Executed);
    }

    [Fact]
    public async Task Inline_approval_requires_owner_csrf_hash_and_repairs_order_card_after_lost_response()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var (other, _) = await fixture.SignedInAsync(); using var outsider = other;
        var id = await Create(client);
        PendingActionDto action; Guid cardId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var product = await db.Products.FirstAsync(p => p.Stock > 2);
            (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = product.Id, quantity = 1 })).EnsureSuccessStatusCode();
            var response = await MutateAsync(client, HttpMethod.Post, "/api/orders/prepare", new { paymentMode = "success" });
            response.EnsureSuccessStatusCode(); action = (await response.Content.ReadFromJsonAsync<PendingActionDto>())!;
            var turn = new ChatTurn { Id = Guid.NewGuid(), ConversationId = id, Input = "Checkout", Status = "Completed", CreatedAt = fixture.Factory.Clock.GetUtcNow() };
            db.ChatTurns.Add(turn);
            var card = ChatPolicy.Project("prepare_order", new(false, Data(action)), id, turn.Id, Guid.NewGuid(), fixture.Factory.Clock.GetUtcNow());
            cardId = card.Id; db.ChatCards.Add(card); await db.SaveChangesAsync();
        }
        var path = $"/api/chat/conversations/{id}/cards/{cardId}/decision";
        var decision = new ChatDecision(Guid.NewGuid(), "approve", action.SnapshotHash);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, decision)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(other, HttpMethod.Post, path, decision)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, path, decision with { SnapshotHash = "tampered" })).StatusCode);
        // Simulate domain commit followed by a dropped response before any chat projection exists.
        (await MutateAsync(client, HttpMethod.Post, $"/api/actions/{action.Id}/confirm", new { action.SnapshotHash })).EnsureSuccessStatusCode();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync($"/api/chat/conversations/{id}")));
        (await MutateAsync(client, HttpMethod.Post, path, decision)).EnsureSuccessStatusCode();
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        Assert.Single(read.GetProperty("cards").EnumerateArray(), c => c.GetProperty("kind").GetString() == "order");
        Assert.Single((await client.GetFromJsonAsync<OrderDto[]>("/api/orders"))!);
        Assert.Equal("Completed", read.GetProperty("cards")[0].GetProperty("payload").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Imported_reviews_stay_in_chat_and_rejection_changes_no_order_or_bag()
    {
        var (client, _) = await fixture.SignedInAsync(); using var shopper = client;
        var (other, _) = await fixture.SignedInAsync(); using var outsider = other;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var product = await db.Products.FirstAsync(p => p.Stock > 2);
        (await MutateAsync(client, HttpMethod.Post, "/api/cart/items", new { productId = product.Id, quantity = 1 })).EnsureSuccessStatusCode();
        (await MutateAsync(client, HttpMethod.Post, "/api/orders/prepare", new { paymentMode = "success" })).EnsureSuccessStatusCode();
        var path = $"/api/chat/conversations/{id}/reviews";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await MutateAsync(other, HttpMethod.Post, path, new { })).StatusCode);
        var imported = await MutateAsync(client, HttpMethod.Post, path, new { }); imported.EnsureSuccessStatusCode();
        var history = await imported.Content.ReadFromJsonAsync<JsonElement>();
        var card = Assert.Single(history.GetProperty("cards").EnumerateArray());
        var response = await MutateAsync(client, HttpMethod.Post, $"/api/chat/conversations/{id}/cards/{card.GetProperty("cardId").GetGuid()}/decision",
            new ChatDecision(Guid.NewGuid(), "reject", card.GetProperty("payload").GetProperty("snapshotHash").GetString()!));
        response.EnsureSuccessStatusCode();
        Assert.Contains("Rejected", await response.Content.ReadAsStringAsync());
        Assert.Empty((await client.GetFromJsonAsync<OrderDto[]>("/api/orders"))!);
        Assert.Equal(1, (await client.GetFromJsonAsync<CartDto>("/api/cart"))!.ItemCount);
    }

    [Fact]
    public void Compaction_keeps_ordered_real_references_and_complete_tool_pairs()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var cards = new[] { new ChatCard { Kind = "products", PayloadJson = Data(new { items = new[] {
            new { id = first, name = "First" }, new { id = second, name = "Second" } } }).GetRawText() } };
        var memory = ChatMemory.Summarize(cards);
        Assert.True(memory.IndexOf(first.ToString(), StringComparison.Ordinal) < memory.IndexOf(second.ToString(), StringComparison.Ordinal));
        var context = ChatMemory.Context("Store instructions", memory,
            [[new("user", new string('x', 22000)), new("assistant", "Old reply")],
             [new("user", "Find products"), new("assistant", "", [new(new("search_products", Data(new { })))]), new("tool", "actual results", ToolName: "search_products")]],
            [new("user", "The second one")], []);
        Assert.DoesNotContain(context, m => m.Content == "Old reply");
        Assert.Contains(context, m => m.ToolCalls?.Count == 1);
        Assert.Contains(context, m => m.Role == "tool" && m.Content == "actual results");
        Assert.Contains(context, m => m.Content.Contains(second.ToString()));
        var pendingMemory = ChatMemory.Summarize([new() { Kind = "approval", PayloadJson = Data(new { id = Guid.NewGuid(), status = "Pending" }).GetRawText() }]);
        Assert.Contains("Pending", pendingMemory);
    }

    [Fact]
    public async Task Oversized_input_and_reused_request_id_with_changed_text_are_rejected()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        var path = $"/api/chat/conversations/{id}/messages";
        Assert.Equal(HttpStatusCode.BadRequest, (await MutateAsync(client, HttpMethod.Post, path, new ChatInput(Guid.NewGuid(), new string('x', 2001)))).StatusCode);
        var csrf = await client.GetFromJsonAsync<Csrf>("/api/auth/csrf");
        using var oversized = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(JsonSerializer.Serialize(new ChatInput(Guid.NewGuid(), new string('x', 17000)), OllamaChatModel.Json), System.Text.Encoding.UTF8, "application/json") };
        oversized.Headers.Add("X-CSRF-Token", csrf!.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var chat = Service(scope.ServiceProvider, new FakeModel(Say("Hello")), new());
        var input = new ChatInput(Guid.NewGuid(), "Hello"); var turn = await chat.BeginAsync(user.Id, id, input, default);
        await chat.RunAsync(user.Id, turn!, _ => Task.CompletedTask, default);
        Assert.Equal(HttpStatusCode.Conflict, (await MutateAsync(client, HttpMethod.Post, path, input with { Message = "Changed" })).StatusCode);
    }

    [Fact]
    public async Task Site_sources_persist_but_are_retrieved_fresh_for_followups_and_never_become_entity_references()
    {
        var (client, user) = await fixture.SignedInAsync(); using var shopper = client;
        var id = await Create(client);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var model = new FakeModel(Say("Address books are unavailable."), Say("There is no Addresses page."));
        var knowledge = new RecordingKnowledge();
        var chat = ActivatorUtilities.CreateInstance<ChatService>(scope.ServiceProvider, model, new FakeTools(), knowledge);
        var first = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Do I have saved addresses?"), default);
        await chat.RunAsync(user.Id, first!, _ => Task.CompletedTask, default);
        knowledge.Body = "Fresh guidance: no Addresses page.";
        var followup = await chat.BeginAsync(user.Id, id, new(Guid.NewGuid(), "Which page?"), default);
        await chat.RunAsync(user.Id, followup!, _ => Task.CompletedTask, default);
        Assert.Contains("Do I have saved addresses?", knowledge.Queries.Last());
        Assert.Contains("Which page?", knowledge.Queries.Last());
        Assert.Contains(model.Contexts.Last(), m => m.Role == "user" && m.Content.Contains(knowledge.Body));
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var cards = await db.ChatCards.Where(c => c.ConversationId == id).ToListAsync();
        Assert.Equal(2, cards.Count); Assert.All(cards, c => Assert.Equal("knowledge", c.Kind));
        Assert.DoesNotContain("Fresh guidance", ChatMemory.Summarize(cards));
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        Assert.All(read.GetProperty("turns").EnumerateArray(), t => Assert.Equal("Completed", t.GetProperty("status").GetString()));
        Assert.Equal(2, read.GetProperty("cards").GetArrayLength());
    }

    private sealed class RecordingKnowledge : Commerce.Knowledge.IKnowledgeRetriever
    {
        public string Body { get; set; } = "Saved address books are unavailable.";
        public List<string> Queries { get; } = [];
        public Task<Commerce.Knowledge.KnowledgeResult> SearchAsync(string query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(new Commerce.Knowledge.KnowledgeResult("available", Guid.NewGuid(),
                [new("addresses", "Addresses", Body, [new("Orders", "/orders")], DateTimeOffset.UtcNow)]));
        }
    }

    private sealed class FakeModel(params ModelReply[] replies) : IChatModel
    {
        public Queue<ModelReply> Replies { get; } = new(replies);
        public List<ModelMessage[]> Contexts { get; } = [];
        public int Calls => Contexts.Count;
        public Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelTool> tools, CancellationToken cancellationToken = default)
        { Contexts.Add(messages.ToArray()); return Task.FromResult(Replies.Dequeue()); }
    }
    private sealed class FakeTools : IChatToolFactory, IChatToolSession
    {
        public ChatToolResult Result { get; set; } = new(false, Data(new { }));
        public bool Fail { get; set; }
        public List<ModelFunction> Executed { get; } = [];
        public IReadOnlyList<ModelTool> Tools { get; } = [
            new("search_products", "Search", Data(new { type = "object", properties = new { } })),
            new("get_product", "Read", Data(new { type = "object", properties = new { productId = new { type = "string", format = "uuid" } }, required = new[] { "productId" } })),
            new("prepare_order", "Prepare", Data(new { type = "object", properties = new { paymentMode = new { type = "string" } }, required = new[] { "paymentMode" } }))];
        public Task<IChatToolSession> OpenAsync(CommerceUser user, CancellationToken ct) => Task.FromResult<IChatToolSession>(this);
        public Task<ChatToolResult> CallAsync(ModelFunction call, CancellationToken ct)
        { Executed.Add(call); if (Fail) throw new HttpRequestException("Connection lost after submission"); return Task.FromResult(Result); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
