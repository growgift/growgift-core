namespace GrowGift.Brokerage.Domain;

/// <summary>
/// Broker-agnostic port. ViewTrade is the first adapter; adding a second
/// broker means implementing this interface and registering it in the
/// provider registry keyed by market/region. Every call takes a client
/// reference (our order/gift id) which the adapter maps to the broker's
/// idempotency mechanism, so retried calls never double-execute.
/// </summary>
public interface IBrokerageProvider
{
    string ProviderKey { get; }

    /// <summary>Buy $amount of symbol into the GrowGift omnibus account.</summary>
    Task<AcquisitionResult> AcquireForOmnibusAsync(
        string clientRef, string symbol, decimal dollarAmount, string currency, CancellationToken ct);

    /// <summary>Open a fully disclosed account for a KYC-approved recipient.</summary>
    Task<AccountResult> OpenAccountAsync(AccountApplication application, CancellationToken ct);

    /// <summary>
    /// Buy-at-redemption model: market-buy a notional dollar amount of
    /// <paramref name="symbol"/> directly into the recipient's fully
    /// disclosed account. <paramref name="clientRef"/> is the broker-side
    /// idempotency key (safe to retry).
    /// </summary>
    Task<AcquisitionResult> BuyForAccountAsync(
        string accountRef, string symbol, decimal notionalAmount, string currency,
        string clientRef, CancellationToken ct);

    /// <summary>Move a lot from the omnibus account to the recipient's account (journal transfer).</summary>
    Task<TransferResult> TransferFromOmnibusAsync(
        string clientRef, string omnibusLotRef, string destinationAccountRef, CancellationToken ct);

    Task<Portfolio> GetPortfolioAsync(string accountRef, CancellationToken ct);
}

public sealed record AcquisitionResult(string OmnibusLotRef, decimal Quantity, decimal AverageFillPrice);
public sealed record AccountResult(string AccountRef, string Status);
public sealed record TransferResult(string TransferRef, string Status);
public sealed record AccountApplication(
    Guid UserId, string GivenName, string FamilyName, DateOnly DateOfBirth,
    string ResidentialAddress, string CountryCode, string TaxFileNumberToken, string PersonaInquiryRef);
public sealed record Portfolio(string AccountRef, IReadOnlyList<Holding> Holdings, decimal CashBalance);
public sealed record Holding(string Symbol, decimal Quantity, decimal MarketValue, decimal CostBasis);
