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
public sealed class ApprovalTools
{
    [Authorize(Policy = McpAccessTokens.OrdersCancelPrepare)]
    [McpServerTool(Name = "prepare_cancellation", ReadOnly = false, Destructive = true,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Prepare a five-minute cancellation review for one of the shopper's orders before dispatch. Does not cancel, refund, void payment or release stock. Supersedes an earlier pending cancellation review for the same order; never retry automatically. Present the exact returned terms for an explicit human button approval. Order eligibility is rechecked when approved.")]
    public static Task<PendingActionDto> PrepareCancellation(
        [Description("The exact owned order UUID from get_my_orders, not the CP- display number.")] Guid orderId,
        ClaimsPrincipal user, [FromServices] IOrderService orders, CancellationToken cancellationToken) =>
        PrepareChange(user, orders, orderId, "Cancellation", cancellationToken);

    [Authorize(Policy = McpAccessTokens.OrdersRefundPrepare)]
    [McpServerTool(Name = "prepare_refund", ReadOnly = false, Destructive = true,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Prepare a five-minute full-refund review for the shopper's delivered order with captured payment, within 30 days of delivery. Does not refund or restock. Partial refunds and returns are unsupported. Supersedes an earlier pending refund review for this order; never retry automatically. Present returned terms for explicit human button approval; eligibility is checked again on approval.")]
    public static Task<PendingActionDto> PrepareRefund(
        [Description("The exact owned order UUID from get_my_orders, not the CP- display number.")] Guid orderId,
        ClaimsPrincipal user, [FromServices] IOrderService orders, CancellationToken cancellationToken) =>
        PrepareChange(user, orders, orderId, "Refund", cancellationToken);

    private static async Task<PendingActionDto> PrepareChange(ClaimsPrincipal user, IOrderService orders,
        Guid orderId, string kind, CancellationToken cancellationToken)
    {
        try { return await orders.PrepareChangeAsync(Owner(user), orderId, kind, cancellationToken); }
        catch (OrderFault fault) when (fault.Status == 404)
        {
            throw new McpException("OrderNotFound: No order was found for this account.");
        }
        catch (OrderFault fault) when (fault.Status is 400 or 409)
        {
            throw new McpException($"OrderPreparationFailed: {fault.Message}");
        }
    }

    [Authorize(Policy = McpAccessTokens.OrdersPrepare)]
    [McpServerTool(Name = "prepare_order", ReadOnly = false, Destructive = true,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Prepare a five-minute checkout review from the signed-in shopper's current bag and server prices. Does NOT place an order, reserve stock, charge money or approve anything. Replaces any previous pending checkout review for this shopper; do not retry automatically. Present the returned terms in the application's trusted review UI for explicit human button approval; a chat message alone is not approval. Returns the review ID, expiry, snapshot and status; use get_pending_action_status to read it.")]
    public static async Task<PendingActionDto> PrepareOrder(
        [Description("Local demo payment outcome after human approval: success, failure or delay. No real payment occurs.")] string paymentMode,
        ClaimsPrincipal user, [FromServices] IOrderService orders, CancellationToken cancellationToken)
    {
        try { return await orders.PrepareOrderAsync(Owner(user), paymentMode, cancellationToken); }
        catch (OrderFault fault) when (fault.Status is 400 or 409)
        {
            throw new McpException($"OrderPreparationFailed: {fault.Message}");
        }
    }

    [Authorize(Policy = McpAccessTokens.ActionsRead)]
    [McpServerTool(Name = "get_pending_action_status", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a review belonging to the signed-in shopper using its exact action UUID. Returns the immutable terms, expiry, current review status and resulting order ID if completed. Also reads expired, superseded, rejected or completed reviews. Never approves, rejects, creates an order or advances a payment. Pending means human approval is still required in the storefront; Completed means the review executed, not necessarily that payment succeeded.")]
    public static async Task<PendingActionDto> GetPendingActionStatus(
        [Description("The exact review UUID returned by prepare_order or the storefront review API. Not an order UUID.")] Guid actionId,
        ClaimsPrincipal user, [FromServices] IOrderService orders, CancellationToken cancellationToken)
    {
        try { return await orders.GetActionAsync(Owner(user), actionId, cancellationToken); }
        catch (OrderFault fault) when (fault.Status == 404)
        {
            throw new McpException("ActionNotFound: No review was found for this account.");
        }
    }

    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated shopper is missing.");
}
