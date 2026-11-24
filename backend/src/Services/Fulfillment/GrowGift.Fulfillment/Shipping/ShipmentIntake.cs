namespace GrowGift.Fulfillment.Shipping;

public sealed record ShipmentRequest(
    Guid OrderId, Guid GiftId, Guid BuyerId, string? BuyerEmail, string? RecipientName, string? BoxSku,
    string? DeliveryMethod, bool PaymentConfirmed);

public enum IntakeStatus { Created, AlreadyExists, NotPhysical, PaymentNotConfirmed, Invalid }
public sealed record IntakeResult(IntakeStatus Status, Guid? ShipmentId);

/// <summary>
/// Turns a paid physical order into a durable shipment job. The unique OrderId index (not an in-memory check) is what
/// stops two deliveries of the same "order paid" event creating two shipments. Digital gifts are rejected here.
/// </summary>
public sealed class ShipmentIntake(IShipmentStore store, IClock clock)
{
    public async Task<IntakeResult> RegisterAsync(ShipmentRequest r, CancellationToken ct)
    {
        if (!string.Equals(r.DeliveryMethod, "Physical", StringComparison.OrdinalIgnoreCase)) return new(IntakeStatus.NotPhysical, null);
        if (!r.PaymentConfirmed) return new(IntakeStatus.PaymentNotConfirmed, null);
        if (r.OrderId == Guid.Empty || r.BuyerId == Guid.Empty) return new(IntakeStatus.Invalid, null);

        var now = clock.UtcNow;
        var (s, created) = await store.CreateIfAbsentAsync(new Shipment
        {
            Id = Guid.NewGuid(), OrderId = r.OrderId, GiftId = r.GiftId, BuyerId = r.BuyerId,
            BuyerEmail = r.BuyerEmail ?? "", RecipientName = r.RecipientName, BoxSku = r.BoxSku,
            Reference = Shipment.MakeReference(r.OrderId), Status = ShipmentStatus.AwaitingAddress,
            NextAttemptAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        }, ct);
        return new(created ? IntakeStatus.Created : IntakeStatus.AlreadyExists, s.Id);
    }
}
