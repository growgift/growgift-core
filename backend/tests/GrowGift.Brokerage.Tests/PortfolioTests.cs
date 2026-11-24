using System.Net;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class PortfolioParserTests
{
    // Shape taken from the sample saved on Postman's Trading/AccountPortfolio (trimmed to the fields we read).
    private const string Sample = """
    [{"quote":{"symbol":"IBM","lastPrice":0},"currency":"USD","symbol":"IBM","price":227.134744002,"quantity":0.02029,
      "value":0,"costValue":4.61,"preciseQuantity":0.02029}]
    """;

    [Fact]
    public void Reads_the_saved_sample()
    {
        var (rows, _, ok) = PortfolioParser.Parse(Sample);
        Assert.True(ok);
        var r = Assert.Single(rows);
        Assert.Equal("IBM", r.Symbol);
        Assert.Equal(0.02029m, r.Quantity);
        Assert.Equal(4.61m, r.CostBasis);
        Assert.Equal("USD", r.Currency);
        Assert.Null(r.MarketValue);          // value 0 + lastPrice 0 = unpriced, not "worth $0"
    }

    [Fact] // the user's expected row: IBM 0.02029 @ 253.56, +0.575%, value 5.14, cost 4.61, ~11.5% unrealised
    public void Computes_last_price_change_and_unrealised_pnl()
    {
        var (rows, _, _) = PortfolioParser.Parse("""[{"symbol":"IBM","quantity":0.02029,"value":5.14,"costValue":4.61,"quote":{"lastPrice":253.56,"changePercent":0.575145769703696}}]""");
        var r = rows[0];
        Assert.Equal(253.56m, r.LastPrice);
        Assert.Equal(0.575145769703696m, r.ChangePercent);
        Assert.Equal(0.53m, r.UnrealisedPnl);          // no gain fields in this row, so computed from value and cost
        Assert.Equal(11.4967m, r.UnrealisedPnlPercent);
    }

    [Fact] // the priced response saved in Postman (GET api/user/portfolio?loadQuotes=true&loadEarnings=true)
    public void Reads_the_priced_postman_response()
    {
        var json = """
        [{"quote":{"symbol":"IBM","currency":"USD","lastPrice":253.56,"changePercent":0.5751457697036962},
          "currency":"USD","symbol":"IBM","price":227.134744002,"quantity":0.02029,"gain":0.54,"gainPercent":11.6342,
          "preciseGain":0.54,"preciseGainPercent":11.6342,"costValue":4.61,"preciseValue":5.14,"value":5.14}]
        """;
        var r = PortfolioParser.Parse(json).Positions[0];
        Assert.Equal(5.14m, r.MarketValue);
        Assert.Equal(4.61m, r.CostBasis);
        Assert.Equal(253.56m, r.LastPrice);
        Assert.Equal(0.5751457697036962m, r.ChangePercent);
        Assert.Equal(0.54m, r.UnrealisedPnl);
        Assert.Equal(11.6342m, r.UnrealisedPnlPercent);   // ViewTrade's own figure, not recomputed from rounded values
    }

    [Fact]
    public void Pnl_is_unknown_when_unpriced() =>
        Assert.Null(PortfolioParser.Parse(Sample).Positions[0].UnrealisedPnlPercent);

    [Fact]
    public void Falls_back_to_last_price_when_value_is_zero()
    {
        var (rows, _, _) = PortfolioParser.Parse("""[{"symbol":"IBM","quantity":2,"value":0,"quote":{"lastPrice":10.5},"costValue":20}]""");
        Assert.Equal(21m, rows[0].MarketValue);
    }

    [Fact]
    public void Uses_value_when_present() =>
        Assert.Equal(30m, PortfolioParser.Parse("""[{"symbol":"IBM","quantity":2,"value":30}]""").Positions[0].MarketValue);

    [Fact]
    public void Empty_array_is_recognised_as_no_holdings()
    {
        var (rows, _, ok) = PortfolioParser.Parse("[]");
        Assert.True(ok); Assert.Empty(rows);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"hello":"world"}""")]
    [InlineData("""[{"foo":1}]""")]
    public void Unrecognised_shapes_are_flagged_not_guessed(string json) =>
        Assert.False(PortfolioParser.Parse(json).Recognised);

    [Fact]
    public void Skips_zero_quantity_rows() =>
        Assert.Empty(PortfolioParser.Parse("""[{"symbol":"IBM","quantity":0}]""").Positions);
}

public class ViewTradePortfolioServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeViewTrade _vt = new();

    private ViewTradePortfolioService Svc()
    {
        var opts = new ViewTradeTradingOptions("https://sso.test", "https://trading.test", "1585", "2641", SessionCacheMinutes: 0);
        var factory = new FakeFactory(_vt);
        var tokens = new FakeInitialTokens();
        var dir = new ViewTradeUserDirectory(factory, tokens, new ViewTradeFirmOptions("1585", "2641"), new CapturingLogger<ViewTradeUserDirectory>());
        var sessions = new ViewTradeSessionService(factory, tokens, dir, opts, new CapturingLogger<ViewTradeSessionService>());
        return new ViewTradePortfolioService(factory, sessions, new CapturingLogger<ViewTradePortfolioService>());
    }

    private void Reply(string json) => _vt.Override = p =>
        p == "api/user/portfolio" ? FakeViewTrade.Json(json) : null;

    [Fact]
    public async Task Gets_portfolio_with_SessionId_after_the_full_chain()
    {
        Reply("""[{"symbol":"IBM","quantity":1,"costValue":9}]""");
        var p = await Svc().GetAsync(User, "a@x.com", default);

        Assert.Single(p.Positions);
        var call = _vt.Calls.Last(c => c.Path == "api/user/portfolio");
        Assert.Equal("trading.test", call.Host);
        Assert.Equal("SessionId " + FakeViewTrade.SessionId, call.Auth);
        Assert.Contains(_vt.Calls, c => c.Path == "api/auth/v1/sso/platform");
    }

    [Fact]
    public async Task Reauthenticates_once_after_401()
    {
        var n = 0;
        _vt.Override = p => p == "api/user/portfolio"
            ? (++n == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : FakeViewTrade.Json("[]"))
            : null;
        var r = await Svc().GetAsync(User, "a@x.com", default);
        Assert.True(r.Recognised);
        Assert.Equal(2, _vt.Calls.Count(c => c.Path == "api/user/portfolio"));
    }

    [Fact]
    public async Task Server_error_surfaces_as_portfolio_exception()
    {
        _vt.Override = p => p == "api/user/portfolio" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        var ex = await Assert.ThrowsAsync<ViewTradePortfolioException>(() => Svc().GetAsync(User, "a@x.com", default));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Session_failure_keeps_its_stage()
    {
        _vt.UsersJson = """{"users_list":[]}""";
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.UserNotFound, ex.Stage);
    }
}

public class RedemptionAccountPickerTests
{
    [Theory]
    [InlineData("ORBS-01-004099", "VT-DEV-1", "ORBS-01-004099")]   // stored real account wins
    [InlineData("VT-DEV-1", "ORBS-01-004099", "ORBS-01-004099")]   // placeholder stored, real on the event
    [InlineData(null, "ORBS-01-004099", "ORBS-01-004099")]
    [InlineData("", " ORBS-01-004099 ", "ORBS-01-004099")]
    public void Picks_the_real_account(string? stored, string? ev, string expected) =>
        Assert.Equal(expected, GrowGift.Brokerage.Infrastructure.Redemption.RedemptionAccountPicker.Pick(stored, ev));

    [Theory]
    [InlineData("VT-DEV-417EA9D0", "VT-DEV-417EA9D0")]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void Never_picks_a_placeholder_or_blank(string? stored, string? ev) =>
        Assert.Null(GrowGift.Brokerage.Infrastructure.Redemption.RedemptionAccountPicker.Pick(stored, ev));

    [Fact]
    public void Masks_account_numbers() =>
        Assert.Equal("ORBS••••99", GrowGift.Brokerage.Infrastructure.Redemption.RedemptionAccountPicker.Mask("ORBS-01-004099"));
}
