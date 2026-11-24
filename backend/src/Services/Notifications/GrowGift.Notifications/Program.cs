// Notifications service.
//
// Currently implements one real path: the buyer's receipt email after a
// gift order is paid, including the redemption code. That last part is why
// this is a direct internal HTTP call from Gifting's GiftOrderPaidConsumer
// (see that class's own doc) rather than a Kafka consumer on
// growgift.gifting.gift-order-paid.v1 as originally sketched — the
// plaintext code doesn't exist yet when that event is published (it's
// generated afterward, in the same step that also calls Fulfillment
// directly for the same reason: this codebase deliberately never lets the
// plaintext code sit in a message log, only ever handing it off
// synchronously, once, to whichever service needs it at that moment).
//
// Still scaffolded, not yet implemented: shipment-dispatched tracking
// emails/SMS, the gift-redeemed "buyer sees their gift was opened" moment,
// and task-reward-approved pushes — those don't carry anything as sensitive
// as the redemption code, so a normal Kafka consumer (matching
// GrowGift.Brokerage.Infrastructure.Consumers' pattern) is the right shape
// for them whenever they're built.
using SendGrid;
using SendGrid.Helpers.Mail;

var builder = WebApplication.CreateBuilder(args);

// No fallback to a placeholder key here (unlike some other services'
// "dev-only" defaults) — an email provider either has a real key or it
// doesn't, and a placeholder would just fail every send with a confusing
// SendGrid 401 instead of a clear startup error naming the actual problem.
var sendGridApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
    ?? builder.Configuration["SendGrid:ApiKey"]
    ?? throw new InvalidOperationException("SENDGRID_API_KEY is not configured.");
builder.Services.AddSingleton<ISendGridClient>(new SendGridClient(sendGridApiKey));

builder.Services.AddHealthChecks();
var app = builder.Build();

// A styled text wordmark rather than an <img> — there's no publicly
// hosted logo asset for this local/dev setup to reference (an <img src>
// pointing at localhost would just show a broken image in every email
// client, which is worse than no logo at all), and a text-based mark
// renders identically everywhere with zero external dependency. Declared
// here, before either endpoint below, since a top-level local can't be
// referenced from a lambda defined earlier in the file than its own
// declaration — even though that lambda only runs later, at request time.
const string GrowGiftWordmarkHtml =
    """
    <div style="text-align:center; margin-bottom:20px;">
      <span style="font-size:32px;">🌱</span>
      <p style="font-size:20px; font-weight:700; color:#2f6f4f; margin:4px 0 0; letter-spacing:1px;">GrowGift</p>
    </div>
    """;

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("Strict-Transport-Security", "max-age=63072000; includeSubDomains");
    await next();
});

app.MapHealthChecks("/healthz");

