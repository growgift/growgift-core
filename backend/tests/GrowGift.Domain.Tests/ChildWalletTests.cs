using FluentAssertions;
using GrowGift.Family.Domain;
using GrowGift.SharedKernel.Domain;
using Xunit;

namespace GrowGift.Domain.Tests;

public class ChildWalletTests
{
    [Fact]
    public void Reward_splits_equally_across_three_jars()
    {
        var wallet = ChildWallet.Open(Guid.NewGuid());
        wallet.DepositRewardSplit(Money.Of(9.00m, "AUD"), Guid.NewGuid()).IsSuccess.Should().BeTrue();

        wallet.BalanceOf(JarKind.ShortTerm).Amount.Should().Be(3.00m);
        wallet.BalanceOf(JarKind.MediumTerm).Amount.Should().Be(3.00m);
        wallet.BalanceOf(JarKind.LongTerm).Amount.Should().Be(3.00m);
    }

    [Fact]
    public void Remainder_cents_are_distributed_deterministically()
    {
        var wallet = ChildWallet.Open(Guid.NewGuid());
        wallet.DepositRewardSplit(Money.Of(1.00m, "AUD"), Guid.NewGuid());

        // 100c / 3 = 33c each + 1c remainder to the short-term jar.
        wallet.BalanceOf(JarKind.ShortTerm).Amount.Should().Be(0.34m);
        wallet.BalanceOf(JarKind.MediumTerm).Amount.Should().Be(0.33m);
        wallet.BalanceOf(JarKind.LongTerm).Amount.Should().Be(0.33m);

        var total = wallet.BalanceOf(JarKind.ShortTerm)
            .Add(wallet.BalanceOf(JarKind.MediumTerm))
            .Add(wallet.BalanceOf(JarKind.LongTerm));
        total.Amount.Should().Be(1.00m, "no cent may be created or destroyed");
    }

    [Fact]
    public void Locked_jar_rejects_withdrawal()
    {
        var wallet = ChildWallet.Open(Guid.NewGuid());
        wallet.DepositRewardSplit(Money.Of(30m, "AUD"), Guid.NewGuid());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = wallet.Withdraw(JarKind.LongTerm, Money.Of(1m, "AUD"), "toy", today, today);
        result.Error.Code.Should().Be("jar.locked");
    }
}
