using Commerce.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Infrastructure;

public sealed class CommerceDbContext(DbContextOptions<CommerceDbContext> options)
    : IdentityDbContext<CommerceUser>(options), IDataProtectionKeyContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<CommerceUser>().Property(u => u.DisplayName).HasMaxLength(100);
        var cartItem = modelBuilder.Entity<CartItem>();
        cartItem.HasKey(i => new { i.UserId, i.ProductId });
        cartItem.HasOne<CommerceUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        cartItem.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        cartItem.ToTable("CartItems", table => table.HasCheckConstraint("CK_CartItems_Quantity", "\"Quantity\" BETWEEN 1 AND 99"));
        var product = modelBuilder.Entity<Product>();
        product.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Price", "\"Price\" >= 0");
            table.HasCheckConstraint("CK_Products_Stock", "\"Stock\" >= 0");
        });
        product.HasKey(p => p.Id);
        product.Property(p => p.Slug).HasMaxLength(120);
        product.HasIndex(p => p.Slug).IsUnique();
        product.Property(p => p.Name).HasMaxLength(160);
        product.Property(p => p.Description).HasMaxLength(2000);
        product.Property(p => p.Category).HasMaxLength(40);
        product.Property(p => p.Currency).HasMaxLength(3);
        product.Property(p => p.Price).HasPrecision(12, 2);
        product.Property(p => p.Artwork).HasMaxLength(40);
        product.Property(p => p.Color).HasMaxLength(30);
        product.Property(p => p.Badge).HasMaxLength(40);
        product.HasIndex(p => new { p.Category, p.Price });
    }
}
