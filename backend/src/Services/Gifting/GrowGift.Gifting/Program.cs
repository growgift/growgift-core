using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using FluentValidation;
using GrowGift.Gifting.Api;
using GrowGift.Gifting.Infrastructure.Auditing;
using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Payments;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Auditing;
using GrowGift.Infrastructure.Common.Idempotency;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Stripe.net reads this static key for every SDK call (Checkout Sessions,
// webhook construction uses its own secret separately). Sandbox keys start
// with sk_test_; never a live key outside production secrets management.
Stripe.StripeConfiguration.ApiKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")
    ?? builder.Configuration["Stripe:SecretKey"]
    ?? throw new InvalidOperationException("STRIPE_SECRET_KEY is not configured.");

// ---------- Persistence ----------
builder.Services.AddDbContext<GiftingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("gifting"),
        npgsql => npgsql.EnableRetryOnFailure(3)));

// ---------- Redis / idempotency ----------
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("redis")!));
builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
builder.Services.AddSingleton<GrowGift.Gifting.Infrastructure.Codes.IRevealedCodeCache,
    GrowGift.Gifting.Infrastructure.Codes.RedisRevealedCodeCache>();

// ---------- Kafka outbox ----------
builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        EnableIdempotence = true,
        Acks = Acks.All
    }).Build());
builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher<GiftingDbContext>>();
builder.Services.AddHostedService<OutboxRelayService<GiftingDbContext>>();
builder.Services.AddSingleton<Confluent.Kafka.IConsumer<string, string>>(_ =>
    new Confluent.Kafka.ConsumerBuilder<string, string>(new Confluent.Kafka.ConsumerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        GroupId = "gifting-service",
        EnableAutoCommit = false,
        AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest
    }).Build());
builder.Services.AddHostedService<GrowGift.Gifting.Infrastructure.Consumers.GiftOrderPaidConsumer>();
builder.Services.AddHttpClient("fulfillment", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:FulfillmentBaseUrl"] ?? "http://localhost:5006"));
builder.Services.AddHttpClient("notifications", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:NotificationsBaseUrl"] ?? "http://localhost:5007"));

// ---------- Application ----------
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddSingleton<ICodeSaltProvider, EnvironmentCodeSaltProvider>();
builder.Services.AddSingleton<IStripeWebhookVerifier, StripeWebhookVerifier>();
builder.Services.AddScoped<IAuditWriter, PostgresAuditWriter>();

// ---------- AuthN/AuthZ: RS256 JWTs issued by the Identity service ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"];
        // Local dev talks to Identity over plain HTTP; only production
        // requires the Authority to be HTTPS for metadata fetches.
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

// A child's login is read-only by design (see Identity's User doc): it may
// never buy, redeem, or otherwise mutate anything in Gifting. "Buyer" used
// to mean any authenticated user, which silently included role=child.
static bool IsChild(System.Security.Claims.ClaimsPrincipal u) =>
    u.Claims.Any(c => (c.Type == "role" || c.Type == System.Security.Claims.ClaimTypes.Role)
                      && c.Value.Equals("child", StringComparison.OrdinalIgnoreCase));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Buyer", p => p.RequireAuthenticatedUser().RequireAssertion(ctx => !IsChild(ctx.User)))
    .AddPolicy("KycApproved", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => !IsChild(ctx.User))
        .RequireClaim("kyc_status", "approved")
        .RequireClaim("viewtrade_account"));

// ---------- Observability ----------
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation());

// Without this, enum fields bound from a request body (like
// CreateOrderRequest.DeliveryMethod) only accept their raw numeric value
// (0/1) by default — sending "Physical"/"Email" as the frontend does fails
// to deserialize entirely, before any validation even runs, surfacing as a
// bare 400 with no useful error body. This makes string enum values work
// for any request DTO, present or future, not just this one field.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddDbContextCheck<GiftingDbContext>();

var app = builder.Build();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<GiftingDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
}

// ---------- Security headers ----------
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "no-referrer");
    ctx.Response.Headers.Append("Strict-Transport-Security", "max-age=63072000; includeSubDomains; preload");
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapGiftingEndpoints();
app.MapHealthChecks("/healthz");

app.Run();