app.MapPost("/internal/v1/notifications/gift-order-receipt", async (
    GiftOrderReceiptRequest body, ISendGridClient sendGrid, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    // SendGrid requires the From address to be a verified sender/domain in
    // the account itself — this can't be just any address, and there's no
    // way to check that from here. If sends start failing with a SendGrid
    // "from address not verified" error, that's a Dashboard step (Settings
    // → Sender Authentication), not something to fix in this code.
    var fromEmail = config["SendGrid:FromAddress"] ?? "gifts@growgift.com.au";
    var fromName = config["SendGrid:FromName"] ?? "GrowGift";
    // The public site address shown in emails. Configurable (App:PublicBaseUrl /
    // env App__PublicBaseUrl) so staging can point elsewhere; defaults to the
    // production domain. Never hardcode a domain into email copy.
    var redeemUrl = $"{(config["App:PublicBaseUrl"] ?? "https://growgift.com.au").TrimEnd('/')}/redeem";

    // PostalFee and ServiceFee are mutually exclusive — see GiftOrder's own
    // doc — so at most one of them is non-null for any given order.
    var totalAmount = body.Amount + (body.BoxPrice ?? 0m) + (body.PostalFee ?? 0m) + (body.ServiceFee ?? 0m);
    var deliveryLine = body.BoxSku is not null
        ? $" + {body.BoxSku} gift box"
        : body.ServiceFee is not null ? " (delivered by email)" : "";
    var plainText =
        $"""
         Your gift is on its way!

         {body.Symbol} — ${body.Amount:0.00} {body.Currency}{deliveryLine}
         For: {body.RecipientName}
         Total charged: ${totalAmount:0.00} {body.Currency}

         Redemption code: {body.RedemptionCode}

         Share this code (or the QR code on the printed card, once it arrives)
         with {body.RecipientName} — that's all they need to claim their shares
         at {redeemUrl}.

         — GrowGift
         """;

    var htmlContent =
        $"""
         <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
           {GrowGiftWordmarkHtml}
           <h1 style="font-size: 28px;">Your gift is on its way! 🎁</h1>
           <p><strong>{body.Symbol}</strong> — ${body.Amount:0.00} {body.Currency}{deliveryLine}<br/>
              For: {body.RecipientName}<br/>
              Total charged: ${totalAmount:0.00} {body.Currency}</p>
           <div style="background:#f4f4f4; border-radius:12px; padding:20px; text-align:center; margin:24px 0;">
             <p style="font-size:13px; color:#666; margin:0 0 8px;">Redemption code</p>
             <p style="font-family: monospace; font-size:24px; letter-spacing:2px; margin:0;">{body.RedemptionCode}</p>
           </div>
           <p>Share this with {body.RecipientName} — that's all they need to claim their shares.
              {(body.ServiceFee is null ? "A printed copy is also on its way in the mail." : "We've also emailed this code directly to them.")}</p>
           <p style="color:#999; font-size:12px;">— GrowGift</p>
         </div>
         """;

    var message = MailHelper.CreateSingleEmail(
        from: new EmailAddress(fromEmail, fromName),
        to: new EmailAddress(body.BuyerEmail),
        subject: $"Your GrowGift receipt — {body.Symbol} for {body.RecipientName}",
        plainTextContent: plainText,
        htmlContent: htmlContent);

    var response = await sendGrid.SendEmailAsync(message, ct);
    if ((int)response.StatusCode >= 400)
    {
        var responseBody = await response.Body.ReadAsStringAsync(ct);
        logger.LogError("SendGrid rejected the receipt email for order {OrderId}: {Status} {Body}",
            body.OrderId, response.StatusCode, responseBody);
        return Results.Problem(statusCode: 502, detail: "Email provider rejected the request.");
    }

    logger.LogInformation("Sent receipt email for order {OrderId} to {Email}", body.OrderId, body.BuyerEmail);
    return Results.Ok();
});

// Email-delivery orders only: the recipient's own copy of the code,
// separate from the buyer's receipt above (different audience, different
// tone — "you've received a gift" rather than "your purchase went
// through"). This is the ONLY copy the recipient gets for an email-delivery
// order, since Fulfillment is skipped entirely for these (see
// GiftOrderPaidConsumer) — no printed card is ever produced.
app.MapPost("/internal/v1/notifications/gift-arrived", async (
    GiftArrivedRequest body, ISendGridClient sendGrid, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    var fromEmail = config["SendGrid:FromAddress"] ?? "gifts@growgift.com.au";
    var fromName = config["SendGrid:FromName"] ?? "GrowGift";
    // The public site address shown in emails. Configurable (App:PublicBaseUrl /
    // env App__PublicBaseUrl) so staging can point elsewhere; defaults to the
    // production domain. Never hardcode a domain into email copy.
    var redeemUrl = $"{(config["App:PublicBaseUrl"] ?? "https://growgift.com.au").TrimEnd('/')}/redeem";

    var messageBlock = string.IsNullOrWhiteSpace(body.GreetingMessage) ? "" : $"\n\"{body.GreetingMessage}\"\n";
    var plainText =
        $"""
         You've received a gift! 🎁

         {body.Symbol} — ${body.Amount:0.00} {body.Currency}
         {messageBlock}
         Redemption code: {body.RedemptionCode}

         Head to {redeemUrl} and enter this code to claim your shares.

         — GrowGift
         """;

    var messageHtml = string.IsNullOrWhiteSpace(body.GreetingMessage)
        ? ""
        : $"""<p style="font-style:italic; color:#444; border-left:3px solid #2f6f4f; padding-left:12px; margin:20px 0;">"{body.GreetingMessage}"</p>""";

    var htmlContent =
        $"""
         <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
           {GrowGiftWordmarkHtml}
           <h1 style="font-size: 28px;">You've received a gift! 🎁</h1>
           <p><strong>{body.Symbol}</strong> — ${body.Amount:0.00} {body.Currency}</p>
           {messageHtml}
           <div style="background:#f4f4f4; border-radius:12px; padding:20px; text-align:center; margin:24px 0;">
             <p style="font-size:13px; color:#666; margin:0 0 8px;">Redemption code</p>
             <p style="font-family: monospace; font-size:24px; letter-spacing:2px; margin:0;">{body.RedemptionCode}</p>
           </div>
           <p>Head to {redeemUrl} and enter this code to claim your shares.</p>
           <p style="color:#999; font-size:12px;">— GrowGift</p>
         </div>
         """;

    var message = MailHelper.CreateSingleEmail(
        from: new EmailAddress(fromEmail, fromName),
        to: new EmailAddress(body.RecipientEmail),
        subject: $"You've received a gift — {body.Symbol}",
        plainTextContent: plainText,
        htmlContent: htmlContent);

    var response = await sendGrid.SendEmailAsync(message, ct);
    if ((int)response.StatusCode >= 400)
    {
        var responseBody = await response.Body.ReadAsStringAsync(ct);
        logger.LogError("SendGrid rejected the gift-arrived email for order {OrderId}: {Status} {Body}",
            body.OrderId, response.StatusCode, responseBody);
        return Results.Problem(statusCode: 502, detail: "Email provider rejected the request.");
    }

    logger.LogInformation("Sent gift-arrived email for order {OrderId} to {Email}", body.OrderId, body.RecipientEmail);
    return Results.Ok();
});

