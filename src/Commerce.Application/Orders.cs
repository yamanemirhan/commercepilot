using System.Text.Json.Serialization;

namespace Commerce.Application;

public sealed record OrderLine(Guid ProductId, string Name, string Artwork, string Color,
    int Quantity, decimal UnitPrice, int AvailableStock);
public sealed record ActionSnapshot(string Kind, IReadOnlyList<OrderLine> Items, decimal Total,
    string Currency, string PaymentMode, string Destination, Guid? OrderId = null, int? OrderVersion = null);
public sealed record PendingActionDto(Guid Id, string Kind, string Status, string SnapshotHash,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ActionSnapshot Snapshot, Guid? OrderId);
public sealed record OrderEventDto(DateTimeOffset At, string Title, string Detail);
public sealed record OrderDto(Guid Id, string Number, string Status, string PaymentStatus,
    decimal Total, string Currency, decimal CapturedAmount, decimal RefundedAmount,
    DateTimeOffset CreatedAt,
    // MCP output schemas require this property even when there is no scheduled transition.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? NextTransitionAt,
    string Destination,
    IReadOnlyList<OrderLine> Items, IReadOnlyList<OrderEventDto> Timeline, bool CanCancel, bool CanRefund);

public sealed class OrderFault(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public interface IOrderService
{
    Task<PendingActionDto> PrepareOrderAsync(string userId, string paymentMode, CancellationToken ct);
    Task<PendingActionDto> PrepareChangeAsync(string userId, Guid orderId, string kind, CancellationToken ct);
    Task<PendingActionDto> GetActionAsync(string userId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<PendingActionDto>> PendingAsync(string userId, CancellationToken ct);
    Task<PendingActionDto> ConfirmAsync(string userId, Guid id, string snapshotHash, CancellationToken ct);
    Task<PendingActionDto> RejectAsync(string userId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<OrderDto>> ListAsync(string userId, CancellationToken ct);
    Task<OrderDto> GetAsync(string userId, Guid id, CancellationToken ct);
    Task AdvanceAsync(string userId, Guid orderId, CancellationToken ct);
}
