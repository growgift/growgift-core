using System.Text.RegularExpressions;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

/// <summary>
/// Configuration for the trading path. The UMA host (steps 1–4 of the auth
/// chain) is configured elsewhere (ViewTrade:UmaBaseUrl); the SSO and
/// Trading hosts are separate ViewTrade environments (ViewTrade-Staging-SSO
/// and ViewTrade-Staging-Trading in Postman) and are deliberately REQUIRED
/// config with no built-in default — see <see cref="IsConfigured"/>.
/// </summary>
public sealed record ViewTradeTradingOptions(
    string? SsoBaseUrl,
    string? TradingBaseUrl,
    string? FirmId,
    string? BranchId,
    int SessionCacheMinutes = 5,
    int MaxOrderQuantity = 10_000)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SsoBaseUrl) && !string.IsNullOrWhiteSpace(TradingBaseUrl);
}

/// <summary>
/// An established ViewTrade trading session (the SSO "sessionId"). This is a
/// credential: it must never reach the browser, a URL, or a log line, so the
/// default ToString is replaced to make an accidental log call harmless.
/// </summary>
public sealed record TradingSession(string SessionId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "TradingSession(<redacted>)";
}

public enum TradingStage { NotConfigured, UserLookup, UserNotFound, UserAmbiguous, Impersonation, Watchman, Sso }

public sealed class ViewTradeSessionException(
    TradingStage stage, string message, System.Net.HttpStatusCode? statusCode = null, Exception? inner = null, string? viewTradeCode = null)
    : Exception(message, inner)
{
    public TradingStage Stage { get; } = stage;
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
    /// <summary>ViewTrade's own numeric error code, if the reply carried one (safe to show; never the body).</summary>
    public string? ViewTradeCode { get; } = viewTradeCode;
}

public enum OrderSide { Buy, Sell }

/// <param name="Notional">When set, the order is sent as a dollar-amount ("Notional", DAY) order for this many dollars
/// instead of a share quantity. ViewTrade's saved SELL (RBLX, Notional, DAY, expectedValue 10) was accepted; a SELL by
/// share quantity has never been shown to work, and a sell of a fractional holding has to be a dollar order.</param>
public sealed record PlaceOrderRequest(OrderSide Side, string Symbol, decimal Quantity, decimal? Notional = null);

/// <summary>
/// What we can honestly say about an order. "Accepted" means ViewTrade took
/// the order and returned a reference — NOT that it executed. Unconfirmed
/// means the order MAY have been placed (timeout / 5xx / unreadable reply)
/// and must never be blindly resubmitted.
/// </summary>
public enum OrderState { Accepted, Pending, PartiallyFilled, Filled, Rejected, Failed, Unconfirmed }

public sealed record OrderOutcome(
    OrderState State, string? OrderRef, string? ViewTradeStatus, string Message)
{
    /// <summary>True when we know for certain what happened (so the result can be cached/replayed).</summary>
    public bool Definitive => State != OrderState.Unconfirmed;
}

/// <summary>Server-side validation of a client-supplied order. Pure, so it is directly unit-tested.</summary>
public static partial class TradeOrderValidator
{
    public sealed record Result(bool Ok, string? Code, string? Message, PlaceOrderRequest? Order);

