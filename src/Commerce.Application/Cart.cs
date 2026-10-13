namespace Commerce.Application;

public sealed record CartItemDto(Guid ProductId, string Name, string Category, string Artwork,
    string Color, int Quantity, decimal UnitPrice, string Currency, int AvailableStock, decimal LineTotal)
{
    public bool IsAvailable => AvailableStock >= Quantity;
}

public sealed record CartDto(IReadOnlyList<CartItemDto> Items, int ItemCount, decimal Total, string Currency)
{
    public bool IsAvailable => Items.Count > 0 && Items.All(item => item.IsAvailable);
}

public enum CartError { InvalidQuantity, ProductNotFound, InsufficientStock, UnsupportedCurrency }
public sealed record CartResult(CartDto? Cart, CartError? Error, string? Message)
{
    public static CartResult Success(CartDto cart) => new(cart, null, null);
    public static CartResult Failure(CartError error, string message) => new(null, error, message);
}

// Hosts must obtain userId from the authenticated principal, never from tool or request arguments.
public interface ICartService
{
    Task<CartDto> GetAsync(string userId, CancellationToken cancellationToken);
    Task<CartResult> AddAsync(string userId, Guid productId, int quantity, CancellationToken cancellationToken);
    Task<CartResult> SetQuantityAsync(string userId, Guid productId, int quantity, CancellationToken cancellationToken);
    Task<CartDto> RemoveAsync(string userId, Guid productId, CancellationToken cancellationToken);
}
