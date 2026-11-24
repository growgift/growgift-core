using GrowGift.Fulfillment.Api;
using GrowGift.Fulfillment.Shipping;
using GrowGift.Fulfillment.Shipping.Ef;
using GrowGift.Fulfillment.Shipping.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<FulfillmentDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("fulfillment")));
builder.Services.AddHttpClient("shipbob", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Shipbob:BaseUrl"] ?? "https://api.shipbob.com");
    c.DefaultRequestHeaders.Add("Authorization",
        $"Bearer {Environment.GetEnvironmentVariable("SHIPBOB_TOKEN") ?? "dev-only"}");
});
// ---------- Shippit (STAGING ONLY) ----------
// The secret comes from the environment (SHIPPIT_API_SECRET) and is never written to a file, log or response.
// ShippitGuard refuses anything but https://app.staging.shippit.com, and the service refuses to start if the
// configuration points anywhere else — production use cannot happen by accident.
var shippitOptions = new ShippitOptions();
builder.Configuration.GetSection("Shippit").Bind(shippitOptions);
// Shippit issues one API key per merchant; it is sent as the Bearer secret. Either name works.
shippitOptions.ApiSecret = Environment.GetEnvironmentVariable("SHIPPIT_API_SECRET") ?? Environment.GetEnvironmentVariable("SHIPPIT_API_KEY");
// Base URL can come from the environment, but it still has to pass ShippitGuard below (staging host only).
shippitOptions.BaseUrl = Environment.GetEnvironmentVariable("SHIPPIT_BASE_URL") ?? shippitOptions.BaseUrl;
shippitOptions.WebhookToken = Environment.GetEnvironmentVariable("SHIPPIT_WEBHOOK_TOKEN") ?? shippitOptions.WebhookToken;
shippitOptions.WebhookUrl = Environment.GetEnvironmentVariable("SHIPPIT_WEBHOOK_URL") ?? shippitOptions.WebhookUrl;
ShippitGuard.EnsureStaging(shippitOptions);
builder.Services.AddSingleton(shippitOptions);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHttpClient<IShippitClient, ShippitClient>((sp, c) => ShippitClient.Configure(c, sp.GetRequiredService<ShippitOptions>()))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("identity", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:IdentityBaseUrl"] ?? "http://localhost:5001");
    c.Timeout = TimeSpan.FromSeconds(10);
    var key = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_KEY") ?? builder.Configuration["Internal:ServiceKey"];
    if (!string.IsNullOrEmpty(key)) c.DefaultRequestHeaders.Add("X-Internal-Key", key);
});
builder.Services.AddHttpClient("notifications", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:NotificationsBaseUrl"] ?? "http://localhost:5007");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IShipmentStore, EfShipmentStore>();
builder.Services.AddScoped<IAddressSource, IdentityAddressSource>();
builder.Services.AddScoped<IShipmentNotifier, HttpShipmentNotifier>();
builder.Services.AddScoped<ShipmentProcessor>();
builder.Services.AddScoped<ShipmentIntake>();
builder.Services.AddScoped<ShippitWebhookHandler>();
builder.Services.AddHostedService<ShipmentWorker>();
builder.Services.AddHostedService<ShippitWebhookRegistrar>();

// ---------- AuthN/AuthZ: RS256 JWTs from Identity (same setup as the other services) ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;      // keep "role"/"sub" as issued (see Family's note on this)
        o.Authority = builder.Configuration["Auth:Authority"];
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = builder.Configuration["Auth:Issuer"],
            ValidateAudience = true, ValidAudience = "growgift.api",
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Authenticated", p => p.RequireAuthenticatedUser())
    .AddPolicy("ShippingAdmin", p => p.RequireAuthenticatedUser().RequireClaim("role", "admin"));

builder.Services.AddHealthChecks();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Development only: create this service's tables if EF migrations haven't
// been applied. EnsureCreated() can't be used here — it no-ops when the
// database already contains ANY tables, and init.sql pre-creates audit_log.
// Production applies real migrations in the deploy pipeline instead.
if (app.Environment.IsDevelopment())
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
    try
    {
        bootstrapDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTables();
    }
    catch (Npgsql.PostgresException e) when (e.SqlState == "42P07")
    {
        // Tables already exist — nothing to do.
    }
    // CreateTables() above does nothing useful on a database that already has other tables, so make sure the
    // shipping tables exist too. Idempotent; the same SQL ships as infra/sql/fulfillment_shipping.sql for deploys.
    try { bootstrapDb.Database.ExecuteSqlRaw(ShippingSchema.Sql); }
    catch (Exception e)
    {
        // Don't take the whole service down in Development: say what to run by hand instead.
        app.Logger.LogError("Could not create the shipping tables automatically ({Type}). Run infra/sql/fulfillment_shipping.sql as a database owner.", e.GetType().Name);
    }
}

