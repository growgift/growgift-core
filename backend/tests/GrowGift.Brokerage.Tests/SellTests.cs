using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class SellValidationTests
{
    [Fact] public void Sell_accepts_a_fractional_quantity()
    {
        var r = TradeOrderValidator.Validate("SELL", "ibm", 0.02029m, 10_000);
        Assert.True(r.Ok); Assert.Equal(0.02029m, r.Order!.Quantity); Assert.Equal(OrderSide.Sell, r.Order.Side);
    }
    [Fact] public void Buy_by_quantity_is_still_whole_shares_only() =>
        Assert.Equal("trading.fractional_quantity", TradeOrderValidator.Validate("BUY", "IBM", 0.5m, 10_000).Code);
    [Fact] public void Sell_precision_is_limited() =>
        Assert.Equal("trading.too_many_decimals", TradeOrderValidator.Validate("SELL", "IBM", 0.1234567m, 10_000).Code);
    [Fact] public void Sell_still_rejects_zero_and_negative()
    {
        Assert.Equal("trading.invalid_quantity", TradeOrderValidator.Validate("SELL", "IBM", 0m, 10_000).Code);
        Assert.Equal("trading.invalid_quantity", TradeOrderValidator.Validate("SELL", "IBM", -1m, 10_000).Code);
    }
    [Fact] public void Sell_all_needs_no_quantity_and_is_sell_only()
    {
        var ok = TradeOrderValidator.Validate("SELL", "IBM", 0m, 10_000, sellAll: true);
        Assert.True(ok.Ok); Assert.Equal(OrderSide.Sell, ok.Order!.Side);
        Assert.Equal("trading.sell_all_only_for_sell", TradeOrderValidator.Validate("BUY", "IBM", 0m, 10_000, sellAll: true).Code);
    }
    [Fact] public void Sell_all_still_validates_the_symbol() =>
        Assert.Equal("trading.invalid_symbol", TradeOrderValidator.Validate("SELL", "IB M", 0m, 10_000, sellAll: true).Code);
}

public class SellPlannerTests
{
    private static PortfolioPosition P(string s, decimal q) => new(s, q, null, null);

    [Fact] public void Sell_all_uses_the_exact_held_quantity()
    {
        var p = SellPlanner.Plan(new[] { P("IBM", 0.02029m), P("AAPL", 3m) }, "ibm", 0, sellAll: true);
        Assert.True(p.Ok); Assert.Equal(0.02029m, p.Quantity); Assert.True(p.IsFullPosition);
    }
    [Fact] public void Partial_sell_keeps_the_requested_quantity()
    {
        var p = SellPlanner.Plan(new[] { P("IBM", 0.02029m) }, "IBM", 0.01m, sellAll: false);
        Assert.True(p.Ok); Assert.Equal(0.01m, p.Quantity); Assert.False(p.IsFullPosition);
    }
    [Fact] public void Selling_more_than_held_is_refused()
    {
        var p = SellPlanner.Plan(new[] { P("IBM", 0.02029m) }, "IBM", 0.03m, sellAll: false);
        Assert.False(p.Ok); Assert.Equal("trading.insufficient_shares", p.Code);
    }
    [Fact] public void Not_holding_the_symbol_is_refused()
    {
        Assert.Equal("trading.not_held", SellPlanner.Plan(new[] { P("AAPL", 1m) }, "IBM", 1m, false).Code);
        Assert.Equal("trading.not_held", SellPlanner.Plan(Array.Empty<PortfolioPosition>(), "IBM", 0, true).Code);
        Assert.Equal("trading.not_held", SellPlanner.Plan(new[] { P("IBM", 0m) }, "IBM", 1m, false).Code);
    }
    [Fact] public void Selling_almost_everything_sells_exactly_everything_so_no_dust_remains()
    {
        var p = SellPlanner.Plan(new[] { P("IBM", 0.0202900001m) }, "IBM", 0.02029m, false);
        Assert.True(p.Ok); Assert.True(p.IsFullPosition); Assert.Equal(0.0202900001m, p.Quantity);
    }
    [Fact] public void A_tiny_rounding_overshoot_is_tolerated_but_a_real_one_is_not()
    {
        Assert.True(SellPlanner.Plan(new[] { P("IBM", 0.02029m) }, "IBM", 0.0202905m, false).Ok);
        Assert.False(SellPlanner.Plan(new[] { P("IBM", 0.02029m) }, "IBM", 0.0203m, false).Ok);
    }
    [Fact] public void Duplicate_lines_for_one_symbol_are_summed() =>
        Assert.Equal(3m, SellPlanner.Plan(new[] { P("IBM", 1m), P("ibm", 2m) }, "IBM", 0, true).Quantity);
    [Fact] public void Request_is_never_larger_than_the_position()
    {
        foreach (var req in new[] { 0.001m, 0.01m, 0.02m, 0.02029m })
        {
            var p = SellPlanner.Plan(new[] { P("IBM", 0.02029m) }, "IBM", req, false);
            Assert.True(p.Ok); Assert.True(p.Quantity <= 0.02029m);
        }
    }

    private static PortfolioPosition PV(string s, decimal q, decimal? value, decimal? last = null) => new(s, q, value, null, LastPrice: last);

    [Fact]
    public void Sell_all_uses_market_value_floored_to_the_cent()
    {
        var plan = SellPlanner.Plan(new[] { PV("RBLX", 0.3m, 12.3456m) }, "RBLX", 0, true);
        Assert.True(plan.Ok);
        Assert.Equal(12.34m, plan.Notional);
        Assert.True(plan.IsFullPosition);
    }

    [Fact]
    public void Partial_sell_prices_quantity_by_value_per_share()
    {
        var plan = SellPlanner.Plan(new[] { PV("RBLX", 2m, 100m) }, "RBLX", 0.5m, false);
        Assert.True(plan.Ok);
        Assert.Equal(25m, plan.Notional);
        Assert.False(plan.IsFullPosition);
    }

    [Fact]
    public void Falls_back_to_last_price_when_no_market_value()
    {
        var plan = SellPlanner.Plan(new[] { PV("RBLX", 2m, null, 40m) }, "RBLX", 1m, false);
        Assert.Equal(40m, plan.Notional);
    }

    [Fact]
    public void No_price_gives_no_notional_so_the_endpoint_sends_nothing()
    {
        var plan = SellPlanner.Plan(new[] { PV("RBLX", 2m, null) }, "RBLX", 1m, false);
        Assert.True(plan.Ok);
        Assert.Null(plan.Notional);
    }

    [Fact]
    public void Under_a_cent_is_refused()
    {
        var plan = SellPlanner.Plan(new[] { PV("RBLX", 1m, 100m) }, "RBLX", 0.00001m, false);
        Assert.False(plan.Ok);
        Assert.Equal("trading.sell_too_small", plan.Code);
    }
}
