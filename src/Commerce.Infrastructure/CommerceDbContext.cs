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
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatTurn> ChatTurns => Set<ChatTurn>();
    public DbSet<ChatEvent> ChatEvents => Set<ChatEvent>();
    public DbSet<ChatCard> ChatCards => Set<ChatCard>();
    public DbSet<KnowledgeRelease> KnowledgeReleases => Set<KnowledgeRelease>();
    public DbSet<KnowledgeIndex> KnowledgeIndexes => Set<KnowledgeIndex>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();
        var release = modelBuilder.Entity<KnowledgeRelease>();
        release.HasKey(x => x.Id);
        release.Property(x => x.ContentHash).HasMaxLength(64);
        release.Property(x => x.ApplicationRevision).HasMaxLength(64);
        release.Property(x => x.ModelIdentity).HasMaxLength(200);
        release.Property(x => x.PublishedBy).HasMaxLength(100);
        release.HasIndex(x => new { x.ContentHash, x.ApplicationRevision, x.ModelIdentity }).IsUnique();
        var index = modelBuilder.Entity<KnowledgeIndex>();
        index.HasKey(x => x.Name);
        index.Property(x => x.Name).HasMaxLength(30);
        index.HasOne<KnowledgeRelease>().WithMany().HasForeignKey(x => x.ActiveReleaseId).OnDelete(DeleteBehavior.Restrict);
        var chunk = modelBuilder.Entity<KnowledgeChunk>();
        chunk.HasKey(x => x.Id);
        chunk.HasOne<KnowledgeRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Cascade);
        chunk.Property(x => x.DocumentId).HasMaxLength(80);
        chunk.Property(x => x.Title).HasMaxLength(160);
        chunk.Property(x => x.Body).HasMaxLength(1800);
        chunk.Property(x => x.SearchText).HasMaxLength(4000);
        chunk.HasIndex(x => new { x.ReleaseId, x.DocumentId, x.Ordinal }).IsUnique();
        var conversation = modelBuilder.Entity<Conversation>();
        conversation.HasKey(x => x.Id);
        conversation.Property(x => x.Title).HasMaxLength(100);
        conversation.HasOne<CommerceUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        conversation.HasIndex(x => new { x.UserId, x.CreatedAt });
        var turn = modelBuilder.Entity<ChatTurn>();
        turn.HasKey(x => x.Id);
        turn.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        turn.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        turn.Property(x => x.Input).HasMaxLength(2000);
        turn.Property(x => x.Status).HasMaxLength(20);
        var chatEvent = modelBuilder.Entity<ChatEvent>();
        chatEvent.HasKey(x => x.Sequence);
        chatEvent.Property(x => x.Sequence).ValueGeneratedOnAdd();
        chatEvent.HasOne<ChatTurn>().WithMany().HasForeignKey(x => x.TurnId).OnDelete(DeleteBehavior.Cascade);
        chatEvent.HasIndex(x => new { x.ConversationId, x.Sequence });
        chatEvent.Property(x => x.Kind).HasMaxLength(40);
        var card = modelBuilder.Entity<ChatCard>();
        card.HasKey(x => x.Id);
        card.HasOne<ChatTurn>().WithMany().HasForeignKey(x => x.TurnId).OnDelete(DeleteBehavior.Cascade);
        card.HasIndex(x => new { x.ConversationId, x.ObservedAt });
        card.Property(x => x.Kind).HasMaxLength(30);
        var action = modelBuilder.Entity<PendingAction>();
        action.HasKey(a => a.Id);
        action.HasOne<CommerceUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        action.HasIndex(a => new { a.UserId, a.Status, a.CreatedAt });
        action.Property(a => a.Kind).HasMaxLength(24);
        action.Property(a => a.Status).HasMaxLength(24);
        action.Property(a => a.SnapshotHash).HasMaxLength(64);
        var order = modelBuilder.Entity<Order>();
        order.HasKey(o => o.Id);
        order.HasOne<CommerceUser>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Restrict);
        order.HasIndex(o => new { o.UserId, o.CreatedAt });
        order.HasIndex(o => o.NextTransitionAt);
        order.Property(o => o.Total).HasPrecision(12, 2);
        order.Property(o => o.CapturedAmount).HasPrecision(12, 2);
        order.Property(o => o.RefundedAmount).HasPrecision(12, 2);
        order.Property(o => o.Currency).HasMaxLength(3);
        order.Property(o => o.Status).HasMaxLength(24);
        order.Property(o => o.PaymentStatus).HasMaxLength(24);
        order.ToTable("Orders", t => t.HasCheckConstraint("CK_Orders_Money",
            "\"Total\" >= 0 AND \"CapturedAmount\" >= 0 AND \"CapturedAmount\" <= \"Total\" AND \"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"CapturedAmount\""));
        order.HasMany(o => o.Events).WithOne().HasForeignKey(e => e.OrderId);
        modelBuilder.Entity<OrderEvent>().Property(e => e.Title).HasMaxLength(100);
        modelBuilder.Entity<OrderEvent>().Property(e => e.Detail).HasMaxLength(500);
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
