using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Gifting.Domain;

public enum GiftStatus { Issued = 0, Redeemed = 1, Expired = 2, Revoked = 3 }

/// <summary>
/// The redeemable side of an order. Holds the hashed code, expiry, single-use
/// enforcement and brute-force lockout counters. Redemption is additionally
/// guarded by Postgres xmin optimistic concurrency so a code can never be
/// claimed twice even under a race.
/// </summary>
public sealed class Gift : AggregateRoot<Guid>
{
    private const int MaxFailedAttempts = 10;

    private Gift() { } // EF

    public Guid OrderId { get; private set; }
    public string CodeHashHex { get; private set; } = default!;
    public GiftStatus Status { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public int FailedAttemptCount { get; private set; }
    public Guid? RedeemedByUserId { get; private set; }
    public Guid? BeneficiaryChildId { get; private set; }
    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    public static Gift Issue(Guid orderId, RedemptionCode code, TimeSpan validity)
    {
        var gift = new Gift
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CodeHashHex = code.HashHex,
            Status = GiftStatus.Issued,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(validity)
        };
        gift.Raise(new Events.CodeIssued(gift.Id, orderId, gift.ExpiresAtUtc));
        return gift;
    }

    public Result Redeem(string candidateCode, string salt, Guid recipientUserId,
        Guid? beneficiaryChildId, DateTimeOffset nowUtc)
    {
        if (Status == GiftStatus.Redeemed)
            return Result.Failure(Error.Conflict("gift.already_redeemed", "This gift has already been redeemed."));
        if (Status == GiftStatus.Revoked)
            return Result.Failure(Error.Conflict("gift.revoked", "This gift is no longer valid. Contact support."));
        if (Status == GiftStatus.Expired || nowUtc > ExpiresAtUtc)
            return Result.Failure(Error.Conflict("gift.expired", "This code has expired. The buyer can request a reissue."));
        if (FailedAttemptCount >= MaxFailedAttempts)
            return Result.Failure(Error.Forbidden("gift.locked", "Too many attempts. This code is locked; contact support."));

        var code = RedemptionCode.FromPlaintext(candidateCode, salt);
        if (!string.Equals(code.HashHex, CodeHashHex, StringComparison.OrdinalIgnoreCase))
        {
            FailedAttemptCount++;
            return Result.Failure(Error.Validation("gift.code_mismatch", "That code doesn't match. Check the card and try again."));
        }

        Status = GiftStatus.Redeemed;
        RedeemedByUserId = recipientUserId;
        BeneficiaryChildId = beneficiaryChildId;
        RedeemedAtUtc = nowUtc;
        Raise(new Events.Redeemed(Id, OrderId, recipientUserId, beneficiaryChildId));
        return Result.Success();
    }

    public Result Reissue(RedemptionCode newCode, TimeSpan validity)
    {
        if (Status == GiftStatus.Redeemed)
            return Result.Failure(Error.Conflict("gift.already_redeemed", "A redeemed gift cannot be reissued."));

        CodeHashHex = newCode.HashHex;
        Status = GiftStatus.Issued;
        ExpiresAtUtc = DateTimeOffset.UtcNow.Add(validity);
        FailedAttemptCount = 0;
        Raise(new Events.CodeIssued(Id, OrderId, ExpiresAtUtc));
        return Result.Success();
    }

    public static class Events
    {
        public sealed record CodeIssued(Guid GiftId, Guid OrderId, DateTimeOffset ExpiresAtUtc) : DomainEvent;
        public sealed record Redeemed(Guid GiftId, Guid OrderId, Guid RecipientUserId, Guid? BeneficiaryChildId) : DomainEvent;
    }
}