    [GeneratedRegex(@"^[A-Z][A-Z0-9.\-]{0,9}$")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{8,64}$")]
    private static partial Regex KeyRegex();

    public const int MaxSellDecimals = 6;

    public static bool IsValidIdempotencyKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    public static Result Validate(string? side, string? symbol, decimal quantity, int maxQuantity, bool sellAll = false)
    {
        OrderSide parsedSide;
        switch (side?.Trim().ToUpperInvariant())
        {
            case "BUY": parsedSide = OrderSide.Buy; break;
            case "SELL": parsedSide = OrderSide.Sell; break;
            default: return Fail("trading.invalid_side", "Choose buy or sell.");
        }

        var sym = symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(sym) || !SymbolRegex().IsMatch(sym))
            return Fail("trading.invalid_symbol", "Enter a valid ticker symbol, e.g. AAPL.");

        // "Sell everything": the exact quantity is resolved on the server from ViewTrade's own holdings, so the
        // client never needs (or gets to choose) a number. Only valid for a sell.
        if (sellAll)
        {
            if (parsedSide != OrderSide.Sell) return Fail("trading.sell_all_only_for_sell", "\"Sell all\" only applies to a sell order.");
            return new Result(true, null, null, new PlaceOrderRequest(OrderSide.Sell, sym, 0));
        }

        if (quantity <= 0)
            return Fail("trading.invalid_quantity", "Quantity must be greater than zero.");
        if (parsedSide == OrderSide.Buy)
        {
            // Buys by quantity stay whole-share (the Postman PlaceEquity contract only shows whole shares; gift
            // purchases use the separate dollar-amount path).
            if (quantity != decimal.Truncate(quantity))
                return Fail("trading.fractional_quantity", "Quantity must be a whole number of shares.");
        }
        else if (decimal.Round(quantity, MaxSellDecimals) != quantity)
        {
            // Gifted holdings are fractional (e.g. 0.02029), so a sell may be too — but only to a sane precision.
            return Fail("trading.too_many_decimals", $"Use at most {MaxSellDecimals} decimal places.");
        }
        if (quantity > maxQuantity)
            return Fail("trading.quantity_too_large", $"Quantity can't exceed {maxQuantity:N0} shares per order.");

        return new Result(true, null, null, new PlaceOrderRequest(parsedSide, sym, quantity));

        static Result Fail(string code, string message) => new(false, code, message, null);
    }
}


/// <summary>
/// Decides the exact quantity of a SELL from what ViewTrade says the user holds. Pure and unit-tested.
/// Never sells more than is held, and turns "almost everything" into exactly everything so no dust is left behind.
/// </summary>
public static class SellPlanner
{
    public const decimal Epsilon = 0.000001m;

    /// <param name="Notional">Dollar value to sell (floored to whole cents), from ViewTrade's own market value for the
/// holding. Null when no price could be established; the caller must then not place the order.</param>
    public sealed record SellPlan(bool Ok, string? Code, string? Message, decimal Quantity, bool IsFullPosition, decimal? Notional = null);

    /// <summary>The smallest dollar amount we will send; anything below a cent can't be expressed as an order.</summary>
    public const decimal MinNotional = 0.01m;

    private static decimal? PricePerShare(IEnumerable<PortfolioPosition> held)
    {
        var qty = held.Sum(p => p.Quantity);
        var value = held.Where(p => p.MarketValue is not null).Sum(p => p.MarketValue!.Value);
        if (qty > 0 && held.All(p => p.MarketValue is not null) && value > 0) return value / qty;
        var last = held.Select(p => p.LastPrice).FirstOrDefault(x => x is > 0);
        return last;
    }

    private static decimal? NotionalFor(IReadOnlyList<PortfolioPosition> held, decimal quantity, bool full)
    {
        // Selling everything: use ViewTrade's own market value for the position (no price * quantity drift).
        if (full && held.All(p => p.MarketValue is not null) && held.Sum(p => p.MarketValue!.Value) > 0)
            return decimal.Floor(held.Sum(p => p.MarketValue!.Value) * 100m) / 100m;
        var price = PricePerShare(held);
        return price is null ? null : decimal.Floor(quantity * price.Value * 100m) / 100m;
    }

    public static SellPlan Plan(IEnumerable<PortfolioPosition> positions, string symbol, decimal requested, bool sellAll)
    {
        var rows = positions.Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToList();
        var held = rows.Sum(p => p.Quantity);
        if (held <= 0)
            return new(false, "trading.not_held", $"You don't hold any {symbol.ToUpperInvariant()} to sell.", 0, false);
        if (sellAll)
            return Priced(rows, held, true);
        if (requested <= 0)
            return new(false, "trading.invalid_quantity", "Quantity must be greater than zero.", 0, false);
        if (requested > held + Epsilon)
            return new(false, "trading.insufficient_shares", $"You only hold {held} share{(held == 1 ? "" : "s")} of {symbol.ToUpperInvariant()}.", 0, false);
        return requested >= held - Epsilon
            ? Priced(rows, held, true)
            : Priced(rows, requested, false);
    }

    private static SellPlan Priced(List<PortfolioPosition> rows, decimal quantity, bool full)
    {
        var notional = NotionalFor(rows, quantity, full);
        if (notional is { } n && n < MinNotional)
            return new(false, "trading.sell_too_small", "That's less than one cent, which is the smallest amount that can be sold.", 0, false);
        return new(true, null, null, quantity, full, notional);
    }
}
