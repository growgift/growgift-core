using System.Security.Claims;
using GrowGift.Identity.Billing;
using GrowGift.Identity.Domain;
using GrowGift.SharedKernel.Billing;
using Xunit;

namespace GrowGift.Identity.Tests;

public class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Enforce = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- who must subscribe, and the lapse rule (free period runs from REGISTRATION) ----

    private static bool Ok(UserRole role, DateTimeOffset created, SubscriptionState? sub, DateTimeOffset? enforce, DateTimeOffset now) =>
        SubscriptionPolicy.BillingOk(role, created, sub, enforce, 3, now);

    [Fact] public void Enforcement_off_means_nobody_is_ever_blocked()
    {
        foreach (var role in Enum.GetValues<UserRole>())
            Assert.True(Ok(role, Now.AddYears(-2), new SubscriptionState("canceled", true), null, Now));
    }

    [Fact] public void Only_parents_and_recipients_must_subscribe()
    {
        Assert.True(SubscriptionPolicy.RoleMustSubscribe(UserRole.Parent));
        Assert.True(SubscriptionPolicy.RoleMustSubscribe(UserRole.Recipient));
        Assert.False(SubscriptionPolicy.RoleMustSubscribe(UserRole.Buyer));
        Assert.False(SubscriptionPolicy.RoleMustSubscribe(UserRole.Child));
        Assert.False(SubscriptionPolicy.RoleMustSubscribe(UserRole.Admin));
        var later = Enforce.AddMonths(8);
        Assert.True(Ok(UserRole.Buyer, Enforce.AddDays(1), null, Enforce, later));
        Assert.True(Ok(UserRole.Child, Enforce.AddDays(1), null, Enforce, later));
    }

    [Fact] public void An_account_registered_after_enforcement_needs_a_card_straight_away()
    {
        var registered = Enforce.AddDays(10);
        Assert.False(Ok(UserRole.Parent, registered, null, Enforce, registered));                                   // day 0, no card
        Assert.False(Ok(UserRole.Recipient, registered, new SubscriptionState("none", false), Enforce, registered.AddDays(5)));
        Assert.True(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Parent, registered, null, Enforce));
        Assert.True(Ok(UserRole.Parent, registered, new SubscriptionState("trialing", true), Enforce, registered));  // card on file
        Assert.False(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Parent, registered, new SubscriptionState("trialing", true), Enforce));
    }

    [Fact] public void Roles_that_dont_subscribe_are_never_asked_for_a_card()
    {
        var registered = Enforce.AddDays(10);
        Assert.False(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Buyer, registered, null, Enforce));
        Assert.False(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Child, registered, null, Enforce));
        Assert.False(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Parent, registered, null, null));
    }

    [Fact] public void A_new_account_whose_card_was_rejected_or_subscription_ended_is_blocked_again()
    {
        var registered = Enforce.AddDays(10);
        foreach (var bad in new[] { "card_rejected", "canceled", "unpaid", "incomplete_expired" })
            Assert.False(Ok(UserRole.Parent, registered, new SubscriptionState(bad, false), Enforce, registered.AddDays(30)));
    }

    [Fact] public void The_first_charge_lands_three_months_after_each_persons_own_registration()
    {
        var early = Enforce.AddDays(1); var late = Enforce.AddDays(60);
        Assert.Equal(early.AddMonths(3), SubscriptionPolicy.FreeUntil(early, Enforce, 3));
        Assert.Equal(late.AddMonths(3), SubscriptionPolicy.FreeUntil(late, Enforce, 3));
        // Their card is added at registration (hours after), so Stripe's trial ends at registration + 3 months.
        Assert.Equal(early.AddMonths(3), SubscriptionPolicy.TrialEndFor(null, early.AddMonths(3), early.AddMinutes(10)));
    }

    [Fact] public void Accounts_older_than_enforcement_get_three_months_from_the_switch_on_date_and_may_skip_the_card()
    {
        var old = Enforce.AddYears(-2);
        Assert.False(SubscriptionPolicy.CardRequiredAtRegistration(UserRole.Parent, old, null, Enforce));
        Assert.True(Ok(UserRole.Parent, old, null, Enforce, Enforce.AddDays(1)));                    // not locked out on day one
        Assert.True(Ok(UserRole.Parent, old, null, Enforce, Enforce.AddMonths(3).AddMinutes(-1)));
        Assert.False(Ok(UserRole.Parent, old, null, Enforce, Enforce.AddMonths(3)));
        Assert.Equal(Enforce.AddMonths(3), SubscriptionPolicy.FreeUntil(old, Enforce, 3));
    }

    [Fact] public void Trial_active_and_past_due_keep_access_for_anyone()
    {
        var after = Enforce.AddMonths(12);
        foreach (var ok in new[] { "trialing", "active", "past_due" })
            Assert.True(Ok(UserRole.Parent, Enforce.AddDays(1), new SubscriptionState(ok, true), Enforce, after));
    }

    [Fact] public void Cancelling_early_does_not_shorten_an_older_accounts_free_period()
    {
        var old = Enforce.AddYears(-1);
        Assert.True(Ok(UserRole.Parent, old, new SubscriptionState("canceled", true), Enforce, Enforce.AddMonths(2)));
        Assert.False(Ok(UserRole.Parent, old, new SubscriptionState("canceled", true), Enforce, Enforce.AddMonths(3).AddDays(1)));
    }

    [Fact] public void The_card_prompt_applies_to_parents_and_recipients_without_a_live_subscription()
    {
        Assert.True(SubscriptionPolicy.NeedsCardPrompt(UserRole.Parent, null, Enforce));
        Assert.True(SubscriptionPolicy.NeedsCardPrompt(UserRole.Recipient, new SubscriptionState("card_rejected", false), Enforce));
        Assert.False(SubscriptionPolicy.NeedsCardPrompt(UserRole.Parent, new SubscriptionState("trialing", true), Enforce));
        Assert.False(SubscriptionPolicy.NeedsCardPrompt(UserRole.Buyer, null, Enforce));
        Assert.False(SubscriptionPolicy.NeedsCardPrompt(UserRole.Parent, null, null));
    }

    // ---- Stripe's trial ends exactly when the free period ends ----

    [Fact] public void The_stripe_trial_ends_at_the_end_of_the_free_period()
    {
        var freeUntil = Now.AddMonths(2);
        Assert.Equal(freeUntil, SubscriptionPolicy.TrialEndFor(null, freeUntil, Now));
    }

    [Fact] public void With_only_hours_left_the_trial_gets_a_short_minimum_instead_of_charging_immediately()
    {
        Assert.Equal(Now.AddDays(3), SubscriptionPolicy.TrialEndFor(null, Now.AddHours(5), Now));
    }

    [Fact] public void Past_the_free_period_or_after_a_used_trial_there_is_no_trial()
    {
        Assert.Null(SubscriptionPolicy.TrialEndFor(null, Now.AddMinutes(-1), Now));
        Assert.Null(SubscriptionPolicy.TrialEndFor(new SubscriptionState("canceled", true), Now.AddMonths(2), Now));
    }

    // ---- one free trial per account ----

    [Fact] public void The_free_trial_is_granted_once()
    {
        Assert.True(SubscriptionPolicy.TrialEligible(null));
        Assert.True(SubscriptionPolicy.TrialEligible(new SubscriptionState("none", false)));
        Assert.False(SubscriptionPolicy.TrialEligible(new SubscriptionState("canceled", true)));
    }

    [Fact] public void A_live_subscription_blocks_a_second_checkout()
    {
        Assert.True(SubscriptionPolicy.HasLiveSubscription("trialing"));
        Assert.True(SubscriptionPolicy.HasLiveSubscription("active"));
        Assert.True(SubscriptionPolicy.HasLiveSubscription("past_due"));
        Assert.False(SubscriptionPolicy.HasLiveSubscription("canceled"));
        Assert.False(SubscriptionPolicy.HasLiveSubscription("card_rejected"));
        Assert.False(SubscriptionPolicy.HasLiveSubscription(null));
    }

    // ---- the 6-month card rule ----

    [Fact] public void A_card_must_be_valid_for_at_least_the_minimum_months()
    {
        // Today is 7 Nov 2026, so six months on is 7 May 2027. A card is valid through the END of its expiry month.
        Assert.True(SubscriptionPolicy.CardMeetsMinimumValidity(5, 2027, Now, 6));   // valid to 31 May 2027
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(4, 2027, Now, 6));  // valid only to 30 Apr 2027
        Assert.True(SubscriptionPolicy.CardMeetsMinimumValidity(12, 2030, Now, 6));
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(11, 2026, Now, 6)); // expires this month
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(10, 2026, Now, 6)); // already expired
    }

    [Fact] public void Nonsense_expiry_values_are_never_accepted()
    {
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(0, 2030, Now, 6));
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(13, 2030, Now, 6));
        Assert.False(SubscriptionPolicy.CardMeetsMinimumValidity(6, 99, Now, 6));
    }

    // ---- turning Stripe's view into what we store ----

    private static StripeSubscriptionSnapshot Snap(string status, CardInfo? card, DateTimeOffset? trialEnd = null, string sub = "sub_1") =>
        new(sub, "cus_1", Guid.NewGuid(), status, trialEnd, false, card);

    [Fact] public void A_good_card_in_a_trial_is_accepted_and_uses_up_the_trial()
    {
        var d = BillingSync.Decide(Snap("trialing", new(12, 2030), Now.AddMonths(3)), null, null, false, false, Now, 6);
        Assert.Equal("trialing", d.Status);
        Assert.True(d.TrialUsed); Assert.True(d.CardChecked); Assert.False(d.CancelAtStripe);
    }

    [Fact] public void A_short_lived_card_cancels_the_subscription_and_does_not_burn_the_trial()
    {
        var d = BillingSync.Decide(Snap("trialing", new(2, 2027), Now.AddMonths(3)), null, null, false, false, Now, 6);
        Assert.Equal("card_rejected", d.Status);
        Assert.True(d.CancelAtStripe); Assert.False(d.TrialUsed);
    }

    [Fact] public void The_rejection_reason_survives_Stripes_own_canceled_event()
    {
        var d = BillingSync.Decide(Snap("canceled", new(2, 2027)), "card_rejected", "sub_1", false, true, Now, 6);
        Assert.Equal("card_rejected", d.Status);
        Assert.False(d.CancelAtStripe);   // already cancelled; don't cancel twice
    }

    [Fact] public void A_second_attempt_with_a_good_card_is_judged_afresh_and_still_gets_the_trial()
    {
        var d = BillingSync.Decide(Snap("trialing", new(12, 2030), Now.AddMonths(3), "sub_2"), "card_rejected", "sub_1", false, true, Now, 6);
        Assert.Equal("trialing", d.Status);
        Assert.True(d.TrialUsed);
    }

    [Fact] public void An_unreadable_card_is_left_unchecked_not_silently_passed_or_cancelled()
    {
        var d = BillingSync.Decide(Snap("trialing", null, Now.AddMonths(3)), null, null, false, false, Now, 6);
        Assert.Equal("trialing", d.Status);
        Assert.False(d.CardChecked); Assert.False(d.CancelAtStripe);
    }

    [Fact] public void The_card_is_not_rechecked_once_it_has_passed()
    {
        // Same card now close to expiry months later: that's renewal's business (Stripe), not a reason to cancel.
        var d = BillingSync.Decide(Snap("active", new(1, 2027)), "trialing", "sub_1", true, true, Now.AddMonths(3), 6);
        Assert.Equal("active", d.Status);
        Assert.False(d.CancelAtStripe); Assert.True(d.TrialUsed);
    }

    [Fact] public void Every_stripe_status_maps_and_unknown_ones_fail_safe()
    {
        Assert.Equal("trialing", SubscriptionPolicy.MapStripeStatus("trialing"));
        Assert.Equal("past_due", SubscriptionPolicy.MapStripeStatus("PAST_DUE"));
        Assert.Equal("unknown", SubscriptionPolicy.MapStripeStatus("something_new"));
        Assert.Equal("unknown", SubscriptionPolicy.MapStripeStatus(null));
        Assert.False(SubscriptionPolicy.IsEntitled("unknown"));
    }

    // ---- configuration safety ----

    private static BillingOptions Opts(Dictionary<string, string> env) =>
        BillingOptions.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null, null);

    [Fact] public void Defaults_are_three_months_free_six_months_card_and_enforcement_off()
    {
        var o = Opts(new());
        Assert.Equal(3, o.TrialMonths); Assert.Equal(6, o.MinCardMonths);
        Assert.Null(o.EnforceFromUtc); Assert.False(o.Configured);
        Assert.Equal("http://localhost:3000", o.WebBaseUrl);
    }

    [Fact] public void A_typo_in_the_enforcement_date_leaves_enforcement_off()
    {
        Assert.Null(Opts(new() { ["BILLING_ENFORCE_FROM_UTC"] = "next tuesday" }).EnforceFromUtc);
        Assert.Equal(Enforce, Opts(new() { ["BILLING_ENFORCE_FROM_UTC"] = "2026-12-01T00:00:00Z" }).EnforceFromUtc);
    }

    [Fact] public void Out_of_range_settings_fall_back_to_the_defaults()
    {
        var o = Opts(new() { ["BILLING_TRIAL_MONTHS"] = "-4", ["BILLING_MIN_CARD_MONTHS"] = "9999" });
        Assert.Equal(3, o.TrialMonths); Assert.Equal(6, o.MinCardMonths);
    }

    [Fact] public void A_live_stripe_key_is_refused_outside_production()
    {
        var live = Opts(new() { ["STRIPE_SECRET_KEY"] = "sk_live_abc", ["STRIPE_PRICE_ID"] = "price_1" });
        Assert.Contains("live", live.Refusal(isProduction: false));
        Assert.Null(live.Refusal(isProduction: true));
        Assert.Null(Opts(new() { ["STRIPE_SECRET_KEY"] = "sk_test_abc", ["STRIPE_PRICE_ID"] = "price_1" }).Refusal(isProduction: false));
    }

    // ---- what Gifting and Family actually read ----

    private static ClaimsPrincipal Caller(string? billingOk) =>
        new(new ClaimsIdentity(billingOk is null ? Array.Empty<Claim>() : new[] { new Claim("billing_ok", billingOk) }, "test"));

    [Fact] public void Only_an_explicit_false_blocks_a_gated_action()
    {
        Assert.True(BillingGate.IsBlocked(Caller("false")));
        Assert.True(BillingGate.IsBlocked(Caller("FALSE")));
        Assert.False(BillingGate.IsBlocked(Caller("true")));
        Assert.False(BillingGate.IsBlocked(Caller(null)));   // an older token must not lock anyone out
        Assert.False(BillingGate.IsBlocked(Caller("")));
    }
}
