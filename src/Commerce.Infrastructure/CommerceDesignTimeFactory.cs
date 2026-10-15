using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Commerce.Infrastructure;

// Migration generation needs the model, not a running API, seed operation or production credential.
public sealed class CommerceDesignTimeFactory : IDesignTimeDbContextFactory<CommerceDbContext>
{
    public CommerceDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CommerceDbContext>()
        .UseNpgsql("Host=localhost;Database=commercepilot_design;Username=design").Options);
}
