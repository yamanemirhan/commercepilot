using System.Linq.Expressions;
using Commerce.Application;
using Commerce.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Infrastructure;

public sealed class CatalogService(CommerceDbContext db) : ICatalogService
{
    private static readonly Expression<Func<Product, ProductDto>> Projection = p => new(
        p.Id, p.Slug, p.Name, p.Description, p.Category, p.Price, p.Currency,
        p.Stock, p.Artwork, p.Color, p.Badge);

    public async Task<CatalogPage> SearchAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Escape LIKE metacharacters so a shopper's search remains literal.
            var search = query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{search}%";
            products = products.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") ||
                EF.Functions.ILike(p.Description, pattern, "\\"));
        }
        if (query.Category is not null) products = products.Where(p => p.Category == query.Category);
        if (query.MinPrice.HasValue) products = products.Where(p => p.Price >= query.MinPrice);
        if (query.MaxPrice.HasValue) products = products.Where(p => p.Price <= query.MaxPrice);
        if (query.InStock) products = products.Where(p => p.Stock > 0);
        var total = await products.CountAsync(cancellationToken);
        var sorted = query.Sort switch
        {
            "price-asc" => products.OrderBy(p => p.Price).ThenBy(p => p.Id),
            "price-desc" => products.OrderByDescending(p => p.Price).ThenBy(p => p.Id),
            "name" => products.OrderBy(p => p.Name).ThenBy(p => p.Id),
            _ => products.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id)
        };
        var items = await sorted.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(Projection).ToListAsync(cancellationToken);
        return new(items, total, query.Page, query.PageSize);
    }

    public Task<ProductDto?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().Where(p => p.Id == id).Select(Projection).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().GroupBy(p => p.Category).OrderBy(g => g.Key)
            .Select(g => new CategoryDto(g.Key, g.Count())).ToListAsync(cancellationToken);
}
