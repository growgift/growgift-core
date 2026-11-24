namespace GrowGift.Fulfillment.Shipping;

/// <summary>
/// The single place a carrier event (webhook OR polling) changes a shipment. Records the event first — the unique
/// (shipment, event key) index makes a replay a no-op — then moves the status forward only (see <see cref="ShippitStateMap.Apply"/>).
/// Mutates <paramref name="s"/>; the caller saves it.
/// </summary>
public static class ShipmentEventApplier
{
    public static async Task<string> ApplyAsync(Shipment s, IShipmentStore store, string shippitState, string eventKey,
        string source, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken ct)
    {
        var norm = ShippitStateMap.Normalise(shippitState);
        var mapped = ShippitStateMap.Map(norm);
        var next = ShippitStateMap.Apply(s.Status, mapped);

        string outcome =
            s.IsTerminal ? "ignored_terminal" :
            mapped is null ? "no_change" :
            next is null ? (ShippitStateMap.Rank(mapped.Value) < ShippitStateMap.Rank(s.Status) ? "ignored_stale" : "no_change") :
            "applied";

        var inserted = await store.TryAddEventAsync(new ShipmentEvent
        {
            Id = Guid.NewGuid(), ShipmentId = s.Id, EventKey = eventKey, Source = source,
            ShippitState = norm.Length > 64 ? norm[..64] : norm, OccurredAtUtc = occurredAt.ToUniversalTime(), ReceivedAtUtc = now.ToUniversalTime(), Outcome = outcome
        }, ct);
        if (!inserted) return "duplicate";

        s.LastEventAtUtc = now;
        s.UpdatedAtUtc = now;
        if (outcome != "ignored_terminal") s.ShippitState = norm.Length > 64 ? norm[..64] : norm;

        if (next is { } n)
        {
            s.Status = n;
            s.Attempts = 0;
            s.LastError = null;
            s.NextAttemptAtUtc = now;
            if (ShippitStateMap.NotifyFor(n) is { } k) s.Reach(k);
            s.Alert = n == ShipmentStatus.Exception ? "carrier_exception"
                    : s.Alert == "carrier_exception" || s.Alert == "create_unconfirmed" ? null : s.Alert;
        }
        return outcome;
    }
}
