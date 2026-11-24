using GrowGift.Gifting.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Queries;

public sealed record ReceivedGiftDto(
    Guid GiftId, Guid OrderId, string Symbol, decimal Amount, string Currency,
    DateTimeOffset RedeemedAtUtc);

public sealed record GetReceivedGiftsQuery(Guid RecipientUserId, int Page = 1, int PageSize = 20)
    : IRequest<IReadOnlyList<ReceivedGiftDto>>;

/// <summary>
/// The recipient side of a buyer's GetBuyerOrdersQuery — gifts this user has
/// redeemed, joined back to the order for the symbol/amount they claimed.
/// Gift.RedeemedByUserId is set once (Gift.Redeem), so this is a simple,
/// direct filter — no separate "received gifts" table needed.
/// </summary>
public sealed class GetReceivedGiftsHandler(GiftingDbContext db)
    : IRequestHandler<GetReceivedGiftsQuery, IReadOnlyList<ReceivedGiftDto>>
{
    public async Task<IReadOnlyList<ReceivedGiftDto>> Handle(GetReceivedGiftsQuery request, CancellationToken ct) =>
        await db.Gifts.AsNoTracking()
            .Where(g => g.RedeemedByUserId == request.RecipientUserId && g.RedeemedAtUtc != null)
            .OrderByDescending(g => g.RedeemedAtUtc)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(Math.Clamp(request.PageSize, 1, 100))
            .Join(db.GiftOrders.AsNoTracking(),
                g => g.OrderId, o => o.Id,
                (g, o) => new ReceivedGiftDto(
                    g.Id, o.Id, o.Symbol, o.PurchaseAmount.Amount, o.PurchaseAmount.Currency,
                    g.RedeemedAtUtc!.Value))
            .ToListAsync(ct);
}
