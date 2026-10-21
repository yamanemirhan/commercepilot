using System.Text.Json.Serialization;

namespace Commerce.Application;

public sealed record ProductDto(Guid Id, string Slug, string Name, string Description,
    string Category, decimal Price, string Currency, int Stock, string Artwork, string Color,
    // MCP declares this constructor property required but nullable; keep it in the result even when null.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Badge);

public sealed record CatalogQuery(string? Search = null, string? Category = null,
    decimal? MinPrice = null, decimal? MaxPrice = null, string Sort = "featured",
    int Page = 1, int PageSize = 12, bool InStock = false)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Search?.Length > 100) errors["search"] = ["Search must be 100 characters or fewer."];
        if (Category is not null && !CatalogCategories.All.Contains(Category))
            errors["category"] = ["Choose a supported category."];
        if (MinPrice < 0 || MaxPrice < 0 || (MinPrice.HasValue && MaxPrice.HasValue && MinPrice > MaxPrice))
            errors["price"] = ["Prices must be non-negative and the minimum cannot exceed the maximum."];
        if (Sort is not ("featured" or "price-asc" or "price-desc" or "name"))
            errors["sort"] = ["Choose featured, price-asc, price-desc, or name."];
        if (Page is < 1 or > 10000) errors["page"] = ["Page must be between 1 and 10000."];
        if (PageSize is < 1 or > 48) errors["pageSize"] = ["Page size must be between 1 and 48."];
        return errors;
    }
}

public static class CatalogCategories
{
    public static readonly string[] All = ["Audio", "Workspace", "Everyday", "Gaming"];
}

public sealed record CatalogPage(IReadOnlyList<ProductDto> Items, int Total, int Page, int PageSize);
public sealed record CategoryDto(string Name, int Count);

public interface ICatalogService
{
    Task<CatalogPage> SearchAsync(CatalogQuery query, CancellationToken cancellationToken);
    Task<ProductDto?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken cancellationToken);
}
