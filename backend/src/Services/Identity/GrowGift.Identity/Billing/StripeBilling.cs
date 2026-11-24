using Stripe;
using Stripe.Checkout;

namespace GrowGift.Identity.Billing;

/// <summary>The only door to Stripe from Identity. Everything is server-side; the browser only ever gets a Stripe-hosted URL.</summary>
public interface IStripeBilling
{
    Task<string> EnsureCustomerAsync(Guid userId, string email, string? existingCustomerId, CancellationToken ct);
    Task<string> CreateCheckoutUrlAsync(Guid userId, string customerId, DateTimeOffset? trialEndUtc, CancellationToken ct);
    Task<string> CreatePortalUrlAsync(string customerId, CancellationToken ct);
    Task<PriceInfo?> GetPriceAsync(CancellationToken ct);
    Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);
    Task<StripeSubscriptionSnapshot?> FindLatestForCustomerAsync(string customerId, CancellationToken ct);
    Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct);
}

/// <summary>
/// Stripe.net implementation. NOTE: written against Stripe.net 47 but NOT compiled in the offline build used to
/// verify this change (the package isn't available there). The rules around it (BillingSync, SubscriptionPolicy)
/// are tested; this class needs a real build and a Stripe TEST-mode run before it is trusted.
/// </summary>
public sealed class StripeBilling : IStripeBilling
{
    private readonly BillingOptions _o;

    public StripeBilling(BillingOptions options)
    {
        _o = options;
        if (options.StripeSecretKey is not null) StripeConfiguration.ApiKey = options.StripeSecretKey;
    }

    public async Task<string> EnsureCustomerAsync(Guid userId, string email, string? existingCustomerId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(existingCustomerId)) return existingCustomerId;
        var customer = await new CustomerService().CreateAsync(
            new CustomerCreateOptions { Email = email, Metadata = new Dictionary<string, string> { ["user_id"] = userId.ToString() } },
            new RequestOptions { IdempotencyKey = $"gg-cust-{userId}" }, ct);
        return customer.Id;
    }

    public async Task<string> CreateCheckoutUrlAsync(Guid userId, string customerId, DateTimeOffset? trialEndUtc, CancellationToken ct)
    {
        var sub = new SessionSubscriptionDataOptions
        {
            // The user id rides on the subscription itself so every later webhook can find its owner.
            Metadata = new Dictionary<string, string> { ["user_id"] = userId.ToString() }
        };
        if (trialEndUtc is { } end) sub.TrialEnd = end.UtcDateTime;

        var session = await new SessionService().CreateAsync(new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            ClientReferenceId = userId.ToString(),
            PaymentMethodTypes = ["card"],           // the 6-month expiry rule needs a card
            PaymentMethodCollection = "always",      // card is required even though the trial is free
            LineItems = [new SessionLineItemOptions { Price = _o.PriceId, Quantity = 1 }],
            SubscriptionData = sub,
            SuccessUrl = $"{_o.WebBaseUrl}/subscribe/success",
            CancelUrl = $"{_o.WebBaseUrl}/subscribe?canceled=1"
        }, cancellationToken: ct);
        return session.Url;
    }

    public async Task<string> CreatePortalUrlAsync(string customerId, CancellationToken ct)
    {
        var portal = await new Stripe.BillingPortal.SessionService().CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions { Customer = customerId, ReturnUrl = $"{_o.WebBaseUrl}/profile" },
            cancellationToken: ct);
        return portal.Url;
    }

    public async Task<PriceInfo?> GetPriceAsync(CancellationToken ct)
    {
        if (_o.PriceId is null) return null;
        var p = await new PriceService().GetAsync(_o.PriceId, cancellationToken: ct);
        return p.UnitAmount is { } amt ? new PriceInfo(amt, p.Currency, p.Recurring?.Interval) : null;
    }

    public async Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var s = await new SubscriptionService().GetAsync(subscriptionId, cancellationToken: ct);
        return await ToSnapshotAsync(s, ct);
    }

    public async Task<StripeSubscriptionSnapshot?> FindLatestForCustomerAsync(string customerId, CancellationToken ct)
    {
        var list = await new SubscriptionService().ListAsync(
            new SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 1 }, cancellationToken: ct);
        var s = list.Data.FirstOrDefault();
        return s is null ? null : await ToSnapshotAsync(s, ct);
    }

    public Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct) =>
        new SubscriptionService().CancelAsync(subscriptionId, cancellationToken: ct);

    private async Task<StripeSubscriptionSnapshot> ToSnapshotAsync(Stripe.Subscription s, CancellationToken ct)
    {
        Guid? userId = s.Metadata is not null && s.Metadata.TryGetValue("user_id", out var raw) && Guid.TryParse(raw, out var g) ? g : null;

        // Card expiry: the subscription's own default card, else the customer's newest card. Only month/year are read.
        Stripe.PaymentMethod? pm = null;
        if (!string.IsNullOrEmpty(s.DefaultPaymentMethodId))
            pm = await new PaymentMethodService().GetAsync(s.DefaultPaymentMethodId, cancellationToken: ct);
        else
        {
            var cards = await new PaymentMethodService().ListAsync(
                new PaymentMethodListOptions { Customer = s.CustomerId, Type = "card", Limit = 10 }, cancellationToken: ct);
            pm = cards.Data.OrderByDescending(c => c.Created).FirstOrDefault();
        }
        var card = pm?.Card is { } c2 ? new CardInfo((int)c2.ExpMonth, (int)c2.ExpYear) : null;

        DateTimeOffset? trialEnd = s.TrialEnd is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
        return new StripeSubscriptionSnapshot(s.Id, s.CustomerId, userId, s.Status, trialEnd, s.CancelAtPeriodEnd, card);
    }
}
