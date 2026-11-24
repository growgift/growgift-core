namespace GrowGift.Identity.Billing;

/// <summary>What to store after looking at a Stripe subscription, and whether we must cancel it at Stripe.</summary>
public sealed record SyncDecision(string Status, bool TrialUsed, bool CardChecked, bool CancelAtStripe);

/// <summary>
/// Turns "what Stripe says now" into "what we store". Pure: the webhook handler fetches the subscription from
/// Stripe (so out-of-order events can't regress us) and this decides. The 6-month card rule lives here.
/// </summary>
public static class BillingSync
{
    public static SyncDecision Decide(
        StripeSubscriptionSnapshot snap,
        string? existingStatus, string? existingSubscriptionId, bool existingTrialUsed, bool existingCardChecked,
        DateTimeOffset nowUtc, int minCardMonths)
    {
        var status = SubscriptionPolicy.MapStripeStatus(snap.StripeStatus);
        var sameSub = existingSubscriptionId == snap.SubscriptionId;

        // We cancelled this subscription ourselves for a short-lived card: keep saying why, don't downgrade to "canceled".
        if (sameSub && existingStatus == SubscriptionStatus.CardRejected)
            return new SyncDecision(SubscriptionStatus.CardRejected, false, true, false);

        var cardChecked = sameSub && existingCardChecked;
        if (SubscriptionPolicy.IsEntitled(status) && !cardChecked)
        {
            if (snap.Card is { } card)
            {
                if (!SubscriptionPolicy.CardMeetsMinimumValidity(card.ExpMonth, card.ExpYear, nowUtc, minCardMonths))
                    return new SyncDecision(SubscriptionStatus.CardRejected, false, true, true);
                cardChecked = true;
            }
            // Card unreadable: leave unchecked (reported, not silently passed) and try again on the next event.
        }

        var trialUsed = existingTrialUsed || (SubscriptionPolicy.IsEntitled(status) && snap.TrialEndUtc is not null);
        return new SyncDecision(status, trialUsed, cardChecked, false);
    }
}
