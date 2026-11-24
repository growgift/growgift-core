namespace GrowGift.Identity.Billing;

public sealed record CardInfo(int ExpMonth, int ExpYear);

/// <summary>Everything we need from a Stripe subscription, as plain data. No Stripe types leave the adapter.</summary>
public sealed record StripeSubscriptionSnapshot(
    string SubscriptionId, string CustomerId, Guid? UserId, string StripeStatus,
    DateTimeOffset? TrialEndUtc, bool CancelAtPeriodEnd, CardInfo? Card);

public sealed record PriceInfo(long UnitAmountMinor, string Currency, string? Interval);
