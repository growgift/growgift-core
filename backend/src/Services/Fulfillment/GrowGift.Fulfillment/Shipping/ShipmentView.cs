namespace GrowGift.Fulfillment.Shipping;

/// <summary>What a customer may see. Deliberately has no address lines, no internal error codes and no label/manifest data.</summary>
public static class ShipmentView
{
    public static string CustomerStatus(ShipmentStatus s) => s switch
    {
        ShipmentStatus.AwaitingAddress => "Waiting for your delivery address",
        ShipmentStatus.ReadyToCreate or ShipmentStatus.CreateInFlight or ShipmentStatus.CreateUnconfirmed
            or ShipmentStatus.Created or ShipmentStatus.Labelled => "Preparing your gift box",
        ShipmentStatus.Booked => "Ready for courier pickup",
        ShipmentStatus.InTransit => "On its way",
        ShipmentStatus.Exception => "Delivery needs attention — we're on it",
        ShipmentStatus.Delivered => "Delivered",
        ShipmentStatus.Cancelled => "Cancelled",
        ShipmentStatus.NeedsAttention => "We're sorting out a problem with your shipment",
        _ => "Processing"
    };

    public static object ForCustomer(Shipment s, IEnumerable<ShipmentEvent> events) => new
    {
        orderId = s.OrderId,
        status = CustomerStatus(s.Status),
        carrier = s.CourierName,
        trackingNumber = s.TrackingNumber,
        trackingUrl = s.TrackingUrl,
        shipTo = new { suburb = s.ToCity, state = s.ToState, postcode = s.ToPostcode },      // enough to recognise, not enough to locate
        updatedAtUtc = s.UpdatedAtUtc,
        events = events.Where(e => ShippitStateMap.Map(e.ShippitState) is not null && e.Outcome != "ignored_stale")
                       .Select(e => new { state = e.ShippitState, atUtc = e.OccurredAtUtc })
    };

    /// <summary>Support/admin view: operational detail, still no street address.</summary>
    public static object ForAdmin(Shipment s) => new
    {
        s.Id, s.OrderId, s.GiftId, s.Reference, status = s.Status.ToString(), s.TrackingNumber, s.CourierName,
        s.ManifestNumber, s.ManifestId, s.ShippitState, s.Attempts, s.NextAttemptAtUtc, s.LastError, s.Alert,
        s.LastEventAtUtc, s.LastReconciledAtUtc, s.CreatedAtUtc, s.UpdatedAtUtc, labelAvailable = s.LabelUrl is not null
    };

    /// <summary>Which step an operator-requested retry of a NeedsAttention shipment should resume at. Never re-creates an order that has a tracking number.</summary>
    public static ShipmentStatus ResumeStep(Shipment s)
    {
        if (s.TrackingNumber is not null)
            return s.ManifestNumber is not null ? ShipmentStatus.Booked : s.LabelUrl is not null ? ShipmentStatus.Labelled : ShipmentStatus.Created;
        return s.ToLine1 is null || (s.LastError?.StartsWith("address.", StringComparison.Ordinal) ?? false)
            ? ShipmentStatus.AwaitingAddress : ShipmentStatus.ReadyToCreate;
    }

    public static bool LabelIsStale(Shipment s, DateTimeOffset now) => s.LabelUrl is null || s.LabelFetchedAtUtc is null || now - s.LabelFetchedAtUtc > TimeSpan.FromDays(6);
}
