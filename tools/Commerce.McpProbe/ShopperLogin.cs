using System.Net;
using System.Net.Http.Json;
using System.Text;

internal static class ShopperLogin
{
    public static async Task<string> GetTokenAsync(string account, CancellationToken ct, ShopperAccess access = ShopperAccess.CartRead)
    {
        var email = account.ToLowerInvariant() switch
        {
            "alex" => "alex@commercepilot.local",
            "sam" => "sam@commercepilot.local",
            _ => throw new ArgumentException("Choose alex or sam for the local demo.")
        };
        Console.Write($"Password for {email} (hidden): ");
        var password = ReadPassword();
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var api = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5080") };
        using var login = await PostAsync(api, "/api/auth/login", new { email, password }, ct);
        password = "";
        try
        {
            var tokenPath = access switch
            {
                ShopperAccess.CartRead => "/api/auth/mcp-token",
                ShopperAccess.CartWrite => "/api/auth/mcp-cart-token",
                ShopperAccess.OrdersRead => "/api/auth/mcp-orders-token",
                ShopperAccess.CheckoutPrepare => "/api/auth/mcp-checkout-token",
                ShopperAccess.ActionsRead => "/api/auth/mcp-actions-token",
                ShopperAccess.CancellationPrepare => "/api/auth/mcp-cancellation-token",
                ShopperAccess.RefundPrepare => "/api/auth/mcp-refund-token",
                _ => throw new ArgumentOutOfRangeException(nameof(access))
            };
            using var response = await PostAsync(api, tokenPath, null, ct);
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("The API did not issue an MCP credential.");
            Console.WriteLine($"Signed in as {email}. Scope: {token.Scope}. Expires: {token.ExpiresAt:O}");
            Console.WriteLine("The credential stays in memory and is sent only to the local MCP endpoint.");
            return token.AccessToken;
        }
        finally
        {
            // This console login is independent of the browser's shopping session.
            using var logout = await PostAsync(api, "/api/auth/logout", null, ct);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient api, string path, object? body, CancellationToken ct)
    {
        var csrf = await api.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf", ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-Token", csrf!.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await api.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException($"Account request failed (HTTP {status}). Check your local password and API readiness.");
        }
        return response;
    }

    private static string ReadPassword()
    {
        // Redirected stdin allows local automation without putting passwords in command arguments.
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar) && value.Length < 128) value.Append(key.KeyChar);
        }
    }

    private sealed record CsrfResponse(string Token);
    private sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string Scope);
}

internal enum ShopperAccess { CartRead, CartWrite, OrdersRead, CheckoutPrepare, ActionsRead, CancellationPrepare, RefundPrepare }
