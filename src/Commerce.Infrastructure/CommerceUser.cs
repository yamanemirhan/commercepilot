using Microsoft.AspNetCore.Identity;

namespace Commerce.Infrastructure;

public sealed class CommerceUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}