// Account email-verification link, sent by Identity at signup.
app.MapPost("/internal/v1/notifications/verify-email", async (
    VerifyEmailRequest body, ISendGridClient sendGrid, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    var fromEmail = config["SendGrid:FromAddress"] ?? "gifts@growgift.com.au";
    var fromName = config["SendGrid:FromName"] ?? "GrowGift";

    var plainText =
        $"""
         Welcome to GrowGift!

         Confirm your email address by opening this link:
         {body.VerificationUrl}

         If you didn't create an account, you can ignore this email.

         — GrowGift
         """;
    var htmlContent =
        $"""
         <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
           {GrowGiftWordmarkHtml}
           <h1 style="font-size: 28px;">Confirm your email</h1>
           <p>Welcome to GrowGift! Tap the button below to confirm it's really you.</p>
           <p style="text-align:center; margin:28px 0;">
             <a href="{body.VerificationUrl}" style="background:#2f6f4f; color:#fff; padding:14px 28px; border-radius:999px; text-decoration:none; font-weight:700;">Verify my email</a>
           </p>
           <p style="font-size:12px; color:#666;">Or paste this link into your browser:<br/>{body.VerificationUrl}</p>
           <p style="color:#999; font-size:12px;">If you didn't create an account, you can ignore this email.</p>
         </div>
         """;

    var message = MailHelper.CreateSingleEmail(
        from: new EmailAddress(fromEmail, fromName),
        to: new EmailAddress(body.Email),
        subject: "Confirm your GrowGift email",
        plainTextContent: plainText,
        htmlContent: htmlContent);

    var response = await sendGrid.SendEmailAsync(message, ct);
    if ((int)response.StatusCode >= 400)
    {
        var responseBody = await response.Body.ReadAsStringAsync(ct);
        logger.LogError("SendGrid rejected the verification email: {Status} {Body}", response.StatusCode, responseBody);
        return Results.Problem(statusCode: 502, detail: "Email provider rejected the request.");
    }

    logger.LogInformation("Sent verification email to {Email}", body.Email);
    return Results.Ok();
});

