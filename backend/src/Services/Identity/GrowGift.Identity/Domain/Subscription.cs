namespace GrowGift.Identity.Domain;

/// <summary>
/// One row per user: where their GrowGift subscription stands. Stripe is the source of truth for money and
/// renewal; this row is our copy, rebuilt from Stripe on every billing webhook. No card data is stored here —
/// only whether the card passed our minimum-validity check.
/// </summary>
public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string Status { get; set; } = "none";
    public DateTimeOffset? TrialEndsAtUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    /// <summary>True once a free trial was actually granted. Prevents a second free trial.</summary>
    public bool TrialUsed { get; set; }
    /// <summary>Set once we have read the card's expiry and it passed. Null = not checked yet.</summary>
    public DateTimeOffset? CardCheckedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A Stripe webhook event we have already applied. The primary key is the replay/duplicate guard.</summary>
public sealed class BillingEvent
{
    public string EventId { get; set; } = default!;
    public string Type { get; set; } = default!;
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
