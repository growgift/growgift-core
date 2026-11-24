using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace GrowGift.Brokerage.Infrastructure.Fx;

/// <summary>A gift amount converted to USD, with the rate used (kept for the audit trail).</summary>
public sealed record UsdConversion(decimal UsdAmount, decimal Rate, string Source);

public sealed class FxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Converts a gift's currency into USD (ViewTrade accounts trade in USD). Failing to get a trustworthy
/// rate THROWS — the caller must not place an order on a guess; nothing has been sent, so a retry is safe.
/// </summary>
public interface IFxRateProvider
{
    Task<UsdConversion> ToUsdAsync(decimal amount, string currency, CancellationToken ct);
}

public sealed record FxOptions(
    string BaseUrl,
    int CacheMinutes = 10,
    IReadOnlyDictionary<string, decimal>? FixedRates = null,
    decimal MinRate = 0.0001m,
    decimal MaxRate = 1000m)
{
    /// <summary>Tight sanity band for the currency GrowGift actually sells in: a 10x error (feed glitch) must not become an order.</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Min, decimal Max)> Bands =
        new Dictionary<string, (decimal, decimal)> { ["AUD"] = (0.30m, 1.20m) };
}

/// <summary>
/// Default source: Frankfurter (ECB-based, no key) — GET {BaseUrl}/v2/rate/{from}/usd → { "rate": n }.
/// Only the `rate` field is relied on. The URL is configuration, so any provider exposing that shape (or
/// a fixed rate, for tests/dev) can replace it. A per-currency sanity band rejects absurd values.
/// </summary>
public sealed class FxRateProvider(IHttpClientFactory httpFactory, FxOptions options, TimeProvider? clock = null) : IFxRateProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (decimal Rate, DateTimeOffset At)> _cache = new();

    public async Task<UsdConversion> ToUsdAsync(decimal amount, string currency, CancellationToken ct)
    {
        var ccy = currency.Trim().ToUpperInvariant();
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (ccy.Length != 3 || !ccy.All(char.IsAsciiLetterUpper))
            throw new FxUnavailableException("Unsupported currency code.");

        if (ccy == "USD") return new UsdConversion(RoundDown(amount), 1m, "none");

        if (options.FixedRates is { } fixedRates && fixedRates.TryGetValue(ccy, out var fixedRate))
            return Build(amount, Check(ccy, fixedRate), "fixed");

        if (_cache.TryGetValue(ccy, out var c) && _clock.GetUtcNow() - c.At < TimeSpan.FromMinutes(options.CacheMinutes))
            return Build(amount, c.Rate, "frankfurter(cached)");

        decimal rate;
        try
        {
            var http = httpFactory.CreateClient("Fx");
            using var resp = await http.GetAsync($"v2/rate/{ccy.ToLowerInvariant()}/usd", ct);
            if (!resp.IsSuccessStatusCode) throw new FxUnavailableException($"FX source returned HTTP {(int)resp.StatusCode}.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("rate", out var r) ||
                r.ValueKind != JsonValueKind.Number || !r.TryGetDecimal(out rate))
                throw new FxUnavailableException("FX source reply had no readable rate.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new FxUnavailableException("Couldn't fetch an FX rate.", ex);
        }

        Check(ccy, rate);
        _cache[ccy] = (rate, _clock.GetUtcNow());
        return Build(amount, rate, "frankfurter");
    }

    private decimal Check(string ccy, decimal rate)
    {
        var (min, max) = FxOptions.Bands.TryGetValue(ccy, out var band) ? band : (options.MinRate, options.MaxRate);
        return rate >= min && rate <= max
            ? rate : throw new FxUnavailableException("FX rate outside the allowed range; refusing to trade on it.");
    }

    private static UsdConversion Build(decimal amount, decimal rate, string source) => new(RoundDown(amount * rate), rate, source);

    /// <summary>Cents, rounded DOWN so we never spend more than the gift is worth.</summary>
    internal static decimal RoundDown(decimal v) => Math.Floor(v * 100m) / 100m;
}
