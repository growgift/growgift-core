using System.Security.Claims;
using GrowGift.Identity.Domain;
using GrowGift.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using UserSubscription = GrowGift.Identity.Domain.Subscription;

namespace GrowGift.Identity.Billing;

/// <summary>
/// Subscription API. Everything touching Stripe happens here, server-side. The browser receives only a
/// Stripe-hosted URL and our own status summary — never a customer id, subscription id, key or card detail.
/// </summary>
public static class BillingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/billing");

        v1.MapGet("/subscription", Status).RequireAuthorization("Buyer");
        v1.MapPost("/checkout", Checkout).RequireAuthorization("Buyer");
        v1.MapPost("/portal", Portal).RequireAuthorization("Buyer");
        v1.MapPost("/sync", Sync).RequireAuthorization("Buyer");   // webhook fallback, like Gifting's sync-payment
        v1.MapPost("/webhook", Webhook).AllowAnonymous();          // signature-verified
    }

    private static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult NotConfigured(string why) => Results.Json(
        new { error = new { code = "billing.not_configured", message = why } }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string? Unavailable(BillingOptions o, IHostEnvironment env) =>
        o.Refusal(env.IsProduction()) ?? (o.Configured ? null : "Subscriptions aren't set up yet.");

    private static async Task<IResult> Status(ClaimsPrincipal principal, IdentityDbContext db, IStripeBilling stripe,
        BillingOptions options, IHostEnvironment env, ILogger<BillingOptions> logger, CancellationToken ct)
    {
        var userId = UserId(principal);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var state = sub is null ? null : new SubscriptionState(sub.Status, sub.TrialUsed);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? freeUntil = options.EnforceFromUtc is { } ef && SubscriptionPolicy.RoleMustSubscribe(user.Role)
            ? SubscriptionPolicy.FreeUntil(user.CreatedAtUtc, ef, options.TrialMonths) : null;

        // The price is read from Stripe so changing it there changes the page. If Stripe is unreachable the page
        // still loads, just without a price.
        object? price = null;
        if (Unavailable(options, env) is null)
        {
            try
            {
                var p = await stripe.GetPriceAsync(ct);
                if (p is not null) price = new { amount = p.UnitAmountMinor / 100m, currency = p.Currency.ToUpperInvariant(), interval = p.Interval };
            }
            catch (Exception e) { logger.LogWarning("Couldn't read the subscription price from Stripe ({Kind}).", e.GetType().Name); }
        }

        return Results.Ok(new
        {
            required = SubscriptionPolicy.RoleMustSubscribe(user.Role),
            enforced = options.EnforceFromUtc is not null,
            status = sub?.Status ?? SubscriptionStatus.None,
            entitled = sub is not null && SubscriptionPolicy.IsEntitled(sub.Status),
            billingOk = SubscriptionPolicy.BillingOk(user.Role, user.CreatedAtUtc, state, options.EnforceFromUtc, options.TrialMonths, now),
            freeUntil,
            cardRequired = SubscriptionPolicy.CardRequiredAtRegistration(user.Role, user.CreatedAtUtc, state, options.EnforceFromUtc),
            trialEligible = SubscriptionPolicy.TrialEligible(state),
            trialMonths = options.TrialMonths,
            minCardMonths = options.MinCardMonths,
            trialEndsAt = sub?.TrialEndsAtUtc,
            cancelAtPeriodEnd = sub?.CancelAtPeriodEnd ?? false,
            price,
            available = Unavailable(options, env) is null
        });
    }

    private static async Task<IResult> Checkout(ClaimsPrincipal principal, IdentityDbContext db, IStripeBilling stripe,
        BillingOptions options, IHostEnvironment env, ILogger<BillingOptions> logger, CancellationToken ct)
    {
        if (Unavailable(options, env) is { } why) return NotConfigured(why);

        var userId = UserId(principal);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();
        if (!SubscriptionPolicy.RoleMustSubscribe(user.Role))
            return Results.Json(new { error = new { code = "subscription.not_required", message = "Your account type doesn't need a subscription." } }, statusCode: 403);

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub is not null && SubscriptionPolicy.HasLiveSubscription(sub.Status))
            return Results.Conflict(new { error = new { code = "subscription.already_active", message = "You already have a subscription. Use Manage subscription to change it." } });

        try
        {
            var customerId = await stripe.EnsureCustomerAsync(userId, user.Email, sub?.StripeCustomerId, ct);
            if (sub is null)
            {
                sub = new UserSubscription { UserId = userId };
                db.Subscriptions.Add(sub);
            }
            sub.StripeCustomerId = customerId;
            sub.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var state = new SubscriptionState(sub.Status, sub.TrialUsed);
            // The free period runs from REGISTRATION, so the first charge lands exactly when it ends. Without
            // enforcement configured there is no clock, so fall back to months-from-now.
            var now = DateTimeOffset.UtcNow;
            var freeUntil = options.EnforceFromUtc is { } ef
                ? SubscriptionPolicy.FreeUntil(user.CreatedAtUtc, ef, options.TrialMonths)
                : now.AddMonths(options.TrialMonths);
            var trialEnd = options.TrialMonths > 0 ? SubscriptionPolicy.TrialEndFor(state, freeUntil, now) : null;

            var url = await stripe.CreateCheckoutUrlAsync(userId, customerId, trialEnd, ct);
            return Results.Ok(new { url });
        }
        catch (StripeException e)
        {
            logger.LogWarning("Stripe couldn't start subscription checkout for {UserId}: {Code}", userId, e.StripeError?.Code ?? "unknown");
            return Results.Json(new { error = new { code = "billing.stripe_error", message = "We couldn't start checkout just now. Please try again." } }, statusCode: 502);
        }
    }

    private static async Task<IResult> Portal(ClaimsPrincipal principal, IdentityDbContext db, IStripeBilling stripe,
        BillingOptions options, IHostEnvironment env, ILogger<BillingOptions> logger, CancellationToken ct)
    {
        if (Unavailable(options, env) is { } why) return NotConfigured(why);
        var userId = UserId(principal);
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub?.StripeCustomerId is null)
            return Results.NotFound(new { error = new { code = "subscription.none", message = "There's no subscription to manage yet." } });
        try { return Results.Ok(new { url = await stripe.CreatePortalUrlAsync(sub.StripeCustomerId, ct) }); }
        catch (StripeException e)
        {
            logger.LogWarning("Stripe couldn't open the billing portal for {UserId}: {Code}", userId, e.StripeError?.Code ?? "unknown");
            return Results.Json(new { error = new { code = "billing.stripe_error", message = "We couldn't open subscription management just now. Please try again." } }, statusCode: 502);
        }
    }

    /// <summary>Asks Stripe about the caller's own latest subscription and applies it. Only ever for the caller.</summary>
    private static async Task<IResult> Sync(ClaimsPrincipal principal, IdentityDbContext db, IStripeBilling stripe,
        BillingOptions options, IHostEnvironment env, GrowGift.Identity.Infrastructure.TokenService tokens, ILogger<BillingOptions> logger, CancellationToken ct)
    {
        if (Unavailable(options, env) is { } why) return NotConfigured(why);
        var userId = UserId(principal);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub?.StripeCustomerId is not null)
        {
            try
            {
                var snap = await stripe.FindLatestForCustomerAsync(sub.StripeCustomerId, ct);
                if (snap is not null && (snap.UserId is null || snap.UserId == userId))
                {
                    await BillingApplier.ApplyAsync(db, stripe, options, snap with { UserId = userId }, DateTimeOffset.UtcNow, logger, ct);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (StripeException e) { logger.LogWarning("Billing sync failed for {UserId}: {Code}", userId, e.StripeError?.Code ?? "unknown"); }
        }
        return Results.Ok(new { accessToken = await tokens.IssueAccessTokenAsync(user, db, ct), expiresInSeconds = 900 });
    }

    private static async Task<IResult> Webhook(HttpRequest request, IdentityDbContext db, IStripeBilling stripe,
        BillingOptions options, ILogger<BillingOptions> logger, CancellationToken ct)
    {
        if (options.WebhookSecret is null) return NotConfigured("Billing webhook isn't configured.");

        string json;
        using (var reader = new StreamReader(request.Body)) json = await reader.ReadToEndAsync(ct);
        var signature = request.Headers["Stripe-Signature"].ToString();

        Event evt;
        try { evt = EventUtility.ConstructEvent(json, signature, options.WebhookSecret, throwOnApiVersionMismatch: false); }
        catch (Exception e)
        {
            logger.LogWarning("Billing webhook signature check failed ({Kind}).", e.GetType().Name);
            return Results.BadRequest();
        }

        // Replay / duplicate guard: an event id we've already applied is acknowledged and ignored.
        if (await db.BillingEvents.AnyAsync(b => b.EventId == evt.Id, ct)) return Results.Ok();

        string? subscriptionId = evt.Data.Object switch
        {
            Session s when s.Mode == "subscription" => s.SubscriptionId,
            Stripe.Subscription s => s.Id,
            _ => null
        };

        try
        {
            if (subscriptionId is not null)
            {
                var snap = await stripe.GetSubscriptionAsync(subscriptionId, ct);
                if (snap is not null)
                    await BillingApplier.ApplyAsync(db, stripe, options, snap, DateTimeOffset.UtcNow, logger, ct);
            }
            db.BillingEvents.Add(new BillingEvent { EventId = evt.Id, Type = evt.Type });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique violation. If it was the event id, a concurrent delivery already applied this event: done.
            // If not, something else collided, so let Stripe retry rather than dropping the update.
            if (await db.BillingEvents.AsNoTracking().AnyAsync(b => b.EventId == evt.Id, ct)) return Results.Ok();
            logger.LogError("Billing webhook {Type} hit a unique-key conflict; Stripe will retry.", evt.Type);
            return Results.StatusCode(500);
        }
        catch (Exception e)
        {
            // 5xx makes Stripe retry later. Only the exception type is logged.
            logger.LogError("Billing webhook {Type} failed ({Kind}); Stripe will retry.", evt.Type, e.GetType().Name);
            return Results.StatusCode(500);
        }
        return Results.Ok();
    }
}