// Courier progress emails (Fulfillment → here). One endpoint, four kinds; Fulfillment sends each kind at most once
// per shipment and passes a stable idempotency key, which this service also de-duplicates for a day so a
// retried call after a timeout can't double-send. (In-process cache: fine for one instance; use a shared store
// if Notifications is ever scaled out.) No delivery address is ever included — only the courier and tracking link.
var sentShipmentEmails = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
app.MapPost("/internal/v1/notifications/shipment-update", async (
    ShipmentUpdateRequest body, ISendGridClient sendGrid, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.BuyerEmail) || string.IsNullOrWhiteSpace(body.IdempotencyKey)) return Results.BadRequest();
    foreach (var kv in sentShipmentEmails) if (DateTimeOffset.UtcNow - kv.Value > TimeSpan.FromDays(1)) sentShipmentEmails.TryRemove(kv.Key, out _);
    if (sentShipmentEmails.ContainsKey(body.IdempotencyKey)) return Results.Ok();

    var (subject, headline, line) = body.Kind switch
    {
        "booked" => ("Your GrowGift box is ready to ship", "Your gift box is packed", "The courier has been booked and will collect your parcel soon."),
        "intransit" => ("Your GrowGift box is on its way", "On its way", "Your parcel has been picked up and is on its way."),
        "delivered" => ("Your GrowGift box was delivered", "Delivered", "Your parcel has been delivered."),
        "exception" => ("An update on your GrowGift delivery", "A delivery hiccup", "The courier reported a problem with your delivery. We're keeping an eye on it and will help if it needs action."),
        _ => ("", "", "")
    };
    if (subject.Length == 0) return Results.BadRequest();

    var fromEmail = config["SendGrid:FromAddress"] ?? "gifts@growgift.com.au";
    var fromName = config["SendGrid:FromName"] ?? "GrowGift";
    static string enc(string s) => System.Net.WebUtility.HtmlEncode(s) ?? "";
    var safeUrl = Uri.TryCreate(body.TrackingUrl, UriKind.Absolute, out var tu) && tu.Scheme == Uri.UriSchemeHttps ? tu.ToString() : null;
    var trackingText = body.TrackingNumber is null ? "" : $"\nTracking number: {body.TrackingNumber}{(safeUrl is null ? "" : $"\nTrack it: {safeUrl}")}";
    var trackingHtml = body.TrackingNumber is null ? "" :
        $"<p>Tracking number: <strong>{enc(body.TrackingNumber)}</strong>{(body.CourierName is null ? "" : $" ({enc(body.CourierName)})")}</p>" +
        (safeUrl is null ? "" : $"<p><a href=\"{enc(safeUrl)}\">Track your parcel</a></p>");

    var message = MailHelper.CreateSingleEmail(
        from: new EmailAddress(fromEmail, fromName),
        to: new EmailAddress(body.BuyerEmail),
        subject: subject,
        plainTextContent: $"{headline}\n\n{line}{trackingText}\n\n— GrowGift",
        htmlContent: $"""
            <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
              {GrowGiftWordmarkHtml}
              <h1 style="font-size: 28px;">{enc(headline)}</h1>
              <p>{enc(line)}</p>
              {trackingHtml}
              <p style="color:#999; font-size:12px;">— GrowGift</p>
            </div>
            """);

    var response = await sendGrid.SendEmailAsync(message, ct);
    if ((int)response.StatusCode >= 400)
    {
        // Status only — the provider body can echo the recipient address.
        logger.LogError("SendGrid rejected a shipment email for order {OrderId}: {Status}", body.OrderId, response.StatusCode);
        return Results.Problem(statusCode: 502, detail: "Email provider rejected the request.");
    }
    sentShipmentEmails[body.IdempotencyKey] = DateTimeOffset.UtcNow;
    logger.LogInformation("Sent shipment {Kind} email for order {OrderId}", body.Kind, body.OrderId);
    return Results.Ok();
});

app.Run();

/// <summary>
/// Everything needed to render and send the receipt in one call — deliberately
/// self-contained rather than requiring Notifications to look anything up
/// itself (it has no database, and no reason to ever store this).
/// </summary>
public sealed record GiftOrderReceiptRequest(
    Guid OrderId, string BuyerEmail, string RecipientName, string Symbol,
    decimal Amount, string Currency, string? BoxSku, decimal? BoxPrice, decimal? PostalFee, decimal? ServiceFee,
    string RedemptionCode);

public sealed record GiftArrivedRequest(
    Guid OrderId, string RecipientEmail, string Symbol, decimal Amount, string Currency, string RedemptionCode,
    string? GreetingMessage);

public sealed record VerifyEmailRequest(string Email, string VerificationUrl);

public sealed record ShipmentUpdateRequest(
    Guid ShipmentId, Guid OrderId, string BuyerEmail, string? RecipientName, string Kind,
    string? CourierName, string? TrackingNumber, string? TrackingUrl, string IdempotencyKey);
