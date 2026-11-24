namespace GrowGift.SharedKernel.Domain;

/// <summary>
/// Money is stored as decimal with an ISO-4217 currency code. All financial
/// arithmetic goes through this type; mixing currencies throws.
/// </summary>
public sealed class Money : ValueObject, IComparable<Money>
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO-4217 code.", nameof(currency));
        return new Money(decimal.Round(amount, 4, MidpointRounding.ToEven), currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public Money Add(Money other) => Apply(other, (a, b) => a + b);
    public Money Subtract(Money other) => Apply(other, (a, b) => a - b);
    public Money Multiply(decimal factor) => Of(Amount * factor, Currency);

    public bool IsNegative => Amount < 0m;
    public bool IsZero => Amount == 0m;

    private Money Apply(Money other, Func<decimal, decimal, decimal> op)
    {
        if (other.Currency != Currency)
            throw new InvalidOperationException($"Cannot combine {Currency} with {other.Currency}.");
        return Of(op(Amount, other.Amount), Currency);
    }

    public int CompareTo(Money? other)
    {
        if (other is null) return 1;
        if (other.Currency != Currency)
            throw new InvalidOperationException($"Cannot compare {Currency} with {other.Currency}.");
        return Amount.CompareTo(other.Amount);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Amount:0.####} {Currency}";
}
