using Microsoft.Extensions.Configuration;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Commerce.Infrastructure;

public sealed class CommerceOAuth(IConfiguration configuration)
{
    public Uri Issuer { get; } = Endpoint(configuration["OAuth:Issuer"] ?? "http://127.0.0.1:5080/");
    public Uri Resource { get; } = Endpoint(configuration["OAuth:Resource"] ?? "http://127.0.0.1:5081/mcp/authorized");
    public static readonly string[] Scopes = [McpAccessTokens.CartRead, McpAccessTokens.CartWrite,
        McpAccessTokens.OrdersRead, McpAccessTokens.OrdersPrepare, McpAccessTokens.ActionsRead,
        McpAccessTokens.OrdersCancelPrepare, McpAccessTokens.OrdersRefundPrepare];

    public static string ScopeDescription(string scope) => scope switch
    {
        McpAccessTokens.CartRead => "Read your shopping bag",
        McpAccessTokens.CartWrite => "Add, change and remove items in your bag",
        McpAccessTokens.OrdersRead => "Read your order history and details",
        McpAccessTokens.OrdersPrepare => "Prepare checkout reviews for your approval",
        McpAccessTokens.ActionsRead => "Read your review terms and decisions",
        McpAccessTokens.OrdersCancelPrepare => "Prepare cancellation reviews for your approval",
        McpAccessTokens.OrdersRefundPrepare => "Prepare refund reviews for your approval",
        _ => throw new ArgumentException("Unknown scope.")
    };

    private static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("OAuth URLs require HTTPS, except loopback URLs for local development.");
        return uri;
    }

    public async Task RegisterClientsAsync(IOpenIddictApplicationManager manager, CancellationToken ct = default)
    {
        // Static registration: an operator must explicitly configure additional clients/redirects.
        var clients = configuration.GetSection("OAuth:Clients").Get<OAuthClientRegistration[]>() ??
        [new("commercepilot-probe", "CommercePilot MCP Probe", ["http://127.0.0.1:5083/callback/"]),
         new("commercepilot-inspector", "MCP Inspector", ["http://localhost:6274/oauth/callback", "http://localhost:6274/oauth/callback/debug"])];
        foreach (var client in clients)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.DisplayName) || client.RedirectUris.Length == 0)
                throw new InvalidOperationException("OAuth clients require an ID, display name and exact redirect URIs.");
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId, DisplayName = client.DisplayName,
                ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit
            };
            foreach (var redirect in client.RedirectUris) descriptor.RedirectUris.Add(Endpoint(redirect));
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code]);
            descriptor.AddResourcePermissions(Resource.AbsoluteUri);
            descriptor.Permissions.UnionWith(Scopes.Select(s => Permissions.Prefixes.Scope + s));
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            var existing = await manager.FindByClientIdAsync(client.ClientId, ct);
            if (existing is null) await manager.CreateAsync(descriptor, ct);
            else await manager.UpdateAsync(existing, descriptor, ct);
        }
    }
}

public sealed record OAuthClientRegistration(string ClientId, string DisplayName, string[] RedirectUris);
