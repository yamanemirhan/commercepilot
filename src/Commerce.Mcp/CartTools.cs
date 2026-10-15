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
public sealed class CartTools
{
    [Authorize(Policy = McpAccessTokens.CartWrite)]
    [McpServerTool(Name = "update_cart_item", ReadOnly = false, Destructive = true,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Set an existing item in the signed-in shopper's bag to an exact total quantity. This replaces the quantity; it does not add units. Use remove_from_cart to remove an item. Prices and availability are checked again. Does not reserve stock, create an order or charge money.")]
    public static async Task<CartDto> UpdateCartItem(
        [Description("The exact product UUID from the shopper's get_cart result.")] Guid productId,
        [Description("The desired total quantity, from 1 to 99 and no more than current stock. Zero is invalid; use remove_from_cart instead.")] int quantity,
        ClaimsPrincipal user, [FromServices] ICartService carts, CancellationToken cancellationToken)
    {
        var result = await carts.SetQuantityAsync(Owner(user), productId, quantity, cancellationToken);
        if (result.Error is not null) throw new McpException($"{result.Error}: {result.Message}");
        return result.Cart ?? throw new InvalidOperationException("The cart service returned no result.");
    }

    [Authorize(Policy = McpAccessTokens.CartWrite)]
    [McpServerTool(Name = "remove_from_cart", ReadOnly = false, Destructive = true,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Remove a product's entire line from the signed-in shopper's bag and return the remaining bag. If already absent, return the unchanged bag. Does not cancel orders, change inventory or refund payments.")]
    public static Task<CartDto> RemoveFromCart(
        [Description("The exact product UUID from the shopper's get_cart result.")] Guid productId,
        ClaimsPrincipal user, [FromServices] ICartService carts, CancellationToken cancellationToken) =>
        carts.RemoveAsync(Owner(user), productId, cancellationToken);

    [Authorize(Policy = McpAccessTokens.CartWrite)]
    [McpServerTool(Name = "add_to_cart", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Add units of a real product to the signed-in shopper's bag and return the updated bag. Quantity is added to any existing quantity. Prices come from the database. Does not reserve stock, create an order or charge money. Repeating a successful call adds again; after an uncertain result, read get_cart before deciding what to do.")]
    public static async Task<CartDto> AddToCart(
        [Description("The exact product UUID returned by search_products or get_product.")] Guid productId,
        [Description("Units to add, from 1 to 99. The resulting quantity must not exceed 99 or current stock.")] int quantity,
        ClaimsPrincipal user, [FromServices] ICartService carts, CancellationToken cancellationToken)
    {
        var result = await carts.AddAsync(Owner(user), productId, quantity, cancellationToken);
        if (result.Error is not null) throw new McpException($"{result.Error}: {result.Message}");
        return result.Cart ?? throw new InvalidOperationException("The cart service returned no result.");
    }

    [Authorize(Policy = McpAccessTokens.CartRead)]
    [McpServerTool(Name = "get_cart", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the signed-in shopper's current bag, quantities, current prices and availability. Does not change the bag or reserve stock.")]
    public static Task<CartDto> GetCart(ClaimsPrincipal user, [FromServices] ICartService carts,
        CancellationToken cancellationToken)
    {
        // Identity comes from the verified HTTP credential, never from model/tool arguments.
        return carts.GetAsync(Owner(user), cancellationToken);
    }

    private static string Owner(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated shopper is missing.");
}
