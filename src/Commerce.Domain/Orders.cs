namespace Commerce.Domain;

public sealed class PendingAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string UserId { get; init; }
    public required string Kind { get; init; }
    public required string SnapshotJson { get; init; }
    public required string SnapshotHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Status { get; set; } = "Pending";
    public Guid? OrderId { get; set; }
}

public sealed class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string UserId { get; init; }
    public required string SnapshotJson { get; init; }
    public decimal Total { get; init; }
    public string Currency { get; init; } = "USD";
    public string Status { get; set; } = "AwaitingPayment";
    public string PaymentStatus { get; set; } = "Pending";
    public decimal CapturedAmount { get; set; }
    public decimal RefundedAmount { get; set; }
    public bool StockReleased { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextTransitionAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public List<OrderEvent> Events { get; set; } = [];
}

public sealed class OrderEvent
{
    public long Id { get; set; }
    public Guid OrderId { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
}
