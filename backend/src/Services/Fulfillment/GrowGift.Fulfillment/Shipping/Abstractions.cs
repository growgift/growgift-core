namespace GrowGift.Fulfillment.Shipping;

/// <summary>Persistence port. The EF implementation is in <c>EfShipmentStore</c>; tests use <c>InMemoryShipmentStore</c>.</summary>
public interface IShipmentStore
{
    Task<Shipment?> GetAsync(Guid id, CancellationToken ct);
    Task<Shipment?> GetByOrderAsync(Guid orderId, CancellationToken ct);
    Task<Shipment?> GetByTrackingAsync(string trackingNumber, CancellationToken ct);
    Task<Shipment?> GetByReferenceAsync(string reference, CancellationToken ct);
    Task<IReadOnlyList<Shipment>> ListByBuyerAsync(Guid buyerId, CancellationToken ct);
    Task<IReadOnlyList<ShipmentEvent>> ListEventsAsync(Guid shipmentId, CancellationToken ct);

    /// <summary>Inserts unless a shipment for the order already exists (unique OrderId). Returns the stored row and whether it was new.</summary>
    Task<(Shipment Shipment, bool Created)> CreateIfAbsentAsync(Shipment s, CancellationToken ct);

    /// <summary>Optimistic save. False = someone else changed the row; caller reloads and decides again.</summary>
    Task<bool> SaveAsync(Shipment s, CancellationToken ct);

    /// <summary>Atomically takes a lease on one due shipment so two workers never process the same one.</summary>
    Task<Shipment?> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct);

    /// <summary>False = this (ShipmentId, EventKey) was already recorded — the duplicate/replay guard.</summary>
    Task<bool> TryAddEventAsync(ShipmentEvent e, CancellationToken ct);

    /// <summary>Booked/in-transit shipments with no carrier event since <paramref name="silentSince"/>.</summary>
    Task<IReadOnlyList<Shipment>> ListSilentAsync(DateTimeOffset silentSince, int take, CancellationToken ct);
}

public sealed record BuyerAddress(string FullName, string Line1, string? Line2, string City, string Region, string PostalCode, string CountryCode, string? Phone, string Email);

/// <summary>Reads the purchaser's saved address from Identity (server-to-server). Never returns it to a browser.</summary>
public interface IAddressSource
{
    /// <summary>null = the user has no saved address (or the lookup failed — see <paramref name="Failed"/>).</summary>
    Task<(BuyerAddress? Address, bool Failed)> GetAsync(Guid userId, CancellationToken ct);
}

public interface IShipmentNotifier
{
    /// <summary>Sends one customer email. <paramref name="idempotencyKey"/> is stable per (shipment, kind) so a receiver can dedupe. True = delivered to the notification service.</summary>
    Task<bool> SendAsync(Shipment s, NotifyKind kind, string idempotencyKey, CancellationToken ct);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
