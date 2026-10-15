using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commerce.Infrastructure;
using Commerce.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Authentication;
using OpenIddict.Abstractions;
using Xunit;

namespace Commerce.IntegrationTests;

public sealed class OAuthTests(CommerceFixture fixture) : IClassFixture<CommerceFixture>
{
    private const string ClientId = "commercepilot-probe";
    private const string Redirect = "http://127.0.0.1:5083/callback/";
    private const string Resource = "http://127.0.0.1:5081/mcp/authorized";
    private static readonly string Verifier = new('a', 64);

    private WebApplicationFactory<McpProgram> Factory()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<CommerceDbContext>().Database.GetConnectionString();
        return new WebApplicationFactory<McpProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Commerce", connection);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(fixture.Factory.Clock));
        });
    }

    private static Dictionary<string, string?> Request(string scopes = "cart:read") => new()
    {
        ["client_id"] = ClientId, ["redirect_uri"] = Redirect, ["response_type"] = "code",
        ["scope"] = scopes, ["resource"] = Resource, ["state"] = "test-state",
        ["code_challenge_method"] = "S256",
        ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)))
    };

    private static Dictionary<string, string> Fields(string html) => Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
        .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));

    private static async Task<Dictionary<string, string>> ConsentForm(HttpClient api, Dictionary<string, string?>? request = null)
    {
        using var response = await api.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", request ?? Request()));
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Allow access", html);
        return Fields(html);
    }

    private static async Task<string> Code(HttpClient api, string scopes = "cart:read")
    {
        var form = await ConsentForm(api, Request(scopes));
        form["decision"] = "allow";
        using var response = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var values = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(values.ContainsKey("error"), values.GetValueOrDefault("error_description").ToString());
        Assert.Equal("test-state", values["state"]);
        return values["code"].ToString();
    }

    private static Task<HttpResponseMessage> Exchange(HttpClient api, string code, string? verifier = null,
        string? resource = Resource, string client = ClientId, string redirect = Redirect)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = client,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier ?? Verifier
        };
        if (resource is not null) form["resource"] = resource;
        return api.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task Discovery_advertises_pkce_and_protected_resource_challenge()
    {
        using var api = fixture.Client();
        var metadata = (await api.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server"));
        Assert.Equal("http://127.0.0.1:5080/", metadata.GetProperty("issuer").GetString());
        Assert.Contains(metadata.GetProperty("code_challenge_methods_supported").EnumerateArray(), e => e.GetString() == "S256");
        Assert.DoesNotContain(metadata.GetProperty("code_challenge_methods_supported").EnumerateArray(), e => e.GetString() == "plain");
        using var factory = Factory(); using var mcp = factory.CreateClient();
        using var denied = await mcp.PostAsJsonAsync("/mcp/authorized", new { jsonrpc = "2.0", id = 1, method = "initialize" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("resource_metadata", denied.Headers.WwwAuthenticate.ToString());
        var resource = await mcp.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp/authorized");
        Assert.Equal(Resource, resource.GetProperty("resource").GetString());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-state")]
    [InlineData("wrong-issuer")]
    public async Task Sdk_discovers_authorization_and_validates_browser_callback_binding(string callbackMode)
    {
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        using var factory = Factory();
        using var http = new HttpClient(new ProtocolRouter(fixture.Factory.Server.CreateHandler(), factory.Server.CreateHandler()));
        var callbacks = 0;
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Resource), TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                ClientId = ClientId, RedirectUri = new Uri(Redirect), ScopeSelector = _ => ["orders:read"],
                AuthorizationCallbackHandler = async (context, ct) =>
                {
                    callbacks++;
                    using var page = await api.GetAsync(context.AuthorizationUri.PathAndQuery, ct);
                    var fields = Fields(await page.Content.ReadAsStringAsync(ct)); fields["decision"] = "allow";
                    using var allowed = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields), ct);
                    Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
                    var response = QueryHelpers.ParseQuery(allowed.Headers.Location!.Query);
                    return new AuthorizationResult
                    {
                        Code = response["code"], State = callbackMode == "wrong-state" ? "wrong" : response["state"],
                        Iss = callbackMode == "wrong-issuer" ? "https://another.example/" : response.GetValueOrDefault("iss")
                    };
                }
            }
        }, http);
        if (callbackMode == "valid")
        {
            await using var client = await McpClient.CreateAsync(transport);
            Assert.Equal(new[] { "get_my_orders", "get_order_status", "get_product", "search_products" },
                (await client.ListToolsAsync()).Select(t => t.Name).Order().ToArray());
            Assert.False((await client.CallToolAsync("get_my_orders", new Dictionary<string, object?>())).IsError is true);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var client = await McpClient.CreateAsync(transport);
            });
        }
        Assert.Equal(1, callbacks);
    }

    private sealed class ProtocolRouter(HttpMessageHandler apiHandler, HttpMessageHandler mcpHandler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker api = new(apiHandler);
        private readonly HttpMessageInvoker mcp = new(mcpHandler);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (request.RequestUri!.Port == 5080 ? api : mcp).SendAsync(request, ct);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { api.Dispose(); mcp.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Consent_then_single_use_pkce_code_produces_scoped_owner_bound_mcp_token()
    {
        var (api, user) = await fixture.SignedInAsync(); using var shopper = api;
        var code = await Code(api);
        using var exchange = await Exchange(api, code);
        var text = await exchange.Content.ReadAsStringAsync();
        Assert.True(exchange.IsSuccessStatusCode, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("cart:read", body.GetProperty("scope").GetString());
        Assert.False(body.TryGetProperty("refresh_token", out _));
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 1, 300);
        var token = body.GetProperty("access_token").GetString()!;
        using var factory = Factory(); using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        { Endpoint = new Uri(http.BaseAddress!, "/mcp/authorized"), TransportMode = HttpTransportMode.StreamableHttp }, http);
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "get_cart", "get_product", "search_products" }, tools.Select(t => t.Name).Order().ToArray());
        var cart = await client.CallToolAsync("get_cart", new Dictionary<string, object?> { ["userId"] = "someone-else" });
        Assert.False(cart.IsError is true);
        await Assert.ThrowsAnyAsync<McpException>(async () => await client.CallToolAsync("add_to_cart", new Dictionary<string, object?>()));
        using var replay = await Exchange(api, code);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync());
        // OAuth bearer tokens cannot be used as a browser's cookie/CSRF confirmation authority.
        using var bearerApi = fixture.Client();
        bearerApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var denied = await CommerceFixture.MutateAsync(bearerApi, HttpMethod.Post, $"/api/actions/{Guid.NewGuid()}/confirm", new { snapshotHash = "forged" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Theory]
    [InlineData("redirect_uri", "https://attacker.example/callback")]
    [InlineData("client_id", "unregistered-client")]
    [InlineData("scope", "orders:confirm")]
    [InlineData("resource", "https://another.example/mcp")]
    [InlineData("resource", "")]
    [InlineData("code_challenge", "")]
    [InlineData("code_challenge_method", "plain")]
    public async Task Invalid_authorization_requests_never_reach_consent(string key, string value)
    {
        using var api = fixture.Client();
        var request = Request(); request[key] = value;
        using var response = await api.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Allow access", text);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Redirect);
        if (response.Headers.Location is { } location)
        {
            Assert.StartsWith(Redirect, location.AbsoluteUri);
            Assert.True(QueryHelpers.ParseQuery(location.Query).ContainsKey("error"));
        }
    }

    [Theory]
    [InlineData("verifier")]
    [InlineData("resource")]
    [InlineData("missing-resource")]
    [InlineData("client")]
    [InlineData("redirect")]
    public async Task Code_exchange_rejects_wrong_bindings(string change)
    {
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        var code = await Code(api);
        using var response = await Exchange(api, code,
            verifier: change == "verifier" ? new string('b', 64) : null,
            resource: change == "resource" ? "https://another.example/mcp" : change == "missing-resource" ? null : Resource,
            client: change == "client" ? "commercepilot-inspector" : ClientId,
            redirect: change == "redirect" ? "http://127.0.0.1:5083/wrong" : Redirect);
        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain("access_token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Consent_requires_csrf_unchanged_displayed_terms_and_explicit_allow()
    {
        var (api, _) = await fixture.SignedInAsync(); using var shopper = api;
        var form = await ConsentForm(api);
        form["decision"] = "allow";
        form.Remove("__RequestVerificationToken");
        using var noCsrf = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        form = await ConsentForm(api);
        form["scope"] = "cart:read cart:write"; form["decision"] = "allow";
        using var changed = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        form = await ConsentForm(api); form["decision"] = "deny";
        using var denied = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var query = QueryHelpers.ParseQuery(denied.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.False(query.ContainsKey("code"));
        form = await ConsentForm(api); form["decision"] = "switch";
        using var switched = await api.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.StartsWith("/connect/authorize?", switched.Headers.Location!.OriginalString);
        using var loginPage = await api.GetAsync(switched.Headers.Location);
        Assert.Contains("name=\"password\"", await loginPage.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("security-stamp")]
    public async Task Issued_tokens_stop_working_after_revocation_expiry_or_account_change(string reason)
    {
        var (api, user) = await fixture.SignedInAsync(); using var shopper = api;
        using var issued = await Exchange(api, await Code(api));
        issued.EnsureSuccessStatusCode();
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        using var factory = Factory(); using var mcp = factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var before = await mcp.GetAsync("/health/ready"); before.EnsureSuccessStatusCode();
        if (reason == "revoked")
        {
            using var response = await api.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = ClientId, ["token"] = token, ["token_type_hint"] = "access_token" }));
            response.EnsureSuccessStatusCode();
        }
        else if (reason == "expired") fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(6));
        else
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<CommerceUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.Id))!);
        }
        using var after = await mcp.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
