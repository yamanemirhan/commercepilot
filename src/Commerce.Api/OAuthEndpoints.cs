using System.Net;
using System.Security.Claims;
using Commerce.Infrastructure;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Commerce.Api;

public static class OAuthEndpoints
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static void MapCommerceOAuth(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync).RequireRateLimiting("oauth");
        app.MapPost("/connect/login", LoginAsync).RequireRateLimiting("login");
        app.MapPost("/connect/token", async (HttpContext context, UserManager<CommerceUser> users) =>
        {
            var result = await context.AuthenticateAsync(Scheme);
            var principal = result.Principal;
            var user = principal is null ? null : await users.FindByIdAsync(principal.GetClaim(Claims.Subject)!);
            if (user is null || await users.IsLockedOutAsync(user) || user.SecurityStamp != principal!.GetClaim("security_stamp"))
                return Error(Errors.InvalidGrant, "The account is no longer available. Sign in again.");
            return Results.SignIn(principal!, authenticationScheme: Scheme);
        });
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext context, UserManager<CommerceUser> users,
        IOpenIddictApplicationManager applications, IAntiforgery csrf, OAuthConsentTickets tickets)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing OAuth request.");
        var user = await users.GetUserAsync(context.User);
        var isPost = HttpMethods.IsPost(context.Request.Method);
        if (isPost)
        {
            try { await csrf.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Invalid security token. Restart the connection." }); }
        }
        if (user is null)
        {
            if (isPost) return Error(Errors.LoginRequired, "Sign in before granting access.");
            return LoginPage(context, csrf, context.Request.Path + context.Request.QueryString);
        }
        if (await users.IsLockedOutAsync(user)) return Error(Errors.AccessDenied, "This account is locked. Try again later.");
        if (request.HasPromptValue(PromptValues.None)) return Error(Errors.ConsentRequired, "Explicit permission is required for each connection.");

        if (isPost)
        {
            var form = await context.Request.ReadFormAsync();
            if (!tickets.Validate(form["consent_ticket"].ToString(), user.Id, request))
                return Results.BadRequest(new { error = "The displayed permission request changed or expired. Restart the connection." });
            if (form["decision"] == "switch")
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                var query = QueryString.Create(OAuthConsentTickets.Fields.Where(f => request[f] is not null)
                    .Select(f => new KeyValuePair<string, string?>(f, request[f].ToString())));
                return Results.LocalRedirect("/connect/authorize" + query);
            }
            if (form["decision"] == "deny") return Error(Errors.AccessDenied, "The shopper declined access.");
            if (form["decision"] != "allow") return Results.BadRequest();
            var identity = new ClaimsIdentity(Scheme, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, user.Id);
            identity.SetClaim("security_stamp", user.SecurityStamp);
            identity.SetScopes(request.GetScopes());
            identity.SetResources(request.GetResources());
            identity.SetDestinations(_ => [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: Scheme);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("Validated client was not found.");
        var name = await applications.GetDisplayNameAsync(application) ?? request.ClientId!;
        var fields = string.Concat(OAuthConsentTickets.Fields.Where(f => request[f] is not null)
            .Select(f => Hidden(f, request[f].ToString() ?? "")));
        var list = string.Concat(request.GetScopes().Select(scope => $"<li>{H(CommerceOAuth.ScopeDescription(scope))}</li>"));
        return Page(context, "Connect your account", $"""
            <span class="eyebrow">ACCOUNT CONNECTION</span><h1>Let {H(name)} help you shop?</h1>
            <p>Signed in as <strong>{H(user.Email!)}</strong>. This client is requesting:</p>
            <ul>{list}</ul><div class="notice">Access lasts five minutes. These permissions cannot approve orders, cancellations or refunds. Those always need your explicit action in CommercePilot.</div>
            <form method="post" action="/connect/authorize">{fields}{Csrf(context, csrf)}{Hidden("consent_ticket", tickets.Issue(user.Id, request))}
            <div class="buttons"><button class="secondary" name="decision" value="deny">Deny access</button><button name="decision" value="allow">Allow access</button></div>
            <div class="buttons"><button class="secondary" name="decision" value="switch">Use another account</button></div></form>
            <p class="fine">Client: {H(request.ClientId!)}<br>Returns to: {H(request.RedirectUri!)}</p>
            """, new Uri(request.RedirectUri!));
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery csrf,
        SignInManager<CommerceUser> signIn, UserManager<CommerceUser> users)
    {
        try { await csrf.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Invalid security token. Restart the connection." }); }
        var form = await context.Request.ReadFormAsync();
        var returnUrl = form["returnUrl"].ToString();
        if (returnUrl.Length > 8192 || !returnUrl.StartsWith("/connect/authorize?", StringComparison.Ordinal) || returnUrl.Contains('\\'))
            return Results.BadRequest(new { error = "Invalid connection request." });
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var user = email.Length <= 254 ? await users.FindByEmailAsync(email) : null;
        var result = user is not null && password.Length is > 0 and <= 128
            ? await signIn.PasswordSignInAsync(user, password, false, true) : SignInResult.Failed;
        return result.Succeeded ? Results.LocalRedirect(returnUrl) : LoginPage(context, csrf, returnUrl, "Sign-in failed. Check your details or try again later.");
    }

    private static IResult LoginPage(HttpContext context, IAntiforgery csrf, string returnUrl, string? error = null) =>
        Page(context, "Sign in to connect", $"""
            <span class="eyebrow">COMMERCEPILOT</span><h1>Your account.<br>Your permission.</h1>
            <p>Sign in here to connect an MCP client. Your password stays with CommercePilot.</p>
            {(error is null ? "" : $"<p role=\"alert\" class=\"error\">{H(error)}</p>")}
            <form method="post" action="/connect/login">{Csrf(context, csrf)}{Hidden("returnUrl", returnUrl)}
            <label>Email<input name="email" type="email" autocomplete="username" maxlength="254" required></label>
            <label>Password<input name="password" type="password" autocomplete="current-password" maxlength="128" required></label>
            <button type="submit">Continue</button></form><p class="fine">You will review the requested permissions before connecting.</p>
            """);

    private static IResult Error(string error, string description) => Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
    }), [Scheme]);

    private static string H(string text) => WebUtility.HtmlEncode(text);
    private static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{H(name)}\" value=\"{H(value)}\">";
    private static string Csrf(HttpContext context, IAntiforgery csrf)
    {
        var token = csrf.GetAndStoreTokens(context);
        return Hidden(token.FormFieldName, token.RequestToken!);
    }

    private static IResult Page(HttpContext context, string title, string content, Uri? callback = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        // Form redirect destinations must also pass CSP. Only a validated, registered callback origin is allowed.
        var callbackOrigin = callback is null ? "" : " " + callback.GetLeftPart(UriPartial.Authority);
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'unsafe-inline'; form-action 'self'{callbackOrigin}; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Content($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{H(title)}} · CommercePilot</title>
            <style>*{box-sizing:border-box}body{margin:0;background:#f4f3ef;color:#202722;font:16px/1.55 system-ui,sans-serif;min-height:100vh;display:grid;place-items:center;padding:24px}main{width:100%;max-width:560px;background:#fff;border:1px solid #e0e4dd;border-radius:24px;padding:36px;box-shadow:0 16px 48px #18251b0a}h1{font-size:30px;line-height:1.2;letter-spacing:-1px;margin:14px 0 20px}p{color:#546058}.eyebrow{font-size:11px;letter-spacing:2px;font-weight:700;color:#55764e}label{display:block;margin:16px 0;font-size:14px;font-weight:600}input{display:block;width:100%;padding:12px;border:1px solid #bac5b7;border-radius:10px;margin-top:6px;font:inherit}button{font:600 14px system-ui;border:1px solid #253d2a;border-radius:99px;padding:13px 22px;background:#253d2a;color:white;cursor:pointer}button:focus-visible,input:focus-visible{outline:3px solid #b2c78e;outline-offset:3px}.secondary{background:white;color:#253d2a}.buttons{display:flex;flex-wrap:wrap;gap:12px;margin-top:24px}.notice{background:#f0f4ea;padding:16px;border-radius:12px;font-size:14px}.fine{font-size:12px;overflow-wrap:anywhere}.error{color:#963f38}li{margin:8px 0}@media(max-width:480px){main{padding:24px}h1{font-size:26px} }</style></head><body><main>{{content}}</main></body></html>
            """, "text/html; charset=utf-8");
    }
}
