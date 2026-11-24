using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Queries;

public sealed record GiftPreviewDto(
    string Symbol, decimal Amount, string Currency, string? BoxSku,
    string? GreetingMessage, string RecipientName, string Status);

public sealed record PreviewGiftQuery(string Code) : IRequest<Result<GiftPreviewDto>>;

/// <summary>
/// Lets someone with a code see what it actually contains before deciding
/// to log in or complete ViewTrade registration — deliberately public (no
/// auth), the same way checking a physical gift card's balance doesn't
/// require an account. Safe on the same basis RedeemGiftHandler already
/// relies on: codes are high-entropy CSPRNG values (see RedemptionCode),
/// so knowing one is itself the credential: this reveals only what someone
/// who already has the code could always see printed on the card.
///
/// Deliberately doesn't reveal the exact share quantity — that's only
/// known once Brokerage completes the actual purchase at redemption time
/// (buy-at-redemption model), not before. What this shows (symbol + dollar
/// amount) is the only thing actually knowable in advance; claiming an
/// exact share count here would be a number this system doesn't have yet,
/// same category of premature claim as the checkout confirmation page
/// originally made before that was fixed to track real status instead.
/// </summary>
public sealed class PreviewGiftHandler(GiftingDbContext db, ICodeSaltProvider salts)
    : IRequestHandler<PreviewGiftQuery, Result<GiftPreviewDto>>
{
    public async Task<Result<GiftPreviewDto>> Handle(PreviewGiftQuery request, CancellationToken ct)
    {
        var hash = Domain.RedemptionCode.FromPlaintext(request.Code, salts.CurrentSalt).HashHex;
        var gift = await db.Gifts.AsNoTracking().FirstOrDefaultAsync(g => g.CodeHashHex == hash, ct);

        if (gift is null)
        {
            // Same generic message RedeemGiftHandler uses for an unknown
            // code — never reveal whether a code exists at all.
            return Result.Failure<GiftPreviewDto>(Error.Validation(
                "gift.code_mismatch", "That code doesn't match. Check the card and try again."));
        }

        var order = await db.GiftOrders.AsNoTracking().FirstAsync(o => o.Id == gift.OrderId, ct);

        return new GiftPreviewDto(
            order.Symbol, order.PurchaseAmount.Amount, order.PurchaseAmount.Currency, order.BoxSku,
            order.GreetingMessage, order.RecipientName ?? "you", gift.Status.ToString());
    }
}
