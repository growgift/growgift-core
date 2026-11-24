using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Gifting.Infrastructure.Codes;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Auditing;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Commands;

/// <summary>
/// Redemption. The recipient must already be KYC-approved with a ViewTrade
/// account (enforced upstream by the gateway policy + Identity claims). The
/// lookup is by code hash so we never scan plaintext; the concurrency token
/// on Gift means two simultaneous redemptions of the same code cannot both
/// commit — one gets a DbUpdateConcurrencyException mapped to a conflict.
///
/// Returns the symbol and committed dollar amount — the only things known
/// synchronously at this point — not a share quantity. The actual purchase
/// happens afterward, asynchronously, once Brokerage's GiftRedeemedConsumer
/// processes the event enqueued below; claiming a specific share count here
/// would be a number this handler doesn't actually have.
/// </summary>
public sealed record RedeemGiftCommand(
    string Code,
    Guid RecipientUserId,
    Guid? BeneficiaryChildId,
    string ViewTradeAccountRef,
    string? IpAddress) : IRequest<Result<RedeemedGiftDto>>;

public sealed record RedeemedGiftDto(Guid GiftId, string Symbol, decimal Amount, string Currency);

public sealed class RedeemGiftHandler(
    GiftingDbContext db,
    ICodeSaltProvider salts,
    IEventPublisher publisher,
    IAuditWriter audit) : IRequestHandler<RedeemGiftCommand, Result<RedeemedGiftDto>>
{
    public async Task<Result<RedeemedGiftDto>> Handle(RedeemGiftCommand request, CancellationToken ct)
    {
        var hash = Domain.RedemptionCode.FromPlaintext(request.Code, salts.CurrentSalt).HashHex;
        var gift = await db.Gifts.FirstOrDefaultAsync(g => g.CodeHashHex == hash, ct);

        if (gift is null)
        {
            // Do not reveal whether the code exists; identical error to a mismatch.
            await audit.WriteAsync(request.RecipientUserId.ToString(), "gift.redeem_attempt_unknown_code",
                "gift", "unknown", metadata: null, ipAddress: request.IpAddress, ct);
            return Result.Failure<RedeemedGiftDto>(Error.Validation(
                "gift.code_mismatch", "That code doesn't match. Check the card and try again."));
        }

        var redeem = gift.Redeem(request.Code, salts.CurrentSalt,
            request.RecipientUserId, request.BeneficiaryChildId, DateTimeOffset.UtcNow);

        if (redeem.IsFailure)
        {
            await db.SaveChangesAsync(ct); // persist failed-attempt counter
            await audit.WriteAsync(request.RecipientUserId.ToString(), "gift.redeem_failed",
                "gift", gift.Id.ToString(), new { redeem.Error.Code }, request.IpAddress, ct);
            return Result.Failure<RedeemedGiftDto>(redeem.Error);
        }

        var order = await db.GiftOrders.FirstAsync(o => o.Id == gift.OrderId, ct);
        var marked = order.MarkRedeemed();
        if (marked.IsFailure) return Result.Failure<RedeemedGiftDto>(marked.Error);

        // Buy-at-redemption: the event carries the symbol and dollar amount so
        // Brokerage can purchase directly into the recipient's disclosed account.
        await publisher.EnqueueAsync(new GiftRedeemedV1(
            gift.Id, request.RecipientUserId, request.BeneficiaryChildId, request.ViewTradeAccountRef,
            order.Symbol, order.PurchaseAmount.Amount, order.PurchaseAmount.Currency),
            partitionKey: gift.Id.ToString(), ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<RedeemedGiftDto>(Error.Conflict(
                "gift.already_redeemed", "This gift has already been redeemed."));
        }

        await audit.WriteAsync(request.RecipientUserId.ToString(), "gift.redeemed",
            "gift", gift.Id.ToString(), new { gift.OrderId, request.BeneficiaryChildId }, request.IpAddress, ct);

        return new RedeemedGiftDto(gift.Id, order.Symbol, order.PurchaseAmount.Amount, order.PurchaseAmount.Currency);
    }
}
