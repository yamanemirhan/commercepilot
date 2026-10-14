using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commerce.Application;
using Commerce.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Infrastructure;

public sealed class OrderService(CommerceDbContext db, TimeProvider clock) : IOrderService
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private const string DemoDestination = "Demo address · Portland, OR · United States";
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private static ActionSnapshot Snapshot(string json) => JsonSerializer.Deserialize<ActionSnapshot>(json)!;
    private static bool CanCancel(Order order) => order.Status is "AwaitingPayment" or "Processing";
    private bool CanRefund(Order order) => order.Status == "Delivered" && order.PaymentStatus == "Captured"
        && order.DeliveredAt >= Now.AddDays(-30);

    // Shared with cart mutations: serialize a shopper's changes before acquiring product locks.
    // Product locks always use the same ordering, including stock release and background transitions.
    private async Task LockOwner(string userId, CancellationToken ct) =>
        _ = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);

    private async Task<Dictionary<Guid, Product>> LockProducts(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var products = new Dictionary<Guid, Product>();
        foreach (var id in ids.Distinct().Order())
        {
            var rows = await db.Products.FromSqlInterpolated($"SELECT * FROM \"Products\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
            if (rows.Count > 0) products.Add(id, rows[0]);
        }
        return products;
    }

    private PendingActionDto ActionDto(PendingAction a) => new(a.Id, a.Kind,
        a.Status == "Pending" && a.ExpiresAt <= Now ? "Expired" : a.Status,
        a.SnapshotHash, a.CreatedAt, a.ExpiresAt, Snapshot(a.SnapshotJson), a.OrderId);

    private OrderDto Dto(Order o)
    {
        var snapshot = Snapshot(o.SnapshotJson);
        return new(o.Id, $"CP-{o.Id.ToString("N")[..8].ToUpperInvariant()}", o.Status, o.PaymentStatus,
            o.Total, o.Currency, o.CapturedAmount, o.RefundedAmount, o.CreatedAt, o.NextTransitionAt,
            snapshot.Destination, snapshot.Items,
            o.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new OrderEventDto(e.At, e.Title, e.Detail)).ToList(),
            CanCancel(o), CanRefund(o));
    }

    private async Task<PendingAction> NewAction(string userId, ActionSnapshot snapshot, CancellationToken ct)
    {
        // A second review of the same operation supersedes its older, still-pending review.
        var old = await db.PendingActions.Where(a => a.UserId == userId && a.Kind == snapshot.Kind
            && a.OrderId == snapshot.OrderId && a.Status == "Pending").ToListAsync(ct);
        foreach (var action in old) action.Status = "Superseded";
        var json = JsonSerializer.Serialize(snapshot);
        var created = Now;
        var result = new PendingAction { UserId = userId, Kind = snapshot.Kind, SnapshotJson = json,
            SnapshotHash = Hash(json), CreatedAt = created, ExpiresAt = created.AddMinutes(5), OrderId = snapshot.OrderId };
        db.PendingActions.Add(result);
        return result;
    }

    public async Task<PendingActionDto> PrepareOrderAsync(string userId, string paymentMode, CancellationToken ct)
    {
        if (paymentMode is not ("success" or "failure" or "delay"))
            throw new OrderFault(400, "Choose a supported demo payment outcome.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(userId, ct);
        var cart = await db.CartItems.Where(i => i.UserId == userId).OrderBy(i => i.ProductId).ToListAsync(ct);
        if (cart.Count == 0) throw new OrderFault(409, "Your bag is empty. Add an item before reviewing an order.");
        var products = await LockProducts(cart.Select(i => i.ProductId), ct);
        var lines = new List<OrderLine>();
        foreach (var item in cart)
        {
            if (!products.TryGetValue(item.ProductId, out var p) || p.Stock < item.Quantity || p.Currency != "USD")
                throw new OrderFault(409, "An item is unavailable. Refresh and update your bag.");
            lines.Add(new(p.Id, p.Name, p.Artwork, p.Color, item.Quantity, p.Price, p.Stock));
        }
        var action = await NewAction(userId, new("Checkout", lines, lines.Sum(i => i.UnitPrice * i.Quantity),
            "USD", paymentMode, DemoDestination), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ActionDto(action);
    }

    public async Task<PendingActionDto> PrepareChangeAsync(string userId, Guid orderId, string kind, CancellationToken ct)
    {
        if (kind is not ("Cancellation" or "Refund")) throw new OrderFault(400, "Unknown action.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(userId, ct);
        var order = await OwnedOrder(userId, orderId, ct);
        if (kind == "Cancellation" ? !CanCancel(order) : !CanRefund(order))
            throw new OrderFault(409, kind == "Cancellation" ? "This order can no longer be cancelled." : "This order is not eligible for a refund.");
        var original = Snapshot(order.SnapshotJson);
        var action = await NewAction(userId, original with { Kind = kind, OrderId = order.Id, OrderVersion = order.Version }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ActionDto(action);
    }

    public async Task<PendingActionDto> GetActionAsync(string userId, Guid id, CancellationToken ct) =>
        ActionDto(await OwnedAction(userId, id, ct));

    public async Task<IReadOnlyList<PendingActionDto>> PendingAsync(string userId, CancellationToken ct) =>
        (await db.PendingActions.AsNoTracking().Where(a => a.UserId == userId && a.Status == "Pending" && a.ExpiresAt > Now)
            .OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct)).Select(ActionDto).ToList();

    private async Task<PendingAction> OwnedAction(string userId, Guid id, CancellationToken ct) =>
        await db.PendingActions.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct)
        ?? throw new OrderFault(404, "Approval not found.");

    private async Task<Order> OwnedOrder(string userId, Guid id, CancellationToken ct) =>
        await db.Orders.Include(o => o.Events).SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, ct)
        ?? throw new OrderFault(404, "Order not found.");

    public async Task<PendingActionDto> RejectAsync(string userId, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(userId, ct);
        var action = await OwnedAction(userId, id, ct);
        if (action.Status == "Completed") throw new OrderFault(409, "This approval has already been completed.");
        if (action.Status == "Pending") action.Status = action.ExpiresAt <= Now ? "Expired" : "Rejected";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ActionDto(action);
    }

    public async Task<PendingActionDto> ConfirmAsync(string userId, Guid id, string snapshotHash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(userId, ct);
        var action = await OwnedAction(userId, id, ct);
        if (action.SnapshotHash != snapshotHash || Hash(action.SnapshotJson) != action.SnapshotHash)
            throw new OrderFault(409, "The review does not match this approval. Open a fresh review.");
        // Returning the original result is safe even after the TTL; never repeat effects.
        if (action.Status == "Completed") return ActionDto(action);
        if (action.Status != "Pending") throw new OrderFault(409, $"This approval is {action.Status.ToLowerInvariant()}. Open a fresh review.");
        if (action.ExpiresAt <= Now)
        {
            action.Status = "Expired";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new OrderFault(409, "This review expired. Review the latest details before approving.");
        }
        var snapshot = Snapshot(action.SnapshotJson);
        if (action.Kind == "Checkout")
        {
            var cart = await db.CartItems.Where(i => i.UserId == userId).ToListAsync(ct);
            var products = await LockProducts(snapshot.Items.Select(i => i.ProductId), ct);
            var stale = cart.Count != snapshot.Items.Count || snapshot.Items.Any(i =>
                !cart.Any(c => c.ProductId == i.ProductId && c.Quantity == i.Quantity) ||
                !products.TryGetValue(i.ProductId, out var p) || p.Price != i.UnitPrice || p.Currency != snapshot.Currency ||
                p.Stock != i.AvailableStock || p.Stock < i.Quantity);
            if (stale)
            {
                action.Status = "Stale";
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new OrderFault(409, "Your bag, prices or stock changed. Review the updated order before approving.");
            }
            var order = new Order { UserId = userId, SnapshotJson = action.SnapshotJson, Total = snapshot.Total,
                Currency = snapshot.Currency, CreatedAt = Now };
            db.Orders.Add(order);
            foreach (var item in snapshot.Items) products[item.ProductId].Stock -= item.Quantity;
            Event(order, "Order approved", "You approved this order. Inventory reserved; all payments and shipping are simulated.");
            if (snapshot.PaymentMode == "success") Capture(order);
            else if (snapshot.PaymentMode == "failure")
            {
                order.Status = "PaymentFailed";
                order.PaymentStatus = "Failed";
                Release(order, products);
                Event(order, "Payment declined", "Simulated payment failed. Inventory released; your bag is kept for a new review.");
            }
            else
            {
                order.NextTransitionAt = Now.AddSeconds(20);
                Event(order, "Payment pending", "Demo payment will complete in about 20 seconds. You may cancel while waiting.");
            }
            if (snapshot.PaymentMode != "failure") db.CartItems.RemoveRange(cart);
            action.OrderId = order.Id;
        }
        else
        {
            var order = await OwnedOrder(userId, snapshot.OrderId!.Value, ct);
            if (order.Version != snapshot.OrderVersion || (action.Kind == "Cancellation" ? !CanCancel(order) : !CanRefund(order)))
            {
                action.Status = "Stale";
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new OrderFault(409, "The order changed. Refresh its status and review the action again.");
            }
            if (action.Kind == "Cancellation")
            {
                var products = await LockProducts(snapshot.Items.Select(i => i.ProductId), ct);
                Release(order, products);
                order.Status = "Cancelled";
                order.PaymentStatus = order.CapturedAmount > 0 ? "Refunded" : "Voided";
                order.RefundedAmount = order.CapturedAmount;
                Event(order, "Order cancelled", "Approved cancellation. Reserved inventory released; any captured demo payment refunded.");
            }
            else
            {
                order.Status = "Refunded";
                order.PaymentStatus = "Refunded";
                order.RefundedAmount = order.CapturedAmount;
                Event(order, "Refund completed", "Approved full demo refund. Delivered goods are not automatically restocked.");
            }
            order.NextTransitionAt = null;
            order.Version++;
        }
        action.Status = "Completed";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ActionDto(action);
    }

    private void Event(Order order, string title, string detail) =>
        order.Events.Add(new() { OrderId = order.Id, At = Now, Title = title, Detail = detail });

    private void Capture(Order order)
    {
        order.PaymentStatus = "Captured";
        order.CapturedAmount = order.Total;
        order.Status = "Processing";
        order.NextTransitionAt = Now.AddMinutes(2);
        Event(order, "Payment confirmed", "Demo payment captured. Your order is being prepared; simulated dispatch in about 2 minutes.");
    }

    private static void Release(Order order, Dictionary<Guid, Product> products)
    {
        if (order.StockReleased) return;
        foreach (var item in Snapshot(order.SnapshotJson).Items) products[item.ProductId].Stock += item.Quantity;
        order.StockReleased = true;
    }

    public async Task<IReadOnlyList<OrderDto>> ListAsync(string userId, CancellationToken ct) =>
        (await db.Orders.AsNoTracking().Include(o => o.Events).Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync(ct)).Select(Dto).ToList();

    public async Task<OrderDto> GetAsync(string userId, Guid id, CancellationToken ct) => Dto(await OwnedOrder(userId, id, ct));

    // Durable deadlines survive restarts. The scheduler never approves actions; it only advances approved orders.
    public async Task AdvanceAsync(string userId, Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwner(userId, ct);
        var order = await OwnedOrder(userId, orderId, ct);
        if (order.NextTransitionAt is null || order.NextTransitionAt > Now) return;
        switch (order.Status)
        {
            case "AwaitingPayment": Capture(order); break;
            case "Processing":
                order.Status = "Shipped";
                order.NextTransitionAt = Now.AddMinutes(2);
                Event(order, "On its way", "Simulated shipment dispatched. Demo delivery in about 2 minutes; cancellation is now closed.");
                break;
            case "Shipped":
                order.Status = "Delivered";
                order.DeliveredAt = Now;
                order.NextTransitionAt = null;
                Event(order, "Delivered", "Simulated delivery completed at the demo address. A full demo refund is available for 30 days.");
                break;
            default: order.NextTransitionAt = null; break;
        }
        order.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
