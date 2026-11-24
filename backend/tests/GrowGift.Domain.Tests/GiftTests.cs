using FluentAssertions;
using GrowGift.Gifting.Domain;
using Xunit;

namespace GrowGift.Domain.Tests;

public class GiftTests
{
    private const string Salt = "test-pepper";

    private static (Gift Gift, string Code) IssuedGift()
    {
        var (code, plaintext) = RedemptionCode.Generate(Salt);
        return (Gift.Issue(Guid.NewGuid(), code, TimeSpan.FromDays(365)), plaintext);
    }

    [Fact]
    public void Redeeming_with_correct_code_succeeds_once()
    {
        var (gift, code) = IssuedGift();
        var userId = Guid.NewGuid();

        gift.Redeem(code, Salt, userId, null, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        gift.Status.Should().Be(GiftStatus.Redeemed);
        gift.RedeemedByUserId.Should().Be(userId);

        var second = gift.Redeem(code, Salt, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("gift.already_redeemed");
    }

    [Fact]
    public void Wrong_code_increments_lockout_counter()
    {
        var (gift, _) = IssuedGift();
        for (var i = 0; i < 10; i++)
            gift.Redeem("GG-2222-3333-4444", Salt, Guid.NewGuid(), null, DateTimeOffset.UtcNow);

        var locked = gift.Redeem("GG-2222-3333-4444", Salt, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        locked.Error.Code.Should().Be("gift.locked");
    }

    [Fact]
    public void Expired_gift_cannot_be_redeemed()
    {
        var (gift, code) = IssuedGift();
        var farFuture = DateTimeOffset.UtcNow.AddDays(400);
        gift.Redeem(code, Salt, Guid.NewGuid(), null, farFuture).Error.Code.Should().Be("gift.expired");
    }
}
