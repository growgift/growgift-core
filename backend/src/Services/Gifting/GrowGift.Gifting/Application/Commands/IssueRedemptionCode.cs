using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Gifting.Domain;
using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Commands;

/// <summary>
/// Triggered when the order is paid (buy-at-redemption model: no shares are
/// purchased up front — the code simply encodes a claim on the chosen symbol
/// and dollar amount). Generates the single-use code, stores only its hash,
/// and hands the plaintext to Fulfillment/Notifications exactly once via the
/// return value — it is never persisted.
/// </summary>
public sealed record IssueRedemptionCodeCommand(Guid OrderId) : IRequest<Result<IssuedCode>>;

public sealed record IssuedCode(Guid GiftId, string PlaintextCode, DateTimeOffset ExpiresAtUtc);

public sealed class IssueRedemptionCodeHandler(
    GiftingDbContext db,
    ICodeSaltProvider salts,
    IEventPublisher publisher) : IRequestHandler<IssueRedemptionCodeCommand, Result<IssuedCode>>
{
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(365);

    public async Task<Result<IssuedCode>> Handle(IssueRedemptionCodeCommand request, CancellationToken ct)
    {
        var order = await db.GiftOrders.FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null)
            return Result.Failure<IssuedCode>(Error.NotFound("gift_order.not_found", "Order not found."));

        var existing = await db.Gifts.AnyAsync(g => g.OrderId == order.Id, ct);
        if (existing)
            return Result.Failure<IssuedCode>(Error.Conflict("gift.exists", "A code was already issued for this order."));

        var (code, plaintext) = RedemptionCode.Generate(salts.CurrentSalt);
        var gift = Gift.Issue(order.Id, code, DefaultValidity);

        var issued = order.MarkCodeIssued();
        if (issued.IsFailure) return Result.Failure<IssuedCode>(issued.Error);

        db.Gifts.Add(gift);
        await publisher.EnqueueAsync(new RedemptionCodeIssuedV1(
            order.Id, gift.Id, gift.CodeHashHex, gift.ExpiresAtUtc),
            partitionKey: order.Id.ToString(), ct);

        await db.SaveChangesAsync(ct);
        return new IssuedCode(gift.Id, plaintext, gift.ExpiresAtUtc);
    }
}
