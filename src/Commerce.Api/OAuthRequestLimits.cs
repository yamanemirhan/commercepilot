using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace Commerce.Api;

// Protocol handlers can complete token/revocation requests inside authentication middleware.
// Apply their request bounds before that middleware, not only on the mapped endpoint.
public sealed class OAuthRequestLimits : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/connect"))
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 16 * 1024;
            using var lease = await limiter.AcquireAsync(context, cancellationToken: context.RequestAborted);
            if (!lease.IsAcquired)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "60";
                return;
            }
        }
        await next(context);
    }

    public void Dispose() => limiter.Dispose();
}
