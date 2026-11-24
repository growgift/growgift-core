using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Family.Domain;

public enum JarKind { ShortTerm = 1, MediumTerm = 3, LongTerm = 10 }

/// <summary>
/// The wallet owns exactly three jars. Deposits are always split three ways
/// with remainder cents assigned deterministically (short → medium → long) so
/// the split is exact to the cent and reproducible in an audit. All movements
/// append to an immutable ledger; balances are derived, never set.
/// </summary>
public sealed class ChildWallet : AggregateRoot<Guid>
{
    private readonly List<JarEntry> _ledger = [];

    private ChildWallet() { }

    public Guid ChildId { get; private set; }
    public string Currency { get; private set; } = default!;
    public IReadOnlyList<JarEntry> Ledger => _ledger.AsReadOnly();

    public static ChildWallet Open(Guid childId, string currency = "AUD") =>
        new() { Id = Guid.NewGuid(), ChildId = childId, Currency = currency };

    public Money BalanceOf(JarKind jar) =>
        _ledger.Where(e => e.Jar == jar)
            .Aggregate(Money.Zero(Currency), (acc, e) => acc.Add(e.Amount));

    public Result DepositRewardSplit(Money reward, Guid sourceTaskId)
    {
        if (reward.Currency != Currency)
            return Result.Failure(Error.Validation("wallet.currency", "Reward currency must match the wallet."));
        if (reward.IsZero || reward.IsNegative)
            return Result.Failure(Error.Validation("wallet.amount", "Reward must be positive."));

        // Split to whole cents; distribute remainder deterministically.
        var totalCents = (long)decimal.Round(reward.Amount * 100m, 0, MidpointRounding.ToEven);
        var baseCents = totalCents / 3;
        var remainder = totalCents - baseCents * 3;

        var jars = new[] { JarKind.ShortTerm, JarKind.MediumTerm, JarKind.LongTerm };
        for (var i = 0; i < jars.Length; i++)
        {
            var cents = baseCents + (i < remainder ? 1 : 0);
            if (cents == 0) continue;
            _ledger.Add(JarEntry.Deposit(Id, jars[i], Money.Of(cents / 100m, Currency),
                $"task:{sourceTaskId}"));
        }

        Raise(new Events.RewardDeposited(Id, ChildId, reward.Amount, Currency, sourceTaskId));
        return Result.Success();
    }

    public Result Withdraw(JarKind jar, Money amount, string reason, DateOnly today, DateOnly jarOpenedOn)
    {
        var lockYears = (int)jar;
        if (jarOpenedOn.AddYears(lockYears) > today)
            return Result.Failure(Error.Forbidden("jar.locked",
                $"The {jar} jar unlocks {lockYears} year(s) after opening."));

        if (BalanceOf(jar).CompareTo(amount) < 0)
            return Result.Failure(Error.Validation("jar.insufficient", "Insufficient jar balance."));

        _ledger.Add(JarEntry.Withdrawal(Id, jar, amount, reason));
        return Result.Success();
    }

    public static class Events
    {
        public sealed record RewardDeposited(
            Guid WalletId, Guid ChildId, decimal Amount, string Currency, Guid TaskId) : DomainEvent;
    }
}

public sealed class JarEntry
{
    private JarEntry() { }

    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public JarKind Jar { get; private set; }
    public Money Amount { get; private set; } = default!;   // negative = withdrawal
    public string Reference { get; private set; } = default!;
    public DateTimeOffset AtUtc { get; private set; }

    public static JarEntry Deposit(Guid walletId, JarKind jar, Money amount, string reference) =>
        new() { Id = Guid.NewGuid(), WalletId = walletId, Jar = jar, Amount = amount, Reference = reference, AtUtc = DateTimeOffset.UtcNow };

    public static JarEntry Withdrawal(Guid walletId, JarKind jar, Money amount, string reference) =>
        new() { Id = Guid.NewGuid(), WalletId = walletId, Jar = jar, Amount = amount.Multiply(-1), Reference = reference, AtUtc = DateTimeOffset.UtcNow };
}
