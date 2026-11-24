using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

/// <param name="OrderRef">ViewTrade's reference for the order ("ourRef" in the reply).</param>
/// <param name="Side">BUY or SELL ("transaction").</param>
/// <param name="ExpectedValue">Dollar amount of a Notional order, in <paramref name="Currency"/>.</param>
/// <param name="Quantity">Estimated share quantity for a dollar order; the real figure is known once it fills.</param>
public sealed record OpenOrder(
    string OrderRef, string Symbol, string Side, string OrderType, string? Validity, string Currency,
    decimal? ExpectedValue, decimal? Quantity, decimal? RemainingQuantity, decimal? ExecutedQuantity,
    decimal? ExpectedPrice, DateTimeOffset? CreatedUtc, string? CreatedRaw, bool? Cancelable);

public sealed record ViewTradeOpenOrders(IReadOnlyList<OpenOrder> Orders, bool Recognised);

public interface IViewTradeOpenOrdersService
{
    /// <exception cref="ViewTradeSessionException">Couldn't establish the trading session.</exception>
    /// <exception cref="ViewTradePortfolioException">ViewTrade refused or failed the request.</exception>
    Task<ViewTradeOpenOrders> GetAsync(Guid growGiftUserId, string email, CancellationToken ct);
}

/// <summary>
/// OpenOrders from the Postman Trading folder: GET api/orders/open with `Authorization: SessionId …`.
/// No account parameter — ViewTrade answers for the session's own user. Read-only, so one re-authenticate
/// and re-read after a 401/403 can't duplicate anything. The order status vocabulary isn't documented
/// (the sample has status "B"), so every row is simply "open" and the raw code is never shown or interpreted.
/// </summary>
public sealed class ViewTradeOpenOrdersService(
    IHttpClientFactory httpFactory,
    IViewTradeSessionService sessions,
    ILogger<ViewTradeOpenOrdersService> logger) : IViewTradeOpenOrdersService
{
    public async Task<ViewTradeOpenOrders> GetAsync(Guid userId, string email, CancellationToken ct)
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
            logger.LogWarning("ViewTrade open-orders request failed with HTTP {Status}.", (int)status);
            throw new ViewTradePortfolioException("ViewTrade open-orders request failed.", status);
        }

        var parsed = OpenOrdersParser.Parse(body);
        if (!parsed.Recognised)
            logger.LogWarning("ViewTrade open-orders reply wasn't recognised; reply shape: {Shape}", ViewTradeOrderService.DescribeShape(body));
        return parsed;
    }

    private async Task<(HttpStatusCode, string)> GetOnceAsync(TradingSession session, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("ViewTradeTrading");
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/orders/open");
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
/// Reads the saved Postman response: a top-level array; each order has symbol, ourRef, transaction, orderType,
/// validity, expectedValue, quantity, remainingQty, execQty, expectedPx, createDate ("10/25/2026 02:53:43 EDT")
/// and cancelable. Rows without a symbol or reference are skipped; a non-empty array with no readable row is
/// "not recognised" rather than an empty list.
/// </summary>
public static class OpenOrdersParser
{
    public static ViewTradeOpenOrders Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new ViewTradeOpenOrders(Array.Empty<OpenOrder>(), false); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "orders", "data", "items" })
                    if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
            if (root.ValueKind != JsonValueKind.Array) return new ViewTradeOpenOrders(Array.Empty<OpenOrder>(), false);

            var rows = new List<OpenOrder>();
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var symbol = Str(el, "symbol") ?? (el.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.Object ? Str(q, "symbol") : null);
                var orderRef = Str(el, "ourRef") ?? Str(el, "orderRef");
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(orderRef)) continue;

                var created = Str(el, "createDate");
                rows.Add(new OpenOrder(
                    orderRef, symbol.Trim().ToUpperInvariant(),
                    (Str(el, "transaction") ?? "").Trim().ToUpperInvariant() is { Length: > 0 } t ? t : "UNKNOWN",
                    Str(el, "orderType") ?? "", Str(el, "validity"), Str(el, "tradeCurrency") ?? "USD",
                    Num(el, "preciseExpectedValue") ?? Num(el, "expectedValue"),
                    Num(el, "preciseQuantity") ?? Num(el, "quantity"),
                    Num(el, "preciseRemainingQty") ?? Num(el, "remainingQty"),
                    Num(el, "execQty"),
                    Num(el, "preciseExpectedPx") ?? Num(el, "expectedPx"),
                    ParseViewTradeTime(created), created,
                    el.TryGetProperty("cancelable", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null));
            }
            var recognised = rows.Count > 0 || root.GetArrayLength() == 0;
            return new ViewTradeOpenOrders(rows, recognised);
        }
    }

    /// <summary>"10/25/2026 02:53:43 EDT" → UTC. Only EDT/EST/UTC/GMT are understood; anything else stays null (raw text is kept).</summary>
    public static DateTimeOffset? ParseViewTradeTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var offset = parts[2].ToUpperInvariant() switch
        {
            "EDT" => TimeSpan.FromHours(-4),
            "EST" => TimeSpan.FromHours(-5),
            "UTC" or "GMT" => TimeSpan.Zero,
            _ => (TimeSpan?)null
        };
        if (offset is null) return null;
        return DateTime.TryParseExact($"{parts[0]} {parts[1]}", "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, offset.Value).ToUniversalTime()
            : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
