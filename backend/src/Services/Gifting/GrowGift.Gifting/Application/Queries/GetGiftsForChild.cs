using GrowGift.Gifting.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Queries;

public sealed record ChildGiftDto(Guid GiftId, string Symbol, decimal Amount, string Currency, DateTimeOffset RedeemedAtUtc);

public sealed record GetGiftsForChildQuery(Guid ChildId) : IRequest<IReadOnlyList<ChildGiftDto>>;

/// <summary>
/// Distinct from GetReceivedGiftsQuery (which filters by RedeemedByUserId —
/// the adult who did the redeeming) because for a child beneficiary that's
/// always the parent, never the child themselves: children can't redeem on
/// their own (read-only accounts), so a parent always does it on their
/// behalf via BeneficiaryChildId. This is what backs a child's own "gifts
/// I've received" view, which needs the opposite filter.
/// </summary>
public sealed class GetGiftsForChildHandler(GiftingDbContext db)
    : IRequestHandler<GetGiftsForChildQuery, IReadOnlyList<ChildGiftDto>>
{
    public async Task<IReadOnlyList<ChildGiftDto>> Handle(GetGiftsForChildQuery request, CancellationToken ct) =>
        await db.Gifts.AsNoTracking()
            .Where(g => g.BeneficiaryChildId == request.ChildId && g.RedeemedAtUtc != null)
            .OrderByDescending(g => g.RedeemedAtUtc)
            .Join(db.GiftOrders.AsNoTracking(),
                g => g.OrderId, o => o.Id,
                (g, o) => new ChildGiftDto(
                    g.Id, o.Symbol, o.PurchaseAmount.Amount, o.PurchaseAmount.Currency, g.RedeemedAtUtc!.Value))
            .ToListAsync(ct);
}
