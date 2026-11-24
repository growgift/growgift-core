using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace GrowGift.Fulfillment.Shipping.Ef;

/// <summary>Reads the purchaser's saved address from Identity over the internal network. The address is used to build the Shippit order and is never logged or returned to a browser.</summary>
public sealed class IdentityAddressSource(IHttpClientFactory http, ILogger<IdentityAddressSource> log) : IAddressSource
{
    private sealed record Dto(string FullName, string Line1, string? Line2, string City, string Region, string PostalCode, string CountryCode, string? Phone, string? Email);

    public async Task<(BuyerAddress?, bool)> GetAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var c = http.CreateClient("identity");
            using var resp = await c.GetAsync($"/api/v1/auth/internal/v1/users/{userId}/shipping-address", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, false);
            if (!resp.IsSuccessStatusCode) { log.LogWarning("Identity address lookup returned {Status}", (int)resp.StatusCode); return (null, true); }
            var d = await resp.Content.ReadFromJsonAsync<Dto>(cancellationToken: ct);
            return d is null ? (null, false) : (new BuyerAddress(d.FullName, d.Line1, d.Line2, d.City, d.Region, d.PostalCode, d.CountryCode, d.Phone, d.Email ?? ""), false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Identity address lookup failed ({Type})", e.GetType().Name);
            return (null, true);
        }
    }
}

public sealed class HttpShipmentNotifier(IHttpClientFactory http, ILogger<HttpShipmentNotifier> log) : IShipmentNotifier
{
    public async Task<bool> SendAsync(Shipment s, NotifyKind kind, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BuyerEmail)) return true;     // nothing to send to; don't retry forever
        try
        {
            var c = http.CreateClient("notifications");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/notifications/shipment-update")
            {
                Content = JsonContent.Create(new
                {
                    shipmentId = s.Id, orderId = s.OrderId, buyerEmail = s.BuyerEmail, recipientName = s.RecipientName,
                    kind = kind.ToString().ToLowerInvariant(), courierName = s.CourierName,
                    trackingNumber = s.TrackingNumber, trackingUrl = s.TrackingUrl, idempotencyKey
                })
            };
            req.Headers.Add("Idempotency-Key", idempotencyKey);
            using var resp = await c.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) log.LogWarning("Shipment email {Kind} for {ShipmentId} returned {Status}", kind, s.Id, (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Shipment email {Kind} for {ShipmentId} failed ({Type})", kind, s.Id, e.GetType().Name);
            return false;
        }
    }
}
