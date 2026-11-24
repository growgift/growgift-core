using Microsoft.EntityFrameworkCore.Infrastructure;
using Confluent.Kafka;
using GrowGift.Identity.Api;
using GrowGift.Identity.Billing;
using GrowGift.Identity.Infrastructure;
using GrowGift.Identity.Infrastructure.Consumers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<IdentityDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("identity"),
        npgsql => npgsql.EnableRetryOnFailure(3)));
// Billing: every setting comes from the environment (see docs/CHANGES-latest.md). Unset = billing off, nothing breaks.
var billingOptions = BillingOptions.FromEnvironment(Environment.GetEnvironmentVariable, builder.Configuration["App:WebBaseUrl"]);
builder.Services.AddSingleton(billingOptions);
builder.Services.AddSingleton<IStripeBilling, StripeBilling>();
builder.Services.AddSingleton<TokenService>();
// Used to hand the account-verification email to the Notifications service
// (which owns SendGrid) — Identity never talks to the email provider itself.
builder.Services.AddHttpClient("notifications", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:NotificationsBaseUrl"] ?? "http://localhost:5007");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHealthChecks().AddDbContextCheck<IdentityDbContext>();

// AuthN/AuthZ: endpoints below use .RequireAuthorization(...), but nothing
// previously registered an authentication scheme or the JwtBearer package
// at all — every authorized request failed with "contains authorization
// metadata, but a middleware was not found that supports authorization"
// regardless of which endpoint or how valid the token was. Identity issues
// these RS256 tokens itself (see TokenService) and also exposes its own
// JWKS at /.well-known/jwks.json, so — same as every other service that
// validates tokens Identity issued — Authority here just points at itself.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:SelfUrl"];
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

builder.Services.AddAuthorizationBuilder()
    // "Buyer" = any authenticated user (see IdentityEndpoints' own comment)
    // — no role restriction, since address-on-file and account-info reads
    // apply the same way to every signed-in user.
    .AddPolicy("Buyer", p => p.RequireAuthenticatedUser());

// Kafka: Identity's first consumer (previously none). Confluent consumers
// are not thread-safe, so each hosted consumer gets its own instance from
// this factory rather than sharing a singleton — same pattern as Brokerage.
builder.Services.AddSingleton<Func<IConsumer<string, string>>>(_ => () =>
    new ConsumerBuilder<string, string>(new ConsumerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        GroupId = "identity-service",
        EnableAutoCommit = false,
        AutoOffsetReset = AutoOffsetReset.Earliest
    }).Build());
builder.Services.AddHostedService<AccountOpenedConsumer>();

var app = builder.Build();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("Strict-Transport-Security", "max-age=63072000; includeSubDomains");
    await next();
});

// Order matters: authentication (who is this?) must run before
// authorization (are they allowed?), and both must run before the
// endpoints that declare .RequireAuthorization(...) are actually invoked.
app.UseAuthentication();
app.UseAuthorization();

IdentityEndpoints.Map(app);
BillingEndpoints.Map(app);
app.MapHealthChecks("/healthz");
app.Run();
