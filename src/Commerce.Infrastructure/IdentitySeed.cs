using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Commerce.Infrastructure;

public static class IdentitySeed
{
    public const string AdministratorRole = "Administrator";

    public static async Task InitializeAdministratorAsync(UserManager<CommerceUser> users, RoleManager<IdentityRole> roles, IConfiguration configuration)
    {
        var password = configuration["DemoAccounts:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password)) return; // Explicit local demo bootstrap only.
        if (!await roles.RoleExistsAsync(AdministratorRole))
        {
            var created = await roles.CreateAsync(new IdentityRole(AdministratorRole));
            if (!created.Succeeded) throw new InvalidOperationException("Could not create the administrator role.");
        }
        var user = await users.FindByEmailAsync("admin@commercepilot.local");
        if (user is null)
        {
            user = new CommerceUser { UserName = "admin@commercepilot.local", Email = "admin@commercepilot.local",
                EmailConfirmed = true, DisplayName = "Administrator" };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded) throw new InvalidOperationException("Could not initialize the administrator. Check the local password requirements.");
            var granted = await users.AddToRoleAsync(user, AdministratorRole);
            if (!granted.Succeeded) throw new InvalidOperationException("Could not grant the administrator role.");
        }
        // Existing accounts are never promoted on restart; deliberate role revocation stays effective.
    }

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
