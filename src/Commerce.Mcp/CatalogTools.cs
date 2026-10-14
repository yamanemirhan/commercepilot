using System.ComponentModel;
using Commerce.Application;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Commerce.Mcp;

[McpServerToolType]
public sealed class CatalogTools
{
    [McpServerTool(Name = "get_product", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a public product by its real ID from search_products. Returns current price and stock. Does not reserve stock.")]
    public static async Task<ProductDto> GetProduct(
        [Description("The exact product UUID returned by search_products.")] Guid productId,
        [FromServices] ICatalogService catalog, CancellationToken cancellationToken) =>
        await catalog.FindAsync(productId, cancellationToken)
            ?? throw new McpException("ProductNotFound: This product no longer exists.");

    [McpServerTool(Name = "search_products", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search the public CommercePilot catalog. Returns real product IDs, prices in USD, current stock and pagination. Does not change a bag, reserve stock or place an order.")]
    public static async Task<CatalogPage> SearchProducts(
        [FromServices] ICatalogService catalog,
        CancellationToken cancellationToken,
        [Description("Literal text to find in product names or descriptions. Maximum 100 characters.")] string? search = null,
        [Description("One of Audio, Workspace, Everyday or Gaming. Omit for all categories.")] string? category = null,
        [Description("Minimum unit price in USD, at least zero.")] decimal? minPrice = null,
        [Description("Maximum unit price in USD, at least the minimum price.")] decimal? maxPrice = null,
        [Description("featured, price-asc, price-desc or name.")] string sort = "featured",
        [Description("Page number, from 1 to 10000.")] int page = 1,
        [Description("Products per page, from 1 to 48.")] int pageSize = 12,
        [Description("Return only products with stock above zero.")] bool inStock = false)
    {
        var query = new CatalogQuery(search, category, minPrice, maxPrice, sort, page, pageSize, inStock);
        var errors = query.Validate();
        if (errors.Count > 0)
            throw new McpException(string.Join(" ", errors.Values.SelectMany(messages => messages).Distinct()));

        // Reuse the same query and business rules as the storefront's catalog HTTP endpoint.
        return await catalog.SearchAsync(query, cancellationToken);
    }
}
