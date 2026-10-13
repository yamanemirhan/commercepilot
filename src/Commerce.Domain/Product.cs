namespace Commerce.Domain;

public sealed class Product
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public int Stock { get; set; }
    public string Artwork { get; set; } = "";
    public string Color { get; set; } = "";
    public string? Badge { get; set; }
    public int DisplayOrder { get; set; }
}
