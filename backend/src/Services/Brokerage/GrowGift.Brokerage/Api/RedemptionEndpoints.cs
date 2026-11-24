using System.Security.Claims;
using GrowGift.Brokerage.Infrastructure.Persistence;
using GrowGift.Brokerage.Infrastructure.Redemption;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Brokerage.Api;

/// <summary>
/// The caller's own share purchases from redeemed gifts, and a retry for the ones where nothing was placed.
/// Everything is scoped to the verified user id from the JWT — a gift id for someone else simply isn't found.
/// </summary>
public static class RedemptionEndpoints
{
    // States where ViewTrade definitively has no order, so buying again can't double-buy.
    private static string? RetryKeySuffix(string state, bool confirmedNoOrder = false) => state switch
    {
        // Development only, and only when the person says they checked ViewTrade and no order exists: an
        // unconfirmed order MAY exist, so this is never offered in production.
        "unconfirmed" when confirmedNoOrder => "-r2",
        "needs_attention" => "",      // nothing was ever sent; the original idempotency key is still unused
        "rejected" => "-r1",          // 4xx refusal; a fresh key so the cached rejection isn't replayed
        _ => null                     // accepted/pending/filled/unconfirmed: an order may exist — never resend
    };

    public static IEndpointRouteBuilder MapRedemptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/trading/redemptions", List)
            .RequireAuthorization("Applicant")
            .WithSummary("The caller's share purchases from redeemed gifts, with their ViewTrade order state");
        app.MapPost("/api/v1/trading/redemptions/{giftId:guid}/retry", Retry)
            .RequireAuthorization("Applicant")
            .WithSummary("Retry the purchase for one of the caller's redeemed gifts when no order was placed");
        return app;
    }

    private static bool TryUser(ClaimsPrincipal user, out Guid id) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out id);

    private static async Task<IResult> List(ClaimsPrincipal user, IHostEnvironment env, BrokerageDbContext db, CancellationToken ct)
    {
        if (!TryUser(user, out var userId)) return Results.Unauthorized();
        var rows = await db.RedemptionPurchases.AsNoTracking()
            .Where(p => p.RecipientUserId == userId)
            .OrderByDescending(p => p.PurchasedAtUtc)
            .Take(50)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(p => new
        {
            giftId = p.GiftId,
            symbol = p.Symbol,
            amount = p.NotionalAmount,
            currency = p.Currency,
            usdAmount = p.UsdAmount,
            state = p.OrderState,
            orderRef = p.BrokerOrderRef,
            account = RedemptionAccountPicker.Mask(p.AccountRef),
            canRetry = RetryKeySuffix(p.OrderState) is not null,
            canRetryIfNoOrderExists = env.IsDevelopment() && p.OrderState == "unconfirmed",
            purchasedAtUtc = p.PurchasedAtUtc
        }));
    }

    private static async Task<IResult> Retry(Guid giftId, bool? confirmNoOrder, IHostEnvironment env, ClaimsPrincipal user, BrokerageDbContext db,
        RedemptionBuyer buyer, ILogger<RedemptionBuyer> logger, CancellationToken ct)
    {
        if (!TryUser(user, out var userId)) return Results.Unauthorized();

        var row = await db.RedemptionPurchases.FirstOrDefaultAsync(p => p.GiftId == giftId && p.RecipientUserId == userId, ct);
        if (row is null) return Results.NotFound();

        var suffix = RetryKeySuffix(row.OrderState, confirmNoOrder == true && env.IsDevelopment());
        if (suffix is null)
            return Results.Json(new { error = new { code = "redemption.not_retryable", message = "An order may already exist at ViewTrade for this gift, so it can't be bought again here." } }, statusCode: 409);

        try
        {
            var r = await buyer.BuyAsync(new RedemptionRequest(row.GiftId, row.RecipientUserId, row.Symbol, row.NotionalAmount, row.Currency, row.AccountRef), suffix, ct);
            row.OrderState = r.State;
            if (r.Account is not null) row.AccountRef = r.Account;
            row.BrokerOrderRef = r.OrderRef;
            row.UsdAmount = r.Usd?.UsdAmount ?? row.UsdAmount;
            row.FxRate = r.Usd?.Rate ?? row.FxRate;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { giftId, state = row.OrderState, orderRef = row.BrokerOrderRef });
        }
        catch (ViewTradeSessionException ex)
        {
            logger.LogWarning("Redemption retry for gift {GiftId}: trading session failed at {Stage} (HTTP {Status}).", giftId, ex.Stage, (int?)ex.StatusCode);
            return Results.Json(new { error = new { code = $"redemption.session_failed.{ex.Stage.ToString().ToLowerInvariant()}", message = "We couldn't sign in to ViewTrade. Nothing was bought; please try again." } }, statusCode: 502);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Fx.FxUnavailableException)
        {
            logger.LogWarning("Redemption retry for gift {GiftId} not placed: {Reason}", giftId, ex.GetType().Name);
            return Results.Json(new { error = new { code = "redemption.not_placed", message = "The order wasn't placed. Nothing was bought; please try again shortly." } }, statusCode: 502);
        }
    }
}
