using Microsoft.EntityFrameworkCore.Infrastructure;
using Confluent.Kafka;
using GrowGift.Brokerage.Api;
using GrowGift.Brokerage.Domain;
using GrowGift.Brokerage.Infrastructure.Consumers;
using GrowGift.Brokerage.Infrastructure.Persistence;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using GrowGift.Infrastructure.Common.Idempotency;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BrokerageDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("brokerage"),
        npgsql => npgsql.EnableRetryOnFailure(3)));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("redis")!));
builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
builder.Services.AddMemoryCache(); // caches ViewTrade's country reference list — see ViewTradeCountriesClient

// ViewTrade HTTP client: retry with jitter + circuit breaker + mTLS in production.
// Auth scheme is configurable because ViewTrade's real partner API isn't
// publicly documented — set VIEWTRADE_AUTH_SCHEME to whatever their sandbox
// docs specify: "ApiKeyHeader" (default, X-Api-Key: <key>), "Bearer"
// (Authorization: Bearer <key>), or "BasicKeySecret" (HTTP Basic with
// VIEWTRADE_API_KEY as username and VIEWTRADE_API_SECRET as password).
var viewTradeBaseUrl = Environment.GetEnvironmentVariable("VIEWTRADE_BASE_URL")
    ?? builder.Configuration["ViewTrade:BaseUrl"]!;
var viewTradeAuthScheme = Environment.GetEnvironmentVariable("VIEWTRADE_AUTH_SCHEME")
    ?? builder.Configuration["ViewTrade:AuthScheme"] ?? "ApiKeyHeader";
var viewTradeApiKey = Environment.GetEnvironmentVariable("VIEWTRADE_API_KEY") ?? "dev-only";
var viewTradeApiSecret = Environment.GetEnvironmentVariable("VIEWTRADE_API_SECRET");
// Confirmed required on UserRegistration (POST /api/v1/auth/b2c/register
// on UMA) — ViewTrade rejects it with "firm or branch header should be
// specified" (code 6000) without these. Every other confirmed AMO/UMA call
// derives firm/branch from the bearer token's own claims instead, which is
// why this pair is only needed here.
var viewTradeFirmId = Environment.GetEnvironmentVariable("VIEWTRADE_FIRM_ID")
    ?? builder.Configuration["ViewTrade:FirmId"];
var viewTradeBranchId = Environment.GetEnvironmentVariable("VIEWTRADE_BRANCH_ID")
    ?? builder.Configuration["ViewTrade:BranchId"];

// AMO ("Applicant Management") is the ViewTrade host behind onboarding —
// countries list, form answers, form submission. It's a SEPARATE host from
// UMA below, and expects a bearer token minted by UMA, not the static
// api_key directly (confirmed via the GrowGift - ViewTrade Postman
// workspace: ViewTrade-Staging-AMO vs ViewTrade-Staging-UMA environments).
var viewTradeAmoBaseUrl = Environment.GetEnvironmentVariable("VIEWTRADE_AMO_BASE_URL")
    ?? builder.Configuration["ViewTrade:AmoBaseUrl"] ?? viewTradeBaseUrl;
// UMA ("User Management") issues that bearer token from api_key/api_secret
// — see ViewTradeAuthTokenProvider and the "AccessTokenGenerate" request.
var viewTradeUmaBaseUrl = Environment.GetEnvironmentVariable("VIEWTRADE_UMA_BASE_URL")
    ?? builder.Configuration["ViewTrade:UmaBaseUrl"] ?? viewTradeBaseUrl;

// HttpClient silently drops any path segment already in BaseAddress when a
// relative request URI starts with "/" (RFC 3986 absolute-path reference)
// — e.g. BaseAddress "https://host/tenant-a" + relative "/api/x" resolves
// to "https://host/api/x", losing "/tenant-a" entirely. If any of these
// ViewTrade base URLs carry a path segment (tenant/domain identifier and
// not just a bare host), that combination bug would silently strip it on
// every call — a very plausible explanation for "no domain found" errors.
// Every call site below was updated to pass relative paths WITHOUT a
// leading slash; this guarantees BaseAddress ends with "/" so the two
// combine safely regardless of whether it has a path segment.
static Uri ViewTradeBaseAddress(string url) => new(url.EndsWith('/') ? url : url + "/");

