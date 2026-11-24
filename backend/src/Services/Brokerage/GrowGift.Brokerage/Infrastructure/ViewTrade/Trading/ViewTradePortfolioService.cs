using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

/// <param name="MarketValue">Null when ViewTrade reports no current value (e.g. value 0 and no last price, as when the market is closed).</param>
public sealed record PortfolioPosition(string Symbol, decimal Quantity, decimal? MarketValue, decimal? CostBasis, string Currency = "USD", decimal? AvgPrice = null, decimal? LastPrice = null, decimal? ChangePercent = null, decimal? ViewTradeGain = null, decimal? ViewTradeGainPercent = null)
{
    /// <summary>Unrealised P/L in currency; null unless both the market value and a positive cost are known.</summary>
    public decimal? UnrealisedPnl => MarketValue is null ? null : ViewTradeGain ?? (CostBasis is > 0 ? Math.Round(MarketValue.Value - CostBasis.Value, 2) : null);
    /// <summary>Unrealised P/L as a percent of cost.</summary>
    public decimal? UnrealisedPnlPercent => MarketValue is null ? null : ViewTradeGainPercent ?? (CostBasis is > 0 ? Math.Round((MarketValue.Value - CostBasis.Value) / CostBasis.Value * 100m, 4) : null);
}

/// <param name="Recognised">False when the reply parsed as JSON but no holdings list could be identified —
/// the shape isn't documented, so we say so rather than invent rows.</param>
public sealed record ViewTradePortfolio(IReadOnlyList<PortfolioPosition> Positions, decimal? Cash, bool Recognised, string? RawJson);

public sealed class ViewTradePortfolioException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = status;
}

public interface IViewTradePortfolioService
{
    /// <exception cref="ViewTradeSessionException">Couldn't establish the trading session (stage says where).</exception>
    /// <exception cref="ViewTradePortfolioException">ViewTrade refused or failed the portfolio request.</exception>
    Task<ViewTradePortfolio> GetAsync(Guid growGiftUserId, string email, CancellationToken ct);
}

/// <summary>
/// AccountPortfolio from the Postman Trading folder: GET api/user/portfolio on the Trading host with
/// `Authorization: SessionId &lt;id&gt;`. It takes no account parameter — ViewTrade answers for the session's
/// own user, so there is no account id for a caller to tamper with. A read-only GET, so the single
/// re-authenticate-and-retry after a 401/403 (or one plain retry would be) can't duplicate anything.
/// </summary>
public sealed class ViewTradePortfolioService(
    IHttpClientFactory httpFactory,
    IViewTradeSessionService sessions,
    ILogger<ViewTradePortfolioService> logger) : IViewTradePortfolioService
{
    public async Task<ViewTradePortfolio> GetAsync(Guid userId, string email, CancellationToken ct)
    {
        var session = await sessions.GetSessionAsync(userId, email, ct);
        var (status, body) = await GetOnceAsync(session, ct);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            sessions.Invalidate(userId);
            session = await sessions.GetSessionAsync(userId, email, ct);
            (status, body) = await GetOnceAsync(session, ct);
        }

        if ((int)status is < 200 or >= 300)
        {
            // Status only — never the body or the session.
            logger.LogWarning("ViewTrade portfolio request failed with HTTP {Status}.", (int)status);
            throw new ViewTradePortfolioException("ViewTrade portfolio request failed.", status);
        }

        var parsed = PortfolioParser.Parse(body);
        if (!parsed.Recognised)
            logger.LogWarning("ViewTrade portfolio reply was valid JSON but its holdings list wasn't recognised; the mapping needs a sample.");
        return new ViewTradePortfolio(parsed.Positions, parsed.Cash, parsed.Recognised, body);
    }

    private async Task<(HttpStatusCode, string)> GetOnceAsync(TradingSession session, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("ViewTradeTrading");
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/user/portfolio?loadQuotes=true&loadEarnings=true");
        req.Headers.TryAddWithoutValidation("Authorization", $"SessionId {session.SessionId}");
        try
        {
            using var resp = await client.SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new ViewTradePortfolioException("Couldn't reach ViewTrade.");
        }
    }
}

