using System.ComponentModel;
using System.Security.Claims;
using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Commerce.Mcp;

[McpServerToolType]
[Authorize(Policy = McpAccessTokens.OrdersRead)]
public sealed class OrderTools
{
    [McpServerTool(Name = "get_my_orders", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List summaries of the signed-in shopper's most recent 100 orders, newest first. Includes real order IDs, order status, payment status and totals. An empty list means this account has no orders. Does not create orders or advance payment/shipping.")]
    public static async Task<MyOrdersResult> GetMyOrders(ClaimsPrincipal user,
        [FromServices] IOrderService orders, CancellationToken cancellationToken)
    {
        var owned = await orders.ListAsync(Owner(user), cancellationToken);
        return new(owned.Select(order => new OrderSummary(order.Id, order.Number, order.Status,
            order.PaymentStatus, order.Total, order.Currency, order.CreatedAt)).ToArray(), 100);
    }

    [McpServerTool(Name = "get_order_status", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read one order belonging to the signed-in shopper, including its separate order/payment states, items, timeline and cancellation/refund eligibility. Use the order UUID from get_my_orders, not the display number. Eligibility is information, not approval. Does not change the order, advance shipping or charge/refund money.")]
    public static async Task<OrderDto> GetOrderStatus(
        [Description("The exact order UUID from get_my_orders. Not the CP- display number.")] Guid orderId,
        ClaimsPrincipal user, [FromServices] IOrderService orders, CancellationToken cancellationToken)
    {
        try { return await orders.GetAsync(Owner(user), orderId, cancellationToken); }
        catch (OrderFault fault) when (fault.Status == 404)
        {
            // Missing and another shopper's orders must be indistinguishable.
            throw new McpException("OrderNotFound: No order was found for this account.");
        }
    }

    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated shopper is missing.");
}

public sealed record OrderSummary(Guid Id, string Number, string Status, string PaymentStatus,
    decimal Total, string Currency, DateTimeOffset CreatedAt);
public sealed record MyOrdersResult(IReadOnlyList<OrderSummary> Orders, int Limit);