builder.Services.AddHttpClient<ViewTradeProvider>(c =>
    {
        // NOTE: kept on the old static-header scheme deliberately — these
        // trading endpoints (/api/v1/orders, /accounts, /transfers,
        // /portfolio) were never confirmed against a real Postman capture
        // the way AMO's countries/onboarding endpoints were, so we don't
        // yet know whether this is even the same ViewTrade product/host as
        // AMO, let alone whether it uses the same UMA token exchange. Don't
        // change this without a confirmed request/response for it first.
        c.BaseAddress = ViewTradeBaseAddress(viewTradeBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(15);

        switch (viewTradeAuthScheme)
        {
            case "Bearer":
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", viewTradeApiKey);
                break;
            case "BasicKeySecret":
                var basic = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{viewTradeApiKey}:{viewTradeApiSecret}"));
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
                break;
            default: // ApiKeyHeader
                c.DefaultRequestHeaders.Add("X-Api-Key", viewTradeApiKey);
                break;
        }
    })
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(200), 4)))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .CircuitBreakerAsync(8, TimeSpan.FromSeconds(30)));

builder.Services.AddKeyedScoped<IBrokerageProvider, ViewTradeProvider>("viewtrade");

// UMA token exchange: mints the bearer token AMO requires (see
// ViewTradeAuthTokenProvider). Its own HttpClient is separate from the AMO
// ones below since it talks to a different host with a different contract.
builder.Services.AddHttpClient("ViewTradeUma", c =>
{
    c.BaseAddress = ViewTradeBaseAddress(viewTradeUmaBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IViewTradeAuthTokenProvider>(sp => new ViewTradeAuthTokenProvider(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("ViewTradeUma"),
    viewTradeApiKey,
    viewTradeApiSecret ?? "dev-only",
    sp.GetRequiredService<ILogger<ViewTradeAuthTokenProvider>>()));
builder.Services.AddTransient<ViewTradeAuthHandler>();

// Same UMA host as above, but WITH the auth handler attached — used for AMO
// calls whose canonical host happens to be UMA (currently just
// UserRegistration). Kept separate from the raw "ViewTradeUma" client so
// there's no circular dependency (that one is what MINTS the token; this
// one CONSUMES it).
builder.Services.AddHttpClient("ViewTradeUmaAuthenticated", c =>
    {
        c.BaseAddress = ViewTradeBaseAddress(viewTradeUmaBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(15);
    })
    .AddHttpMessageHandler<ViewTradeAuthHandler>();

// Onboarding: config-driven ViewTrade question resolution (see
// ViewTradeQuestionConfigResolver's class docs for the two identifiers that
// need special handling) and the answer-submission client. Both hit AMO —
// ViewTradeAuthHandler attaches a fresh UMA-issued bearer token to every
// request, so no static auth header is configured here.
builder.Services.AddSingleton(sp =>
    new ViewTradeQuestionConfigResolver(sp.GetRequiredService<ILogger<ViewTradeQuestionConfigResolver>>()));
builder.Services.AddSingleton(new ViewTradeFirmOptions(viewTradeFirmId, viewTradeBranchId));
// Form id (175 on this ViewTrade branch) and the verify-documents path: see ViewTradeOnboardingOptions.
builder.Services.AddSingleton(ViewTradeOnboardingOptions.FromEnvironment(Environment.GetEnvironmentVariable));
builder.Services.AddHttpClient<IViewTradeAnswerSubmitter, ViewTradeAnswerSubmitter>(c =>
    {
        c.BaseAddress = ViewTradeBaseAddress(viewTradeAmoBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(30); // file uploads can be slower than trading calls
    })
    .AddHttpMessageHandler<ViewTradeAuthHandler>()
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(200), 4)));

// Same AMO connection again for the country reference list used by the
// onboarding form's "country" type questions (see ViewTradeCountriesClient).
builder.Services.AddHttpClient<IViewTradeCountriesClient, ViewTradeCountriesClient>(c =>
    {
        c.BaseAddress = ViewTradeBaseAddress(viewTradeAmoBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(15);
    })
    .AddHttpMessageHandler<ViewTradeAuthHandler>()
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromMilliseconds(200), 4)));

