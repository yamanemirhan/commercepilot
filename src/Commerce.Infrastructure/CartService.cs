using Commerce.Application;
using Commerce.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Infrastructure;

public sealed class CartService(CommerceDbContext db) : ICartService
{
    public async Task<CartDto> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var items = await db.CartItems.AsNoTracking().Where(i => i.UserId == userId)
            .OrderBy(i => i.Product.DisplayOrder).ThenBy(i => i.ProductId)
            .Select(i => new CartItemDto(i.ProductId, i.Product.Name, i.Product.Category,
                i.Product.Artwork, i.Product.Color, i.Quantity, i.Product.Price,
                i.Product.Currency, i.Product.Stock, i.Quantity * i.Product.Price))
            .ToListAsync(cancellationToken);
        return new(items, items.Sum(i => i.Quantity), items.Sum(i => i.LineTotal), "USD");
    }

    public Task<CartResult> AddAsync(string userId, Guid productId, int quantity, CancellationToken cancellationToken) =>
        MutateAsync(userId, productId, quantity, additive: true, cancellationToken);

    public Task<CartResult> SetQuantityAsync(string userId, Guid productId, int quantity, CancellationToken cancellationToken) =>
        MutateAsync(userId, productId, quantity, additive: false, cancellationToken);

    private async Task<CartResult> MutateAsync(string userId, Guid productId, int quantity, bool additive,
        CancellationToken cancellationToken)
    {
        if (quantity is < 1 or > 99)
            return CartResult.Failure(CartError.InvalidQuantity, "Quantity must be between 1 and 99.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockCartOwnerAsync(userId, cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null) return CartResult.Failure(CartError.ProductNotFound, "This product is no longer available.");
        if (product.Currency != "USD") return CartResult.Failure(CartError.UnsupportedCurrency, "This cart only supports USD products.");
        var item = await db.CartItems.SingleOrDefaultAsync(i => i.UserId == userId && i.ProductId == productId, cancellationToken);
        if (!additive && item is null) return CartResult.Failure(CartError.ProductNotFound, "This product is not in your bag.");
        var requested = additive ? (item?.Quantity ?? 0) + quantity : quantity;
        if (requested > 99) return CartResult.Failure(CartError.InvalidQuantity, "You can add up to 99 of each product.");
        if (requested > product.Stock)
            return CartResult.Failure(CartError.InsufficientStock, $"Only {product.Stock} available. Refresh your bag to see current stock.");
        if (item is null) db.CartItems.Add(new CartItem { UserId = userId, ProductId = productId, Quantity = requested });
        else item.Quantity = requested;
        await db.SaveChangesAsync(cancellationToken);
        var cart = await GetAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CartResult.Success(cart);
    }

    public async Task<CartDto> RemoveAsync(string userId, Guid productId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockCartOwnerAsync(userId, cancellationToken);
        await db.CartItems.Where(i => i.UserId == userId && i.ProductId == productId).ExecuteDeleteAsync(cancellationToken);
        var cart = await GetAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return cart;
    }

    private async Task LockCartOwnerAsync(string userId, CancellationToken cancellationToken)
    {
        // A PostgreSQL row lock serializes cart mutations across API/MCP hosts and processes.
        // It also protects the initially empty cart, where no CartItem exists to lock yet.
        var owners = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        if (owners.Count != 1) throw new InvalidOperationException("The authenticated cart owner no longer exists.");
    }
}
