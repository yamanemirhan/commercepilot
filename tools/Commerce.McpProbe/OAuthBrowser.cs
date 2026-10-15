using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

internal static class OAuthBrowser
{
    public static ClientOAuthOptions Options(ShopperAccess access) => new()
    {
        ClientId = "commercepilot-probe",
        RedirectUri = new Uri("http://127.0.0.1:5083/callback/"),
        ScopeSelector = _ => access switch
        {
            ShopperAccess.CartRead => ["cart:read"],
            ShopperAccess.CartWrite => ["cart:read", "cart:write"],
            ShopperAccess.OrdersRead => ["orders:read"],
            ShopperAccess.CheckoutPrepare => ["orders:prepare", "actions:read"],
            ShopperAccess.ActionsRead => ["actions:read"],
            ShopperAccess.CancellationPrepare => ["orders:read", "orders:cancel:prepare", "actions:read"],
            ShopperAccess.RefundPrepare => ["orders:read", "orders:refund:prepare", "actions:read"],
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        },
        AuthorizationCallbackHandler = AuthorizeAsync
    };

    private static async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:5083");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
        await using var callback = builder.Build();
        callback.MapGet("/callback/", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            // The SDK checks state and issuer against its original request before token exchange.
            if (http.Request.Query.ContainsKey("error"))
                completion.TrySetException(new InvalidOperationException("Access was declined or authorization failed. No tool was called."));
            else completion.TrySetResult(new AuthorizationResult
            {
                Code = http.Request.Query["code"].ToString(),
                State = http.Request.Query["state"].ToString(),
                Iss = http.Request.Query["iss"].ToString()
            });
            return Results.Text("Authorization response received. You can close this tab and return to the terminal.");
        });
        await callback.StartAsync(ct);
        Console.WriteLine("Opening CommercePilot sign-in and permissions in your browser. The console never receives your password.");
        try
        {
            Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
            return await completion.Task.WaitAsync(ct);
        }
        finally { await callback.StopAsync(CancellationToken.None); }
    }
}
