using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Commerce.Infrastructure;

public static class IdentitySeed
{
    public static async Task InitializeAsync(UserManager<CommerceUser> users, IConfiguration configuration)
    {
        foreach (var name in new[] { "Alex", "Sam" })
        {
            var email = $"{name.ToLowerInvariant()}@commercepilot.local";
            if (await users.FindByEmailAsync(email) is not null) continue;
            var password = configuration[$"DemoAccounts:{name}Password"];
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException($"DemoAccounts__{name}Password is required to initialize demo accounts. Run scripts/start.ps1.");
            var result = await users.CreateAsync(new CommerceUser
            {
                UserName = email, Email = email, EmailConfirmed = true, DisplayName = name
            }, password);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Could not initialize {name}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }
    }
}
