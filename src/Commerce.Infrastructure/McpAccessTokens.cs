using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Commerce.Infrastructure;

// First-party, local credentials. This is not an OAuth authorization server.
public sealed class McpAccessTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    public const string Scheme = "CommerceMcp";
    public const string Audience = "commercepilot:mcp";
    public const string Issuer = "commercepilot:api";
    public const string CartRead = "cart:read";
    public const string CartWrite = "cart:write";
    private readonly TicketDataFormat format = new(protection.CreateProtector("CommercePilot.McpAccessToken", "v1", Audience));

    public McpAccessToken Issue(CommerceUser user, bool allowCartWrite = false)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(5);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim("security_stamp", user.SecurityStamp ?? throw new InvalidOperationException("Missing security stamp.")),
            new Claim("aud", Audience), new Claim("iss", Issuer), new Claim("scope", CartRead)
        }, Scheme));
        if (allowCartWrite) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("scope", CartWrite));
        var ticket = new AuthenticationTicket(principal,
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = expires }, Scheme);
        return new(format.Protect(ticket), expires, allowCartWrite ? $"{CartRead} {CartWrite}" : CartRead);
    }

    public AuthenticationTicket? Validate(string token)
    {
        var ticket = format.Unprotect(token);
        var now = clock.GetUtcNow();
        if (ticket is null || ticket.AuthenticationScheme != Scheme ||
            ticket.Properties.IssuedUtc is not { } issued || ticket.Properties.ExpiresUtc is not { } expires ||
            issued > now || expires <= now || expires <= issued || expires - issued > TimeSpan.FromMinutes(5) ||
            ticket.Principal.FindFirstValue("aud") != Audience || ticket.Principal.FindFirstValue("iss") != Issuer ||
            string.IsNullOrEmpty(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)) ||
            string.IsNullOrEmpty(ticket.Principal.FindFirstValue("security_stamp"))) return null;
        return ticket;
    }
}

public sealed record McpAccessToken(string AccessToken, DateTimeOffset ExpiresAt, string Scope);
