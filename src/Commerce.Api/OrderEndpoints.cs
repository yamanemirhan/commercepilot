using System.Security.Claims;
using Commerce.Application;

namespace Commerce.Api;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Orders").RequireAuthorization().AddEndpointFilter<OrderFaultFilter>();
        group.MapGet("/orders", async (ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Owner(user), ct)));
        group.MapGet("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(Owner(user), id, ct)));
        group.MapGet("/actions", async (ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.PendingAsync(Owner(user), ct)));
        group.MapGet("/actions/{id:guid}", async (Guid id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetActionAsync(Owner(user), id, ct)));
        group.MapPost("/orders/prepare", async (PrepareOrderRequest request, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.PrepareOrderAsync(Owner(user), request.PaymentMode, ct))).AddEndpointFilter<CsrfFilter>();
        group.MapPost("/orders/{id:guid}/prepare-cancellation", async (Guid id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.PrepareChangeAsync(Owner(user), id, "Cancellation", ct))).AddEndpointFilter<CsrfFilter>();
        group.MapPost("/orders/{id:guid}/prepare-refund", async (Guid id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.PrepareChangeAsync(Owner(user), id, "Refund", ct))).AddEndpointFilter<CsrfFilter>();
        // These cookie + CSRF endpoints are exclusively for human review. They must never become MCP tools.
        group.MapPost("/actions/{id:guid}/confirm", async (Guid id, ConfirmActionRequest request, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ConfirmAsync(Owner(user), id, request.SnapshotHash, ct))).AddEndpointFilter<CsrfFilter>();
        group.MapPost("/actions/{id:guid}/reject", async (Guid id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.RejectAsync(Owner(user), id, ct))).AddEndpointFilter<CsrfFilter>();
    }

    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}

public sealed record PrepareOrderRequest(string PaymentMode);
public sealed record ConfirmActionRequest(string SnapshotHash);

public sealed class OrderFaultFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (OrderFault fault) { return Results.Problem(fault.Message, statusCode: fault.Status); }
    }
}
