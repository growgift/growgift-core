using System.Security.Claims;
using GrowGift.Fulfillment.Shipping;

namespace GrowGift.Fulfillment.Api;

public static class ShippingEndpoints
{
    public static void MapShippingEndpoints(this WebApplication app)
    {
        // ---- Customer: only the buyer who owns the order ----
        var mine = app.MapGroup("/api/v1/shipments").RequireAuthorization("Authenticated");

        mine.MapGet("/", async (ClaimsPrincipal user, IShipmentStore store, CancellationToken ct) =>
        {
            if (UserId(user) is not { } uid) return Results.Unauthorized();
            var list = await store.ListByBuyerAsync(uid, ct);
            return Results.Ok(list.Select(s => ShipmentView.ForCustomer(s, Array.Empty<ShipmentEvent>())));
        });

        mine.MapGet("/{orderId:guid}", async (Guid orderId, ClaimsPrincipal user, IShipmentStore store, CancellationToken ct) =>
        {
            if (UserId(user) is not { } uid) return Results.Unauthorized();
            var s = await store.GetByOrderAsync(orderId, ct);
            // Someone else's order looks exactly like a missing one.
            if (s is null || s.BuyerId != uid) return Results.NotFound();
            return Results.Ok(ShipmentView.ForCustomer(s, await store.ListEventsAsync(s.Id, ct)));
        });

        // ---- Shippit webhook: authenticated by the shared header token, not a user ----
        app.MapPost("/api/v1/shipping/webhooks/shippit", async (HttpRequest req, ShippitWebhookHandler handler, CancellationToken ct) =>
        {
            if (req.ContentLength is > ShippitWebhookHandler.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var ms = new MemoryStream();
            var buf = new byte[8192]; int n;
            while ((n = await req.Body.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > ShippitWebhookHandler.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var token = req.Headers[ShippitOptions.WebhookHeaderName].ToString();
            var result = await handler.HandleAsync(string.IsNullOrEmpty(token) ? null : token, ms.ToArray(), ct);
            return result switch
            {
                WebhookResult.Accepted or WebhookResult.Duplicate or WebhookResult.Unmatched => Results.Ok(),   // 200 = what Shippit expects
                WebhookResult.Unauthorized => Results.Unauthorized(),
                WebhookResult.NotConfigured => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                WebhookResult.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                _ => Results.BadRequest()
            };
        }).AllowAnonymous();

        // ---- Operators only (role=admin): labels, manifests, recovery ----
        var admin = app.MapGroup("/api/v1/shipping/admin").RequireAuthorization("ShippingAdmin");

        admin.MapGet("/shipments/{id:guid}", async (Guid id, IShipmentStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } s ? Results.Ok(ShipmentView.ForAdmin(s)) : Results.NotFound());

        // Label = a courier document containing the delivery address, so it is never a customer endpoint.
        // Pre-signed URLs last 7 days; refresh ours after 6.
        admin.MapGet("/shipments/{id:guid}/label", async (Guid id, ClaimsPrincipal user, IShipmentStore store, IShippitClient shippit,
            IClock clock, ILoggerFactory lf, CancellationToken ct) =>
        {
            var s = await store.GetAsync(id, ct);
            if (s?.TrackingNumber is null) return Results.NotFound();
            if (ShipmentView.LabelIsStale(s, clock.UtcNow))
            {
                var r = await shippit.GetLabelAsync(s.TrackingNumber, ct);
                if (!r.IsOk || r.Value!.LabelUrl is null) return Results.Json(new { error = r.Code ?? "label.unavailable" }, statusCode: StatusCodes.Status409Conflict);
                s.LabelUrl = r.Value.LabelUrl; s.LabelFetchedAtUtc = clock.UtcNow; s.UpdatedAtUtc = clock.UtcNow;
                await store.SaveAsync(s, ct);
            }
            lf.CreateLogger("ShippingAdmin").LogInformation("Label for shipment {ShipmentId} accessed by {AdminId}", id, UserId(user));
            return Results.Ok(new { labelUrl = s.LabelUrl, manifestNumber = s.ManifestNumber });
        });

        admin.MapPost("/shipments/{id:guid}/retry", async (Guid id, ClaimsPrincipal user, IShipmentStore store, IClock clock, ILoggerFactory lf, CancellationToken ct) =>
        {
            var s = await store.GetAsync(id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status != ShipmentStatus.NeedsAttention) return Results.Conflict(new { error = "not_in_needs_attention" });
            var to = ShipmentView.ResumeStep(s);
            s.Advance(to, clock.UtcNow); s.Alert = null;
            if (!await store.SaveAsync(s, ct)) return Results.Conflict(new { error = "concurrent_update" });
            lf.CreateLogger("ShippingAdmin").LogInformation("Shipment {ShipmentId} retried to {Step} by {AdminId}", id, to, UserId(user));
            return Results.Ok(new { status = to.ToString() });
        });

        // The operator found the order in the Shippit dashboard (matching our reference) — record its tracking number.
        admin.MapPost("/shipments/{id:guid}/adopt", async (Guid id, AdoptRequest body, ClaimsPrincipal user, IShipmentStore store, IClock clock, ILoggerFactory lf, CancellationToken ct) =>
        {
            var s = await store.GetAsync(id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status != ShipmentStatus.CreateUnconfirmed) return Results.Conflict(new { error = "not_unconfirmed" });
            if (string.IsNullOrWhiteSpace(body.TrackingNumber) || body.TrackingNumber.Length > 64) return Results.BadRequest(new { error = "tracking_number_invalid" });
            if (await store.GetByTrackingAsync(body.TrackingNumber.Trim(), ct) is not null) return Results.Conflict(new { error = "tracking_number_in_use" });
            s.TrackingNumber = body.TrackingNumber.Trim(); s.Alert = null;
            s.Advance(ShipmentStatus.Created, clock.UtcNow);
            if (!await store.SaveAsync(s, ct)) return Results.Conflict(new { error = "concurrent_update" });
            lf.CreateLogger("ShippingAdmin").LogInformation("Shipment {ShipmentId} adopted tracking number by {AdminId}", id, UserId(user));
            return Results.Ok(new { status = s.Status.ToString() });
        });

        // The operator checked Shippit and there is NO order for our reference — only now is it safe to create again.
        admin.MapPost("/shipments/{id:guid}/confirm-not-created", async (Guid id, ConfirmRequest body, ClaimsPrincipal user, IShipmentStore store, IClock clock, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!body.ConfirmNoOrderExistsInShippit) return Results.BadRequest(new { error = "confirmation_required" });
            var s = await store.GetAsync(id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status != ShipmentStatus.CreateUnconfirmed) return Results.Conflict(new { error = "not_unconfirmed" });
            s.Alert = null; s.Advance(ShipmentStatus.ReadyToCreate, clock.UtcNow);
            if (!await store.SaveAsync(s, ct)) return Results.Conflict(new { error = "concurrent_update" });
            lf.CreateLogger("ShippingAdmin").LogWarning("Shipment {ShipmentId} released for re-create by {AdminId}", id, UserId(user));
            return Results.Ok(new { status = s.Status.ToString() });
        });
    }

    public static Guid? UserId(ClaimsPrincipal u) =>
        Guid.TryParse(u.FindFirstValue("sub") ?? u.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
}

public sealed record AdoptRequest(string TrackingNumber);
public sealed record ConfirmRequest(bool ConfirmNoOrderExistsInShippit);
