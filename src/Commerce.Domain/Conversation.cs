namespace Commerce.Domain;

public sealed class Conversation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ActiveTurnId { get; set; }
    public DateTimeOffset? ActiveUntil { get; set; }
}

public sealed class ChatTurn
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Input { get; set; } = "";
    public string Status { get; set; } = "Running";
    public DateTimeOffset CreatedAt { get; set; }
    public string MessagesJson { get; set; } = "[]";
}

public sealed class ChatEvent
{
    public long Sequence { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public string Kind { get; set; } = "";
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChatCard
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public Guid ToolCallId { get; set; }
    public string Kind { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset ObservedAt { get; set; }
}
