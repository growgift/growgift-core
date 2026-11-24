using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GrowGift.Fulfillment.Shipping;

public enum WebhookResult { Accepted, Duplicate, Unmatched, Unauthorized, NotConfigured, BadRequest, TooLarge }

/// <summary>
/// Receives Shippit tracking webhooks. Shippit documents custom headers (not signatures), so the shared token in
/// <see cref="ShippitOptions.WebhookHeaderName"/> is the authentication; it's compared in constant time and the
/// endpoint refuses everything if no token is configured. Replays are absorbed by the event-key unique index.
/// </summary>
public sealed class ShippitWebhookHandler(IShipmentStore store, IClock clock, ShippitOptions opt, ILogger<ShippitWebhookHandler> log)
{
    public const int MaxBodyBytes = 64 * 1024;

    public async Task<WebhookResult> HandleAsync(string? presentedToken, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(opt.WebhookToken)) return WebhookResult.NotConfigured;   // fail closed
        if (!TokenMatches(presentedToken, opt.WebhookToken)) return WebhookResult.Unauthorized;
        if (body.Length > MaxBodyBytes) return WebhookResult.TooLarge;

        string? tn, state, reference; DateTimeOffset? eventTime;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return WebhookResult.BadRequest;
            tn = Str(root, "tracking_number");
            state = Str(root, "current_state");
            reference = Str(root, "retailer_reference") ?? Str(root, "retailer_invoice") ?? Str(root, "retailer_order_number");
            eventTime = LatestHistoryTime(root, state);
        }
        catch (JsonException) { return WebhookResult.BadRequest; }
        if (state is null || (tn is null && reference is null)) return WebhookResult.BadRequest;

        var s = (tn is not null ? await store.GetByTrackingAsync(tn, ct) : null)
                ?? (reference is not null ? await store.GetByReferenceAsync(reference, ct) : null);
        if (s is null)
        {
            // Ack with 200 so Shippit doesn't hammer us; nothing of ours to update.
            log.LogInformation("Shippit webhook for an unknown shipment ignored (state {State})", ShippitStateMap.Normalise(state));
            return WebhookResult.Unmatched;
        }

        // Event key: stable across replays of the same event; differs for a new state or a new history time.
        var keyMaterial = $"{tn ?? s.TrackingNumber}|{ShippitStateMap.Normalise(state)}|{eventTime?.ToUnixTimeSeconds().ToString() ?? Hash(body.Span)}";
        var key = Hash(Encoding.UTF8.GetBytes(keyMaterial));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = clock.UtcNow;
            // Webhook for an order whose create reply we never saw: adopt Shippit's tracking number.
            if (s.TrackingNumber is null && tn is not null && s.Status is ShipmentStatus.CreateUnconfirmed or ShipmentStatus.CreateInFlight or ShipmentStatus.ReadyToCreate)
            {
                s.TrackingNumber = tn; s.Status = ShipmentStatus.Created; s.Alert = null; s.LastError = null;
                s.Attempts = 0; s.NextAttemptAtUtc = now;
            }
            else if (s.TrackingNumber is not null && tn is not null && !string.Equals(s.TrackingNumber, tn, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Shippit webhook tracking number mismatch for shipment {ShipmentId}; ignored", s.Id);
                return WebhookResult.Unmatched;
            }

            var outcome = await ShipmentEventApplier.ApplyAsync(s, store, state, key, "webhook", eventTime ?? now, now, ct);
            if (outcome == "duplicate") return WebhookResult.Duplicate;
            if (await store.SaveAsync(s, ct)) return WebhookResult.Accepted;

            // Lost an optimistic-concurrency race with the worker. The event row is already stored, so reload and
            // re-apply the status change with a distinct key suffix instead of losing it.
            s = await store.GetAsync(s.Id, ct) ?? s;
            key += "~r" + (attempt + 1);
        }
        log.LogWarning("Shippit webhook could not be saved after retries for shipment {ShipmentId}", s.Id);
        return WebhookResult.Accepted;   // event row is stored; the reconcile pass will converge the status
    }

    public static bool TokenMatches(string? presented, string expected)
    {
        if (presented is null) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..32];

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static DateTimeOffset? LatestHistoryTime(JsonElement root, string? state)
    {
        if (!root.TryGetProperty("status_history", out var h) || h.ValueKind != JsonValueKind.Array) return null;
        DateTimeOffset? best = null;
        foreach (var item in h.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var st = Str(item, "status");
            if (state is not null && st is not null && ShippitStateMap.Normalise(st) != ShippitStateMap.Normalise(state)) continue;
            if (DateTimeOffset.TryParse(Str(item, "time"), out var t) && (best is null || t > best)) best = t;
        }
        return best;
    }
}
