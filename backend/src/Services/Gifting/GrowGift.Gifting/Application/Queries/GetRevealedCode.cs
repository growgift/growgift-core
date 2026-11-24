using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Queries;

public sealed record RevealedCodeDto(string Status, string? Code);

public sealed record GetRevealedCodeQuery(Guid OrderId, Guid BuyerId) : IRequest<Result<RevealedCodeDto>>;

/// <summary>
/// Backs the confirmation page's polling loop: the redemption code isn't
/// generated synchronously at checkout (see GiftOrderPaidConsumer — it only
/// runs once Stripe's webhook confirms payment, which can trail the
/// buyer's redirect back to the site by a few seconds), so the frontend
/// polls this until a code appears or the order's status moves past
/// something that could still produce one.
/// </summary>
public sealed class GetRevealedCodeHandler(GiftingDbContext db, IRevealedCodeCache codeCache)
    : IRequestHandler<GetRevealedCodeQuery, Result<RevealedCodeDto>>
{
    public async Task<Result<RevealedCodeDto>> Handle(GetRevealedCodeQuery request, CancellationToken ct)
    {
        var order = await db.GiftOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null)
            return Result.Failure<RevealedCodeDto>(Error.NotFound("gift_order.not_found", "No such order."));
        if (order.BuyerId != request.BuyerId)
            return Result.Failure<RevealedCodeDto>(Error.Forbidden("gift_order.forbidden", "This isn't your order."));

        var code = await codeCache.TryGetAsync(order.Id, ct);
        return new RevealedCodeDto(order.Status.ToString(), code);
    }
}
