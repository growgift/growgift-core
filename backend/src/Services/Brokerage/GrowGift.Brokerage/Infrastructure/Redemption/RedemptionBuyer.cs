using GrowGift.Brokerage.Infrastructure.Fx;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Brokerage.Infrastructure.Redemption;

public sealed record RedemptionRequest(Guid GiftId, Guid RecipientUserId, string Symbol, decimal Amount, string Currency, string? EventAccountRef);

/// <param name="Account">The ViewTrade account the order was sent to (null when none was).</param>
public sealed record BuyResult(string State, string OrderRef, UsdConversion? Usd, string? Account = null);

/// <summary>
/// Buys a redeemed gift into the recipient's ViewTrade account: link lookup → AUD→USD → Notional BUY.
/// Shared by the gift-redeemed consumer and the user-triggered retry so both behave identically.
/// Throws (nothing was sent, safe to retry) when the FX rate is unavailable or the order definitively wasn't placed.
/// </summary>
public sealed class RedemptionBuyer(
    Persistence.BrokerageDbContext db,
    IFxRateProvider fx,
    IViewTradeOrderService orders,
    ILogger<RedemptionBuyer> logger)
{
    /// <param name="keySuffix">"" for the first attempt. A retry after a definitive rejection uses a new suffix so the
    /// cached rejection isn't replayed; the order service's duplicate protection still applies to each key.</param>
    public async Task<BuyResult> BuyAsync(RedemptionRequest r, string keySuffix, CancellationToken ct)
    {
        var link = await db.OnboardingApplications.AsNoTracking()
            .Where(a => a.UserId == r.RecipientUserId)
            .Select(a => new { a.ViewTradeUmaUserId, a.ViewTradeAccountRef })
            .FirstOrDefaultAsync(ct);

        var account = RedemptionAccountPicker.Pick(link?.ViewTradeAccountRef, r.EventAccountRef);
        if (link?.ViewTradeUmaUserId is null || account is null)
        {
            logger.LogError("Gift {GiftId}: no usable ViewTrade user/account for recipient {UserId} (user stored: {HasUser}, account: {Account}) — nothing bought.",
                r.GiftId, r.RecipientUserId, link?.ViewTradeUmaUserId is not null, RedemptionAccountPicker.Mask(account));
            return new BuyResult("needs_attention", "", null);
        }

        // No trustworthy FX rate → throws before anything is sent.
        var usd = await fx.ToUsdAsync(r.Amount, r.Currency, ct);
        if (usd.UsdAmount <= 0) throw new InvalidOperationException($"Gift {r.GiftId}: converted amount is not positive.");

        logger.LogInformation("Gift {GiftId}: buying {Symbol} for USD {Usd} into account {Account}.",
            r.GiftId, r.Symbol, usd.UsdAmount, RedemptionAccountPicker.Mask(account));

        var outcome = await orders.PlaceNotionalBuyAsync(r.RecipientUserId, link.ViewTradeUmaUserId.Value, account,
            r.Symbol, usd.UsdAmount, $"gg-gift-{r.GiftId}{keySuffix}", ct);

        switch (outcome.State)
        {
            case OrderState.Failed:
                throw new InvalidOperationException($"Gift {r.GiftId}: ViewTrade order was not placed; will retry.");
            case OrderState.Unconfirmed:
                logger.LogError("Gift {GiftId}: couldn't confirm whether the ViewTrade order was placed — NOT retrying; reconcile at ViewTrade.", r.GiftId);
                return new BuyResult("unconfirmed", outcome.OrderRef ?? "", usd, account);
            case OrderState.Rejected:
                logger.LogError("Gift {GiftId}: ViewTrade rejected the order: {Reason}", r.GiftId, outcome.Message);
                return new BuyResult("rejected", "", usd, account);
            default:
                logger.LogInformation("Gift {GiftId}: ViewTrade order {OrderRef} {State}.", r.GiftId, outcome.OrderRef, outcome.State);
                return new BuyResult(outcome.State.ToString().ToLowerInvariant(), outcome.OrderRef ?? "", usd, account);
        }
    }
}
