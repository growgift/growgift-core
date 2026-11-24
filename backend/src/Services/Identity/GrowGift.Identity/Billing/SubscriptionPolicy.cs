using GrowGift.Identity.Domain;

namespace GrowGift.Identity.Billing;

/// <summary>Statuses we store. Mostly Stripe's own, lower-cased, plus two of ours.</summary>
public static class SubscriptionStatus
{
    public const string None = "none";                   // no subscription started yet
    public const string CardRejected = "card_rejected";  // card expires too soon; subscription cancelled by us
    public const string Trialing = "trialing";
    public const string Active = "active";
    public const string PastDue = "past_due";            // Stripe is still retrying the payment
}

/// <summary>What the policy needs to know about a stored subscription (no EF types, so it is trivially testable).</summary>
public sealed record SubscriptionState(string Status, bool TrialUsed);

/// <summary>
/// All billing rules in one pure place: who must subscribe, who gets a trial, whether a card is acceptable, and
/// whether the account may do the gated actions. No I/O, no clock reads — callers pass "now".
/// </summary>
public static class SubscriptionPolicy
{
    /// <summary>Roles that must subscribe. Buyers are exempt; children can never mutate anything anyway.</summary>
    public static bool RoleMustSubscribe(UserRole role) => role is UserRole.Parent or UserRole.Recipient;

    /// <summary>Statuses that currently entitle the account. past_due keeps access while Stripe retries the payment.</summary>
    public static bool IsEntitled(string status) =>
        status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    /// <summary>Statuses where a new Checkout must be refused because a live subscription already exists.</summary>
    public static bool HasLiveSubscription(string? status) => status is not null && IsEntitled(status);

    /// <summary>
    /// When this person's free period ends: <paramref name="graceMonths"/> months after they registered. Accounts that
    /// registered BEFORE enforcement was switched on start their free period at the switch-on date instead, so turning
    /// enforcement on never locks anyone out the same day.
    /// </summary>
    public static DateTimeOffset FreeUntil(DateTimeOffset userCreatedAtUtc, DateTimeOffset enforceFromUtc, int graceMonths) =>
        (userCreatedAtUtc > enforceFromUtc ? userCreatedAtUtc : enforceFromUtc).AddMonths(graceMonths);

    /// <summary>
    /// True for accounts registered AFTER billing was switched on: their card is part of registration, so until a live
    /// subscription (a trial counts) exists, registration is incomplete.
    /// </summary>
    public static bool CardRequiredAtRegistration(UserRole role, DateTimeOffset userCreatedAtUtc, SubscriptionState? sub, DateTimeOffset? enforceFromUtc) =>
        enforceFromUtc is { } ef && RoleMustSubscribe(role) && userCreatedAtUtc >= ef && !(sub is not null && IsEntitled(sub.Status));

    /// <summary>
    /// The decision written into the token as billing_ok:
    ///  - enforcement off (enforceFrom null): everyone passes;
    ///  - roles that don't subscribe pass;
    ///  - a trialing/active/past_due subscription passes;
    ///  - an account registered after enforcement began and with no live subscription is blocked straight away
    ///    (no card = registration not finished, or the subscription has ended);
    ///  - an older account (registered before enforcement) has its free period until <see cref="FreeUntil"/>, then stops.
    /// </summary>
    public static bool BillingOk(UserRole role, DateTimeOffset userCreatedAtUtc, SubscriptionState? sub,
        DateTimeOffset? enforceFromUtc, int graceMonths, DateTimeOffset nowUtc)
    {
        if (enforceFromUtc is null) return true;
        if (!RoleMustSubscribe(role)) return true;
        if (sub is not null && IsEntitled(sub.Status)) return true;
        if (userCreatedAtUtc >= enforceFromUtc.Value) return false;
        return nowUtc < FreeUntil(userCreatedAtUtc, enforceFromUtc.Value, graceMonths);
    }

    /// <summary>True when we should invite this person to add a card (older accounts may skip while their free period runs).</summary>
    public static bool NeedsCardPrompt(UserRole role, SubscriptionState? sub, DateTimeOffset? enforceFromUtc) =>
        enforceFromUtc is not null && RoleMustSubscribe(role) && !(sub is not null && IsEntitled(sub.Status));

    /// <summary>
    /// Where Stripe's trial should end so the first charge lands exactly when the free period does. Null = no trial
    /// (already past the free period, or no trial left to give). Stripe won't accept a trial ending too soon, so a
    /// person adding a card with only hours left gets a short minimum of <see cref="MinTrialLead"/>.
    /// </summary>
    public static DateTimeOffset? TrialEndFor(SubscriptionState? sub, DateTimeOffset freeUntilUtc, DateTimeOffset nowUtc)
    {
        if (!TrialEligible(sub) || freeUntilUtc <= nowUtc) return null;
        var earliest = nowUtc + MinTrialLead;
        return freeUntilUtc > earliest ? freeUntilUtc : earliest;
    }

    public static readonly TimeSpan MinTrialLead = TimeSpan.FromDays(3);

    /// <summary>One free trial per account, ever. A cancelled or rejected sign-up never earns a second one.</summary>
    public static bool TrialEligible(SubscriptionState? sub) => sub is null || !sub.TrialUsed;

    /// <summary>
    /// A card is valid through the LAST day of its expiry month. It is acceptable only if it is still valid
    /// <paramref name="minMonths"/> months from now.
    /// </summary>
    public static bool CardMeetsMinimumValidity(int expMonth, int expYear, DateTimeOffset nowUtc, int minMonths)
    {
        if (expMonth is < 1 or > 12 || expYear < 2000) return false;
        var lastInstant = new DateTimeOffset(expYear, expMonth, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddTicks(-1);
        return lastInstant >= nowUtc.AddMonths(minMonths);
    }

    /// <summary>Maps Stripe's subscription status to the one we store; anything unknown is treated as not entitled.</summary>
    public static string MapStripeStatus(string? stripeStatus) => stripeStatus?.ToLowerInvariant() switch
    {
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "unpaid" => "unpaid",
        "canceled" => "canceled",
        "incomplete" => "incomplete",
        "incomplete_expired" => "incomplete_expired",
        "paused" => "paused",
        _ => "unknown"
    };
}
