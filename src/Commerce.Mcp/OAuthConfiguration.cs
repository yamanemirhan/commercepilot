using Commerce.Infrastructure;
using OpenIddict.Abstractions;

namespace Commerce.Mcp;

public static class OAuthConfiguration
{
    public static void AddCommerceOAuthValidation(this WebApplicationBuilder builder)
    {
        var settings = new CommerceOAuth(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<CommerceDbContext>())
            .AddValidation(options =>
            {
                options.SetIssuer(settings.Issuer);
                // Both hosts share this explicit issuer/resource and the persisted Data Protection ring.
                // No network key discovery is needed for opaque tokens in this trust boundary.
                options.SetConfiguration(new OpenIddictConfiguration { Issuer = settings.Issuer });
                options.AddAudiences(settings.Resource.AbsoluteUri);
                options.EnableTokenEntryValidation();
                options.UseDataProtection();
                options.UseAspNetCore();
            });
    }

    public static void MapCommerceOAuthMetadata(this WebApplication app)
    {
        IResult Metadata(CommerceOAuth settings) => Results.Json(new
        {
            resource = settings.Resource.AbsoluteUri,
            authorization_servers = new[] { settings.Issuer.AbsoluteUri },
            scopes_supported = CommerceOAuth.Scopes,
            bearer_methods_supported = new[] { "header" },
            resource_name = "CommercePilot MCP"
        });
        app.MapGet("/.well-known/oauth-protected-resource", Metadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp/authorized", Metadata);
    }

    public static string Challenge(CommerceOAuth settings) =>
        $"Bearer resource_metadata=\"{new Uri(settings.Resource, "/.well-known/oauth-protected-resource/mcp/authorized")}\"";
}