// Internal-only: called by Gifting over mTLS with the plaintext code for card
// printing. The code is sent straight to the print vendor payload and stored
// nowhere; only a masked preview (GG-84••-••••-••8D) is kept for support.
app.MapPost("/internal/v1/fulfillment-orders", async (
    CreateFulfillmentOrder body, FulfillmentDbContext db, ShipmentIntake shipments,
    IHttpClientFactory httpFactory, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    // Courier shipment job. Payment is confirmed upstream (Gifting only calls this from its order-paid consumer);
    // we also require the flag so a malformed caller can't create a shipment for an unpaid or digital order.
    // Only a durable job is created here — Shippit is called later by the worker, so a Shippit outage never
    // fails (or delays) code issuance, gift redemption or brokerage.
    IntakeResult? intake = null;
    if (body.BuyerId is { } buyerId)
    {
        intake = await shipments.RegisterAsync(new ShipmentRequest(body.OrderId, body.GiftId, buyerId, body.BuyerEmail,
            body.RecipientName, body.BoxSku, body.DeliveryMethod ?? "Physical", body.PaymentConfirmed), ct);
        if (intake.Status is IntakeStatus.NotPhysical or IntakeStatus.PaymentNotConfirmed or IntakeStatus.Invalid)
        {
            logger.LogWarning("Shipment not created for order {OrderId}: {Reason}", body.OrderId, intake.Status);
            return Results.BadRequest(new { error = intake.Status.ToString() });
        }
    }

    var existing = await db.Orders.AnyAsync(o => o.GiftId == body.GiftId, ct);
    if (existing) return Results.Ok(); // idempotent replay (the shipment above is idempotent too)

    var masked = MaskCode(body.PlaintextCode);
    var order = new FulfillmentOrder
    {
        Id = Guid.NewGuid(),
        OrderId = body.OrderId,
        GiftId = body.GiftId,
        MaskedCode = masked,
        Status = "PendingPrint",
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
    db.Orders.Add(order);
    await db.SaveChangesAsync(ct);

    // Encodes the same redemption URL a recipient would type in by hand —
    // the QR is purely a convenience for skipping manual entry, not a
    // separate credential. Generated fresh here and handed straight to the
    // print payload; never persisted, matching the plaintext code's own
    // "exists only in memory, only once" handling (see GiftOrderPaidConsumer's
    // doc) — a QR image is just another rendering of that same plaintext,
    // so it carries the same sensitivity and the same rule applies.
    var frontendUrl = (config["Frontend:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    var redemptionUrl = $"{frontendUrl}/redeem?code={Uri.EscapeDataString(body.PlaintextCode)}";
    var qrPngBytes = GenerateQrPng(redemptionUrl);

    // Print/3PL hand-off (card with QR + code) is separate from courier booking and is still a placeholder.
    // var shipbob = httpFactory.CreateClient("shipbob");
    // await shipbob.PostAsJsonAsync("/orders", new { ..., insertImage = Convert.ToBase64String(qrPngBytes) }, ct);

    return Results.Created($"/internal/v1/fulfillment-orders/{order.Id}", new { fulfillmentOrderId = order.Id, qrGenerated = qrPngBytes.Length > 0, shipmentId = intake?.ShipmentId });
});

// Customer shipment status/tracking, the Shippit webhook and operator tools: see Api/ShippingEndpoints.cs.
// (The old unauthenticated GET /api/v1/shipments/{orderId} was replaced by an owner-checked one there.)
app.MapShippingEndpoints();

app.MapHealthChecks("/healthz");

// Dev-only: lets you visually confirm QR generation actually works without
// a real Shipbob account — open this URL in a browser to see the PNG. Not
// mapped outside Development since it would otherwise let anyone generate
// a QR for an arbitrary string with no auth check.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/internal/v1/dev/qr-preview", (string text) =>
        Results.File(GenerateQrPng(text), "image/png"));
}

app.Run();

static string MaskCode(string code)
{
    var chars = code.ToCharArray();
    for (var i = 0; i < chars.Length; i++)
        if (chars[i] != '-' && i > 4 && i < chars.Length - 2)
            chars[i] = '•';
    return new string(chars);
}

// PngByteQRCode (not the Bitmap-based QRCode class) deliberately avoids any
// dependency on System.Drawing.Common, which has been Windows-only since
// .NET 6 — this runs in a Linux container, so a Bitmap-based renderer would
// throw at runtime despite compiling fine locally on a Windows dev machine.
static byte[] GenerateQrPng(string payload)
{
    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
    using var png = new PngByteQRCode(data);
    return png.GetGraphic(20);
}

public sealed record CreateFulfillmentOrder(
    Guid OrderId, Guid GiftId, string PlaintextCode, DateTimeOffset ExpiresAtUtc,
    // Shipping fields — optional so an older Gifting build still works (it simply creates no courier shipment).
    Guid? BuyerId = null, string? BuyerEmail = null, string? RecipientName = null, string? BoxSku = null,
    string? DeliveryMethod = null, bool PaymentConfirmed = false);

public sealed class FulfillmentOrder
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid GiftId { get; set; }
    public string MaskedCode { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    public DbSet<FulfillmentOrder> Orders => Set<FulfillmentOrder>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentEvent> ShipmentEvents => Set<ShipmentEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("fulfillment");
        b.Entity<Shipment>(e =>
        {
            e.ToTable("shipments");
            e.HasKey(x => x.Id);
            e.Property(x => x.RowVersion).IsConcurrencyToken();   // bumped by EfShipmentStore.SaveAsync
            e.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_shipments_order");
            e.HasIndex(x => x.Reference).IsUnique().HasDatabaseName("ux_shipments_reference");
            e.HasIndex(x => x.TrackingNumber).IsUnique().HasFilter("\"TrackingNumber\" IS NOT NULL").HasDatabaseName("ux_shipments_tracking");
            e.HasIndex(x => x.BuyerId).HasDatabaseName("ix_shipments_buyer");
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.PendingNotifications);
        });
        b.Entity<ShipmentEvent>(e =>
        {
            e.ToTable("shipment_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ShipmentId, x.EventKey }).IsUnique().HasDatabaseName("ux_shipment_events_key");
        });
        b.Entity<FulfillmentOrder>(e =>
        {
            e.ToTable("fulfillment_orders");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GiftId).IsUnique();
            e.HasIndex(x => x.OrderId);
        });
    }
}