// ---------- AuthN/AuthZ: RS256 JWTs issued by the Identity service ----------
// Brokerage previously had no user-facing endpoints (Kafka consumers + a dev
// ping route only) — this is its first authenticated HTTP surface.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Auth:Issuer"],
            ValidateAudience = true,
            ValidAudience = "growgift.api",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

// ---- Trading (buy / sell) ---------------------------------------------------
// SSO and Trading are separate ViewTrade hosts (ViewTrade-Staging-SSO /
// ViewTrade-Staging-Trading). Their base URLs are NOT in the repo or the
// Postman API (the environments' baseUrl values are local-only), so they are
// required config with no default — trading reports "not_configured" until set.
var viewTradeSsoBaseUrl = Environment.GetEnvironmentVariable("VIEWTRADE_SSO_BASE_URL")
    ?? builder.Configuration["ViewTrade:SsoBaseUrl"];
var viewTradeTradingBaseUrl = Environment.GetEnvironmentVariable("VIEWTRADE_TRADING_BASE_URL")
    ?? builder.Configuration["ViewTrade:TradingBaseUrl"];
builder.Services.AddSingleton(new ViewTradeTradingOptions(
    viewTradeSsoBaseUrl, viewTradeTradingBaseUrl, viewTradeFirmId, viewTradeBranchId,
    builder.Configuration.GetValue("ViewTrade:SessionCacheMinutes", 5),
    builder.Configuration.GetValue("ViewTrade:MaxOrderQuantity", 10_000)));