/// <summary>Writes a Stripe snapshot into our subscription row, applying <see cref="BillingSync"/>'s decision.</summary>
public static class BillingApplier
{
    public static async Task ApplyAsync(IdentityDbContext db, IStripeBilling stripe, BillingOptions options,
        StripeSubscriptionSnapshot snap, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var row = snap.UserId is { } uid
            ? await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == uid, ct)
            : await db.Subscriptions.FirstOrDefaultAsync(s => s.StripeCustomerId == snap.CustomerId, ct);

        if (row is null && snap.UserId is null)
        {
            logger.LogWarning("Billing event for a subscription with no known user; ignored.");
            return;
        }
        if (row is null)
        {
            row = new UserSubscription { UserId = snap.UserId!.Value, StripeCustomerId = snap.CustomerId };
            db.Subscriptions.Add(row);
        }

        // A late event about an OLDER, finished subscription must not overwrite a newer live one.
        var snapStatus = SubscriptionPolicy.MapStripeStatus(snap.StripeStatus);
        if (row.StripeSubscriptionId is not null && row.StripeSubscriptionId != snap.SubscriptionId
            && !SubscriptionPolicy.IsEntitled(snapStatus) && SubscriptionPolicy.IsEntitled(row.Status))
            return;

        var d = BillingSync.Decide(snap, row.Status, row.StripeSubscriptionId, row.TrialUsed, row.CardCheckedAtUtc is not null, now, options.MinCardMonths);
        if (d.CancelAtStripe)
        {
            await stripe.CancelSubscriptionAsync(snap.SubscriptionId, ct);
            logger.LogWarning("Cancelled a subscription whose card expires within {Months} months.", options.MinCardMonths);
        }
        else if (SubscriptionPolicy.IsEntitled(d.Status) && !d.CardChecked)
            logger.LogWarning("Couldn't read the card expiry for a subscription; it will be checked again on the next event.");

        row.StripeCustomerId = snap.CustomerId;
        row.StripeSubscriptionId = snap.SubscriptionId;
        row.Status = d.Status;
        row.TrialUsed = d.TrialUsed;
        row.TrialEndsAtUtc = snap.TrialEndUtc;
        row.CancelAtPeriodEnd = snap.CancelAtPeriodEnd;
        if (d.CardChecked && row.CardCheckedAtUtc is null) row.CardCheckedAtUtc = now;
        if (!d.CardChecked) row.CardCheckedAtUtc = null;
        row.UpdatedAtUtc = now;
    }
}
