using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Commerce.Mcp;

public sealed class McpAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, McpAccessTokens tokens, CommerceDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization")) return AuthenticateResult.NoResult();
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter) || header.Parameter.Length > 8192)
            return AuthenticateResult.Fail("Invalid MCP credential.");

        var ticket = tokens.Validate(header.Parameter);
        if (ticket is null) return AuthenticateResult.Fail("Invalid or expired MCP credential.");
        var id = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, Context.RequestAborted);
        if (user is null || user.SecurityStamp != ticket.Principal.FindFirstValue("security_stamp") ||
            (user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()))
            return AuthenticateResult.Fail("MCP credential is no longer valid.");
        return AuthenticateResult.Success(ticket);
    }
}
