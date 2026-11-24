namespace GrowGift.Fulfillment.Shipping;

public enum ShipmentStatus
{
    AwaitingAddress = 0,   // job created at payment; the purchaser's address hasn't been captured yet
    ReadyToCreate = 1,     // address captured + validated; Shippit order not created
    CreateInFlight = 2,    // create request about to be / being sent (persisted BEFORE sending)
    CreateUnconfirmed = 3, // outcome unknown (timeout / 5xx / crash) — NEVER resent; resolved by webhook or a human
    Created = 4,           // Shippit order exists (tracking number stored); label pending
    Labelled = 5,          // label available; booking pending
    Booked = 6,            // carrier booked (manifest) — or booked at creation by the service type
    InTransit = 7,
    Delivered = 8,         // terminal
    Cancelled = 9,         // terminal
    Exception = 10,        // delivery problem reported by the carrier; can still recover
    NeedsAttention = 11    // permanent failure or retries exhausted; see LastError
}

/// <summary>Bit flags: which customer notifications a shipment has reached / had sent.</summary>
[Flags]
public enum NotifyKind { None = 0, Booked = 1, InTransit = 2, Delivered = 4, Exception = 8 }

/// <summary>
/// One physical shipment for one paid physical gift order. The address is a snapshot taken from the purchaser's
/// saved address when the shipment was created, so later edits to the account don't change an order in flight.
/// </summary>
public sealed class Shipment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid GiftId { get; set; }
    public Guid BuyerId { get; set; }
    public string BuyerEmail { get; set; } = default!;
    public string? RecipientName { get; set; }
    public string? BoxSku { get; set; }
    /// <summary>Our reference sent to Shippit (retailer_invoice / retailer_reference); also how a webhook is matched when the create outcome was unknown.</summary>
    public string Reference { get; set; } = default!;

    public ShipmentStatus Status { get; set; }

    // Address snapshot (personal data — never logged).
    public string? ToName { get; set; }
    public string? ToLine1 { get; set; }
    public string? ToLine2 { get; set; }
    public string? ToCity { get; set; }
    public string? ToState { get; set; }
    public string? ToPostcode { get; set; }
    public string? ToCountry { get; set; }
    public string? ToPhone { get; set; }

    public string? TrackingNumber { get; set; }
    public string? CourierName { get; set; }
    public string? TrackingUrl { get; set; }
    public string? LabelUrl { get; set; }
    public DateTimeOffset? LabelFetchedAtUtc { get; set; }
    public string? ManifestNumber { get; set; }
    public string? ManifestId { get; set; }
    public string? ShippitState { get; set; }   // last state reported by Shippit

    public int Attempts { get; set; }            // attempts at the CURRENT step (reset on progress)
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }       // sanitised code only — never a body
    public string? Alert { get; set; }           // raised for a human (support) when set
    public DateTimeOffset? LastApiOkAtUtc { get; set; }
    public DateTimeOffset? LastEventAtUtc { get; set; }
    public DateTimeOffset? LastReconciledAtUtc { get; set; }

    public int ReachedMask { get; set; }
    public int NotifiedMask { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Optimistic-concurrency counter (the store bumps it on every save) so the worker and the webhook receiver can't overwrite each other.</summary>
    public int RowVersion { get; set; }

    public static string MakeReference(Guid orderId) => "GG-" + orderId.ToString("N")[..12].ToUpperInvariant();

    public bool IsTerminal => Status is ShipmentStatus.Delivered or ShipmentStatus.Cancelled;

    public void Reach(NotifyKind kind) => ReachedMask |= (int)kind;
    public NotifyKind PendingNotifications => (NotifyKind)(ReachedMask & ~NotifiedMask);

    /// <summary>Moves forward and clears the per-step retry state.</summary>
    public void Advance(ShipmentStatus next, DateTimeOffset now)
    {
        Status = next;
        Attempts = 0;
        LastError = null;
        LastApiOkAtUtc = now;
        NextAttemptAtUtc = now;
        UpdatedAtUtc = now;
    }
}

/// <summary>One accepted carrier event. The unique (ShipmentId, EventKey) index is what makes webhook replays harmless.</summary>
public sealed class ShipmentEvent
{
    public Guid Id { get; set; }
    public Guid ShipmentId { get; set; }
    public string EventKey { get; set; } = default!;
    public string Source { get; set; } = default!;      // "webhook" | "reconcile"
    public string ShippitState { get; set; } = default!;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    /// <summary>What happened to our status: applied | ignored_stale | ignored_terminal | no_change.</summary>
    public string Outcome { get; set; } = default!;
}