// No retry/circuit-breaker policies on these two clients on purpose: an automatic
// retry of a timed-out order or SSO call could double-place or double-establish.
builder.Services.AddHttpClient("ViewTradeSso", c =>
{
    if (!string.IsNullOrWhiteSpace(viewTradeSsoBaseUrl)) c.BaseAddress = ViewTradeBaseAddress(viewTradeSsoBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("ViewTradeTrading", c =>
{
    if (!string.IsNullOrWhiteSpace(viewTradeTradingBaseUrl)) c.BaseAddress = ViewTradeBaseAddress(viewTradeTradingBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(20);
});
// FX: gifts are AUD, ViewTrade trades USD. Source is configurable; FX_AUD_USD_RATE (or Fx:FixedRates:AUD)
// pins a rate for local testing. Default: Frankfurter (ECB-based, no key).
var fxFixed = new Dictionary<string, decimal>();
var fxAudEnv = Environment.GetEnvironmentVariable("FX_AUD_USD_RATE");
if (decimal.TryParse(fxAudEnv ?? builder.Configuration["Fx:FixedRates:AUD"], System.Globalization.NumberStyles.Number,
        System.Globalization.CultureInfo.InvariantCulture, out var fxAud) && fxAud > 0)
    fxFixed["AUD"] = fxAud;
var fxBase = Environment.GetEnvironmentVariable("FX_BASE_URL") ?? builder.Configuration["Fx:BaseUrl"] ?? "https://api.frankfurter.dev/";
builder.Services.AddSingleton(new GrowGift.Brokerage.Infrastructure.Fx.FxOptions(
    fxBase, builder.Configuration.GetValue("Fx:CacheMinutes", 10), fxFixed));
builder.Services.AddHttpClient("Fx", c =>
{
    c.BaseAddress = ViewTradeBaseAddress(fxBase);
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<GrowGift.Brokerage.Infrastructure.Fx.IFxRateProvider, GrowGift.Brokerage.Infrastructure.Fx.FxRateProvider>();
builder.Services.AddSingleton<IViewTradeUserDirectory, ViewTradeUserDirectory>();
builder.Services.AddSingleton<IViewTradeSessionService, ViewTradeSessionService>();
builder.Services.AddSingleton<IViewTradeOrderService, ViewTradeOrderService>();
builder.Services.AddSingleton<IViewTradePortfolioService, ViewTradePortfolioService>();
builder.Services.AddSingleton<IViewTradeOpenOrdersService, ViewTradeOpenOrdersService>();
builder.Services.AddScoped<GrowGift.Brokerage.Infrastructure.Redemption.RedemptionBuyer>();

// Existing-ViewTrade-user check before onboarding (gift redemption). The application lookup is the same
// typed AMO client the onboarding submitter uses, exposed through its narrow read-only interface.
builder.Services.AddTransient<IViewTradeApplicationLookup>(sp =>
    (ViewTradeAnswerSubmitter)sp.GetRequiredService<IViewTradeAnswerSubmitter>());
builder.Services.AddScoped<IExistingAccountStore, ExistingAccountStore>();
builder.Services.AddScoped<ExistingAccountResolver>();

builder.Services.AddAuthorizationBuilder()
    // Any authenticated recipient or parent may work through their own
    // application — no role restriction, since both personas onboard here.
    .AddPolicy("Applicant", p => p.RequireAuthenticatedUser())
    // Trading needs a KYC-approved, non-child user who already has a ViewTrade account.
    // TradingEndpoints re-checks and returns a specific reason; this is the coarse gate.
    .AddPolicy("Trader", p => p.RequireAuthenticatedUser().RequireClaim("viewtrade_account"));

// Kafka
builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        EnableIdempotence = true,
        Acks = Acks.All
    }).Build());
// Confluent consumers are NOT thread-safe: each hosted consumer gets its own
// instance from this factory rather than sharing a singleton.
builder.Services.AddSingleton<Func<IConsumer<string, string>>>(_ => () =>
    new ConsumerBuilder<string, string>(new ConsumerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        GroupId = "brokerage-service",
        EnableAutoCommit = false,
        AutoOffsetReset = AutoOffsetReset.Earliest
    }).Build());

builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher<BrokerageDbContext>>();
builder.Services.AddHostedService<OutboxRelayService<BrokerageDbContext>>();
// Buy-at-redemption model: the only Kafka trigger is gift redemption; no
// up-front acquisition happens when an order is paid.
builder.Services.AddHostedService<GiftRedeemedConsumer>();

builder.Services.AddHealthChecks().AddDbContextCheck<BrokerageDbContext>();

var app = builder.Build();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<BrokerageDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
}
app.MapHealthChecks("/healthz");

app.UseAuthentication();
app.UseAuthorization();
app.MapOnboardingEndpoints();
app.MapPortfolioEndpoints();
app.MapTradingEndpoints();
app.MapRedemptionEndpoints();

// DEVELOPMENT ONLY: hit ViewTrade directly and report exactly what comes
// back, so a new API key can be sanity-checked with one curl instead of
// running the full redeem flow. Set ViewTrade:PingPath if / isn't a valid
// route on their sandbox (e.g. "/api/v1/accounts").
if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/viewtrade/ping", async (
        GrowGift.Brokerage.Infrastructure.ViewTrade.ViewTradeProvider provider,
        IConfiguration config, CancellationToken ct) =>
    {
        var path = config["ViewTrade:PingPath"] ?? "/";
        try
        {
            var (status, body) = await provider.PingAsync(path, ct);
            return Results.Ok(new { requestedPath = path, status, body });
        }
        catch (Exception e)
        {
            return Results.Json(new { requestedPath = path, error = e.Message }, statusCode: 502);
        }
    });
}

app.Run();
