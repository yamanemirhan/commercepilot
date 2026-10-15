using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenIddict.Abstractions;

namespace Commerce.Api;

// Bind the displayed permissions and authorization request to this shopper before accepting consent.
public sealed class OAuthConsentTickets(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("CommercePilot.OAuthConsent.v1");
    public static readonly string[] Fields = ["client_id", "redirect_uri", "response_type", "scope", "resource",
        "code_challenge", "code_challenge_method", "state", "prompt"];

    public string Issue(string owner, OpenIddictRequest request) => protector.Protect(JsonSerializer.Serialize(
        new Consent(owner, Hash(request), clock.GetUtcNow().AddMinutes(5))));

    public bool Validate(string token, string owner, OpenIddictRequest request)
    {
        if (token.Length > 16384) return false;
        try
        {
            var value = JsonSerializer.Deserialize<Consent>(protector.Unprotect(token));
            return value is not null && value.Owner == owner && value.Hash == Hash(request) && value.ExpiresAt > clock.GetUtcNow();
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return false; }
    }

    private static string Hash(OpenIddictRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Fields.Select(field => new { field, value = request[field].ToString() })))));
    private sealed record Consent(string Owner, string Hash, DateTimeOffset ExpiresAt);
}
