namespace GrowGift.Fulfillment.Shipping;

/// <summary>Pure mapping from Shippit tracking states to our <see cref="ShipmentStatus"/>, plus ordering rules for out-of-order events.</summary>
public static class ShippitStateMap
{
    /// <summary>The enabled_states values Shippit documents for webhook registration.</summary>
    public static readonly string[] WebhookStates =
    {
        "order_placed","ready_for_pickup","in_transit","with_driver","delivery_attempted","awaiting_collection","await_collection",
        "awaiting_drop_off","damaged","lost","insufficient_address","returned_to_sender","delivery_failed","with_customs",
        "customs_on_hold","customs_awaiting_payment","customs_failed","return_booked","return_booking_failed","parcel_completed",
        "partially_completed","cancelled","completed","return_requested","invalidated","pickup_failed","untrackable"
    };

    public static string Normalise(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace(' ', '_');

    /// <summary>null = state tells us nothing actionable (or isn't recognised): record the event, don't change our status.</summary>
    public static ShipmentStatus? Map(string? shippitState) => Normalise(shippitState) switch
    {
        "order_placed" or "processing" or "ready_for_pickup" => ShipmentStatus.Booked,
        "in_transit" or "with_driver" or "despatch_in_progress" or "awaiting_collection" or "await_collection" or "awaiting_drop_off"
            or "with_customs" or "customs_on_hold" or "customs_awaiting_payment" => ShipmentStatus.InTransit,
        "completed" or "parcel_completed" => ShipmentStatus.Delivered,
        "cancelled" or "invalidated" => ShipmentStatus.Cancelled,
        "delivery_attempted" or "delivery_failed" or "damaged" or "lost" or "insufficient_address" or "returned_to_sender"
            or "pickup_failed" or "customs_failed" or "return_requested" or "return_booked" or "return_booking_failed"
            or "untrackable" or "partially_completed" => ShipmentStatus.Exception,
        _ => null
    };

    /// <summary>Position along the happy path, used so a late "ready_for_pickup" can't drag a shipment back from InTransit.</summary>
    public static int Rank(ShipmentStatus s) => s switch
    {
        ShipmentStatus.AwaitingAddress => 0,
        ShipmentStatus.ReadyToCreate => 1,
        ShipmentStatus.CreateInFlight => 2,
        ShipmentStatus.CreateUnconfirmed => 2,
        ShipmentStatus.Created => 3,
        ShipmentStatus.Labelled => 4,
        ShipmentStatus.Booked => 5,
        ShipmentStatus.InTransit => 6,
        ShipmentStatus.Exception => 6,
        ShipmentStatus.Delivered => 9,
        ShipmentStatus.Cancelled => 9,
        _ => 0
    };

    /// <summary>
    /// Decides what a carrier event does to a shipment. Returns the new status, or null for "no change".
    /// Rules: terminal is final; Exception may recover to InTransit/Delivered; otherwise only move forward.
    /// </summary>
    public static ShipmentStatus? Apply(ShipmentStatus current, ShipmentStatus? mapped)
    {
        if (mapped is null) return null;
        if (current is ShipmentStatus.Delivered or ShipmentStatus.Cancelled) return null;
        if (mapped == current) return null;
        if (current == ShipmentStatus.Exception)
            return mapped is ShipmentStatus.InTransit or ShipmentStatus.Delivered or ShipmentStatus.Cancelled ? mapped : null;
        if (mapped is ShipmentStatus.Exception or ShipmentStatus.Delivered or ShipmentStatus.Cancelled) return mapped;
        return Rank(mapped.Value) > Rank(current) ? mapped : null;
    }

    public static NotifyKind? NotifyFor(ShipmentStatus s) => s switch
    {
        ShipmentStatus.Booked => NotifyKind.Booked,
        ShipmentStatus.InTransit => NotifyKind.InTransit,
        ShipmentStatus.Delivered => NotifyKind.Delivered,
        ShipmentStatus.Exception => NotifyKind.Exception,
        _ => null
    };
}
