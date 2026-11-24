using Microsoft.EntityFrameworkCore.Infrastructure;
using Confluent.Kafka;
using FluentValidation;
using GrowGift.Family.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FamilyDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("family"),
        npgsql => npgsql.EnableRetryOnFailure(3)));

builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        EnableIdempotence = true,
        Acks = Acks.All
    }).Build());
builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher<FamilyDbContext>>();
builder.Services.AddHostedService<OutboxRelayService<FamilyDbContext>>();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddHttpClient("identity", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:IdentityBaseUrl"] ?? "http://localhost:5001"));
builder.Services.AddHttpClient("gifting", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:GiftingBaseUrl"] ?? "http://localhost:5003"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Without this, the JWT bearer handler silently remaps well-known
        // short claim types (role, nameid, email, ...) to their legacy
        // long-form XML-namespace URIs on the way in — confirmed via
        // diagnostic logging: the token's "role" claim arrived here as
        // "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        // not "role". RequireClaim("role", "parent") below checks the
        // short name specifically, so every "Parent"/"ChildReadOnly" policy
        // check failed with 403 regardless of the actual role value —
        // looking completely correct in a debugger view of the token, and
        // completely broken to RequireClaim. Other services here don't hit
        // this because they only check custom claim names (kyc_status,
        // viewtrade_account, has_shipping_address), which aren't in that
        // legacy mapping table and pass through unchanged either way.
        o.MapInboundClaims = false;
        o.Authority = builder.Configuration["Auth:Authority"];
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Auth:Issuer"],
            ValidAudience = "growgift.api",
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorizationBuilder()
    // Deliberately no kyc_status requirement here — building a family tree
    // (adding children, linking a partner) is just organizational data and
    // doesn't need a brokerage account to exist yet. KYC is enforced where
    // it actually matters: at redemption time, via Gifting's own
    // "KycApproved" policy on POST /gifts/redeem. Requiring it here too
    // meant a brand-new parent couldn't even create their first child
    // profile before finishing broker onboarding — backwards from the
    // intended flow (build the tree, pick a recipient, then onboard).
    .AddPolicy("Parent", p => p.RequireAuthenticatedUser().RequireClaim("role", "parent"))
    // Children authenticate but hold no mutating scope anywhere in the API surface.
    .AddPolicy("ChildReadOnly", p => p.RequireAuthenticatedUser().RequireClaim("role", "child", "parent"));

builder.Services.AddHealthChecks().AddDbContextCheck<FamilyDbContext>();

var app = builder.Build();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<FamilyDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
}
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/healthz");
GrowGift.Family.Api.FamilyEndpoints.Map(app);
app.Run();
