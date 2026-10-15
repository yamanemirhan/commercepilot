using Commerce.Infrastructure;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OpenIddict.Abstractions;

namespace Commerce.Api;

public static class OAuthConfiguration
{
    public static void AddCommerceOAuth(this WebApplicationBuilder builder)
    {
        var settings = new CommerceOAuth(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<OAuthRequestLimits>();
        builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<CommerceDbContext>())
            .AddServer(options =>
            {
                options.SetIssuer(settings.Issuer);
                options.SetAuthorizationEndpointUris("/connect/authorize");
                options.SetTokenEndpointUris("/connect/token");
                options.SetRevocationEndpointUris("/connect/revoke");
                options.SetConfigurationEndpointUris("/.well-known/oauth-authorization-server", "/.well-known/openid-configuration");
                options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                options.Configure(o => o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                options.RegisterScopes(CommerceOAuth.Scopes);
                options.RegisterResources(settings.Resource);
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(5));
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
                // All issued codes/access tokens use the persisted shared Data Protection key ring.
                // No identity tokens or JWT access tokens are issued by this OAuth-only server.
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                options.UseDataProtection();
                var host = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
                {
                    if (!settings.Issuer.IsLoopback || !settings.Resource.IsLoopback)
                        throw new InvalidOperationException("Development OAuth endpoints must use loopback URLs.");
                    host.DisableTransportSecurityRequirement();
                }
                else if (settings.Issuer.Scheme != "https" || settings.Resource.Scheme != "https")
                    throw new InvalidOperationException("Production OAuth issuer and resource URLs must use HTTPS.");
                options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (!context.Request.GetResources().SequenceEqual([settings.Resource.AbsoluteUri]))
                            context.Reject(Errors.InvalidTarget, "Request the configured CommercePilot MCP resource.");
                        if (context.Request.GetScopes().Length == 0)
                            context.Reject(Errors.InvalidScope, "Request at least one CommercePilot scope.");
                        return default;
                    }));
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (!context.Request.GetResources().SequenceEqual([settings.Resource.AbsoluteUri]))
                            context.Reject(Errors.InvalidTarget, "The token request must target the CommercePilot MCP resource.");
                        return default;
                    }));
            });
        builder.Services.AddSingleton<OAuthConsentTickets>();
    }
}
