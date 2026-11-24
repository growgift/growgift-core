using System.Net.Http.Json;
using GrowGift.Brokerage.Domain;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade;

/// <summary>
/// Thrown when ViewTrade rejects a call. Carries the real response body —
/// against a partner API we don't have public docs for, that body is often
/// the only way to learn what a request actually got wrong (bad field name,
/// unrecognized account ref, auth rejected, etc.).
/// </summary>
public sealed class ViewTradeApiException(System.Net.HttpStatusCode statusCode, string body)
    : Exception($"ViewTrade returned {(int)statusCode} {statusCode}: {body}")
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = body;
}

/// <summary>
/// ViewTrade REST adapter (Fully Disclosed Model). The HttpClient is
/// configured in Program.cs with: mTLS client certificate, Polly retry with
/// decorrelated jitter for 5xx/timeouts, and a circuit breaker. ViewTrade's
/// clientOrderId field carries our clientRef, which ViewTrade dedupes —
/// making acquisition and transfer calls idempotent end-to-end.
/// </summary>
public sealed class ViewTradeProvider(HttpClient http) : IBrokerageProvider
{
    public string ProviderKey => "viewtrade";

    /// <summary>
    /// Confirms the base URL and credentials actually work, independent of
    /// any GrowGift domain logic — hits GET {PingPath} and returns whatever
    /// comes back (status + a truncated body) rather than assuming a shape,
    /// since we're configuring against real credentials for the first time.
    /// </summary>
    public async Task<(int StatusCode, string Body)> PingAsync(string path, CancellationToken ct)
    {
        var response = await http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, body.Length > 2000 ? body[..2000] + "…(truncated)" : body);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ViewTradeApiException(response.StatusCode, body);
    }

    public async Task<AcquisitionResult> AcquireForOmnibusAsync(
        string clientRef, string symbol, decimal dollarAmount, string currency, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/v1/orders", new
        {
            clientOrderId = clientRef,               // idempotency key at the broker
            account = "GROWGIFT-OMNIBUS",
            symbol,
            side = "BUY",
            orderType = "MARKET",
            notional = new { amount = dollarAmount, currency }
        }, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ViewTradeOrderResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade order response.");

        return new AcquisitionResult(body.LotRef, body.FilledQuantity, body.AverageFillPrice);
    }

    public async Task<AcquisitionResult> BuyForAccountAsync(
        string accountRef, string symbol, decimal notionalAmount, string currency,
        string clientRef, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/v1/orders", new
        {
            clientOrderId = clientRef,               // idempotency key at the broker
            account = accountRef,                    // recipient's disclosed account
            symbol,
            side = "BUY",
            orderType = "MARKET",
            notional = new { amount = notionalAmount, currency }
        }, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ViewTradeOrderResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade order response.");
        return new AcquisitionResult(body.LotRef, body.FilledQuantity, body.AverageFillPrice);
    }

    public async Task<AccountResult> OpenAccountAsync(AccountApplication application, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/v1/accounts", new
        {
            externalId = application.UserId,
            givenName = application.GivenName,
            familyName = application.FamilyName,
            dateOfBirth = application.DateOfBirth.ToString("yyyy-MM-dd"),
            address = application.ResidentialAddress,
            country = application.CountryCode,
            tfnToken = application.TaxFileNumberToken,   // tokenized — raw TFN never transits our services
            kycVendorRef = application.PersonaInquiryRef
        }, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ViewTradeAccountResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade account response.");
        return new AccountResult(body.AccountRef, body.Status);
    }

    public async Task<TransferResult> TransferFromOmnibusAsync(
        string clientRef, string omnibusLotRef, string destinationAccountRef, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/v1/transfers", new
        {
            clientTransferId = clientRef,
            sourceAccount = "GROWGIFT-OMNIBUS",
            sourceLotRef = omnibusLotRef,
            destinationAccount = destinationAccountRef,
            type = "JOURNAL"
        }, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ViewTradeTransferResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade transfer response.");
        return new TransferResult(body.TransferRef, body.Status);
    }

    public async Task<Portfolio> GetPortfolioAsync(string accountRef, CancellationToken ct)
    {
        var response = await http.GetAsync(
            $"api/v1/accounts/{Uri.EscapeDataString(accountRef)}/portfolio", ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ViewTradePortfolioResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty ViewTrade portfolio response.");

        return new Portfolio(accountRef,
            body.Positions.Select(p => new Holding(p.Symbol, p.Quantity, p.MarketValue, p.CostBasis)).ToList(),
            body.CashBalance);
    }

    private sealed record ViewTradeOrderResponse(string LotRef, decimal FilledQuantity, decimal AverageFillPrice);
    private sealed record ViewTradeAccountResponse(string AccountRef, string Status);
    private sealed record ViewTradeTransferResponse(string TransferRef, string Status);
    private sealed record ViewTradePortfolioResponse(List<ViewTradePosition> Positions, decimal CashBalance);
    private sealed record ViewTradePosition(string Symbol, decimal Quantity, decimal MarketValue, decimal CostBasis);
}
