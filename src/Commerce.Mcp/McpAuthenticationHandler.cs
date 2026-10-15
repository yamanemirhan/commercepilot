using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Commerce.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

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
        if (ticket is null)
        {
            var oauth = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            if (!oauth.Succeeded || oauth.Principal is null) return AuthenticateResult.Fail("Invalid or expired MCP credential.");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, oauth.Principal.GetClaim(OpenIddictConstants.Claims.Subject) ?? ""),
                new Claim("security_stamp", oauth.Principal.GetClaim("security_stamp") ?? "")
            }, McpAccessTokens.Scheme));
            ((ClaimsIdentity)principal.Identity!).AddClaims(oauth.Principal.GetScopes().Select(scope => new Claim("scope", scope)));
            ticket = new AuthenticationTicket(principal, McpAccessTokens.Scheme);
        }
        var id = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, Context.RequestAborted);
        if (user is null || user.SecurityStamp != ticket.Principal.FindFirstValue("security_stamp") ||
            (user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()))
            return AuthenticateResult.Fail("MCP credential is no longer valid.");
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = OAuthConfiguration.Challenge(Context.RequestServices.GetRequiredService<CommerceOAuth>());
        return Task.CompletedTask;
    }
}
