using System.ComponentModel;
using System.Security.Claims;
using Commerce.Application;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Server;

namespace Commerce.Mcp;

[McpServerToolType]
public sealed class CartTools
{
    [Authorize(Policy = McpAccessTokens.CartRead)]
    [McpServerTool(Name = "get_cart", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the signed-in shopper's current bag, quantities, current prices and availability. Does not change the bag or reserve stock.")]
    public static Task<CartDto> GetCart(ClaimsPrincipal user, [FromServices] ICartService carts,
        CancellationToken cancellationToken)
    {
        // Identity comes from the verified HTTP credential, never from model/tool arguments.
        var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated shopper is missing.");
        return carts.GetAsync(owner, cancellationToken);
    }
}
