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
    public const string OrdersRead = "orders:read";
    public const string OrdersPrepare = "orders:prepare";
    public const string ActionsRead = "actions:read";
    public const string OrdersCancelPrepare = "orders:cancel:prepare";
    public const string OrdersRefundPrepare = "orders:refund:prepare";
    private readonly TicketDataFormat format = new(protection.CreateProtector("CommercePilot.McpAccessToken", "v1", Audience));

    public McpAccessToken Issue(CommerceUser user, McpAccessGrant grant = McpAccessGrant.CartRead)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(5);
        string[] scopes = grant switch
        {
            McpAccessGrant.CartRead => [CartRead],
            McpAccessGrant.CartWrite => [CartRead, CartWrite],
            McpAccessGrant.OrdersRead => [OrdersRead],
            McpAccessGrant.CheckoutPrepare => [OrdersPrepare, ActionsRead],
            McpAccessGrant.ActionsRead => [ActionsRead],
            McpAccessGrant.CancellationPrepare => [OrdersRead, OrdersCancelPrepare, ActionsRead],
            McpAccessGrant.RefundPrepare => [OrdersRead, OrdersRefundPrepare, ActionsRead],
            _ => throw new ArgumentOutOfRangeException(nameof(grant))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim("security_stamp", user.SecurityStamp ?? throw new InvalidOperationException("Missing security stamp.")),
            new Claim("aud", Audience), new Claim("iss", Issuer)
        }, Scheme));
        ((ClaimsIdentity)principal.Identity!).AddClaims(scopes.Select(scope => new Claim("scope", scope)));
        var ticket = new AuthenticationTicket(principal,
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = expires }, Scheme);
        return new(format.Protect(ticket), expires, string.Join(' ', scopes));
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
public enum McpAccessGrant { CartRead, CartWrite, OrdersRead, CheckoutPrepare, ActionsRead, CancellationPrepare, RefundPrepare }
