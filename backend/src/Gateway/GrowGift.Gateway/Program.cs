using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Per-client rate limiting: 100 req/min sliding window keyed by user or IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6
            }));
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Strict-Transport-Security", "max-age=63072000; includeSubDomains; preload");
    ctx.Response.Headers.Append("Content-Security-Policy", "default-src 'none'");
    await next();
});

// Service-to-service endpoints live under /internal/ (Identity's child-accounts, link-as-parent and
// shipping-address, Fulfillment's order intake, …). They are for the private network only, so the public
// gateway never forwards them.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Value?.Contains("/internal/", StringComparison.OrdinalIgnoreCase) == true)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.UseRateLimiter();
app.MapReverseProxy();
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.Run();