/// <summary>
/// Reader for GET api/user/portfolio?loadQuotes=true&loadEarnings=true (without loadQuotes ViewTrade returns value 0 and an
/// empty quote; with it, value / gain / gainPercent / quote.lastPrice / quote.changePercent are populated — see the saved Postman response). Mapped from the sample saved in Postman: the reply is a top-level JSON
/// array; each row has symbol, quantity, price (average cost per share), costValue (total cost), value (current
/// value, 0 when unpriced), currency and a nested quote.lastPrice. Wrapped lists are still accepted, and
/// anything unrecognisable is reported as such rather than invented.
/// </summary>
public static class PortfolioParser
{
    private static readonly string[] ListKeys = { "positions", "holdings", "portfolio", "data", "items", "results", "equities", "securities", "assets" };
    private static readonly string[] SymbolKeys = { "symbol", "ticker", "instrumentSymbol", "securitySymbol" };
    private static readonly string[] QuantityKeys = { "quantity", "preciseQuantity", "qty", "shares" };
    private static readonly string[] ValueKeys = { "preciseValue", "value", "marketValue", "currentValue" };
    private static readonly string[] CostKeys = { "costValue", "preciseCostValue", "costBasis" };
    private static readonly string[] CashKeys = { "cashBalance", "cash_balance", "cash", "availableCash" };

    public static (IReadOnlyList<PortfolioPosition> Positions, decimal? Cash, bool Recognised) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (Array.Empty<PortfolioPosition>(), null, false); }

        using (doc)
        {
            var root = doc.RootElement;
            var list = FindList(root, 0, out var viaKnownKey);
            decimal? cash = root.ValueKind == JsonValueKind.Object ? FindNumber(root, CashKeys) : null;
            if (list is null) return (Array.Empty<PortfolioPosition>(), cash, false);

            var rows = new List<PortfolioPosition>();
            foreach (var el in list.Value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var symbol = FindSymbol(el);
                var qty = FindNumber(el, QuantityKeys);
                if (string.IsNullOrWhiteSpace(symbol) || qty is null) continue;
                if (qty.Value == 0) continue;
                // Current value: the row's own value when ViewTrade priced it, else last price x quantity from the
                // nested quote; zero means "unpriced", not "worth nothing".
                var value = FindNumber(el, ValueKeys);
                decimal? lastPrice = null, changePct = null;
                if (TryGet(el, "quote", out var q))
                {
                    lastPrice = FindNumber(q, new[] { "lastPrice", "preciseLastPrice" });
                    if (lastPrice is <= 0) lastPrice = null;
                    changePct = FindNumber(q, new[] { "changePercent", "preciseChangePercent" });
                }
                if (value is null or <= 0)
                    value = lastPrice is > 0 ? Math.Round(lastPrice.Value * qty.Value, 2) : null;
                lastPrice ??= value is > 0 ? Math.Round(value.Value / qty.Value, 4) : null;
                var cur = TryGet(el, "currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "USD";
                rows.Add(new PortfolioPosition(symbol.Trim().ToUpperInvariant(), qty.Value, value,
                    FindNumber(el, CostKeys), cur, FindNumber(el, new[] { "price", "precisePrice" }), lastPrice, changePct,
                    value is null ? null : FindNumber(el, new[] { "preciseGain", "gain" }),
                    value is null ? null : FindNumber(el, new[] { "preciseGainPercent", "gainPercent" })));
            }

            // An empty list is only trusted as "no holdings" when it sits under a conventional key
            // (or is the whole reply); a non-empty list with no readable rows is NOT recognised.
            var recognised = rows.Count > 0 || (list.Value.GetArrayLength() == 0 && viaKnownKey);
            return (rows, cash, recognised);
        }
    }

    private static JsonElement? FindList(JsonElement el, int depth, out bool viaKnownKey)
    {
        viaKnownKey = true;
        if (el.ValueKind == JsonValueKind.Array) return el;
        if (el.ValueKind != JsonValueKind.Object || depth > 3) return null;

        foreach (var key in ListKeys)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (!p.NameEquals(key) && !p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
                if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    var inner = FindList(p.Value, depth + 1, out _);
                    if (inner is not null) return inner;
                }
            }
        }
        return null;
    }

    private static string? FindSymbol(JsonElement row)
    {
        foreach (var k in SymbolKeys)
            if (TryGet(row, k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        foreach (var parent in new[] { "quote", "instrument", "security" })
            if (TryGet(row, parent, out var o) && o.ValueKind == JsonValueKind.Object)
                foreach (var k in SymbolKeys)
                    if (TryGet(o, k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static decimal? FindNumber(JsonElement el, string[] names)
    {
        foreach (var n in names)
        {
            if (!TryGet(el, n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String &&
                decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) return s;
        }
        return null;
    }

    private static bool TryGet(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}
