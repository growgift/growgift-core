using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Queries;

public sealed record BuyerOrderDto(
    Guid Id, string Symbol, decimal Amount, string Currency, string? RecipientName,
    string Status, bool HasPhysicalBox, string? BoxSku, string DeliveryMethod, decimal TotalAmount,
    DateTimeOffset CreatedAtUtc, string? Code);

public sealed record GetBuyerOrdersQuery(Guid BuyerId, int Page = 1, int PageSize = 20)
    : IRequest<IReadOnlyList<BuyerOrderDto>>;

public sealed class GetBuyerOrdersHandler(GiftingDbContext db, IRevealedCodeCache codeCache)
    : IRequestHandler<GetBuyerOrdersQuery, IReadOnlyList<BuyerOrderDto>>
{
    public async Task<IReadOnlyList<BuyerOrderDto>> Handle(GetBuyerOrdersQuery request, CancellationToken ct)
    {
        var orders = await db.GiftOrders.AsNoTracking()
            .Where(o => o.BuyerId == request.BuyerId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(Math.Clamp(request.PageSize, 1, 100))
            .Select(o => new
            {
                o.Id,
                o.Symbol,
                Amount = o.PurchaseAmount.Amount,
                Currency = o.PurchaseAmount.Currency,
                o.RecipientName,
                Status = o.Status.ToString(),
                HasPhysicalBox = o.BoxSku != null,
                o.BoxSku,
                DeliveryMethod = o.DeliveryMethod.ToString(),
                TotalAmount = o.PurchaseAmount.Amount
                    + (o.BoxPrice != null ? o.BoxPrice.Amount : 0m)
                    + (o.PostalFee != null ? o.PostalFee.Amount : 0m)
                    + (o.ServiceFee != null ? o.ServiceFee.Amount : 0m),
                o.CreatedAtUtc
            })
            .ToListAsync(ct);

        // One Redis GET per order — trivial at this page size (max 100), and
        // simpler than a batched MGET for the volume this ever sees. Only
        // orders that actually got a code issued (Paid or later) will have
        // one cached; earlier statuses correctly get null here.
        var result = new List<BuyerOrderDto>(orders.Count);
        foreach (var o in orders)
        {
            var code = await codeCache.TryGetAsync(o.Id, ct);
            result.Add(new BuyerOrderDto(
                o.Id, o.Symbol, o.Amount, o.Currency, o.RecipientName,
                o.Status, o.HasPhysicalBox, o.BoxSku, o.DeliveryMethod, o.TotalAmount, o.CreatedAtUtc, code));
        }
        return result;
    }
}
