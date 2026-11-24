using System.Net;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class OpenOrdersParserTests
{
    // Trimmed from the OpenOrders response saved in Postman.
    private const string Sample = """
    [{"quote":{"symbol":"RBLX"},"orderType":"Notional","validity":"DAY","symbol":"RBLX","ourRef":"CC31974578","status":"B",
      "tradeCurrency":"USD","expectedValue":32.27,"quantity":0.60898,"execQty":0,"remainingQty":0.60898,"expectedPx":52.99,
      "createDate":"10/25/2026 02:53:43 EDT","transaction":"BUY","cancelable":true}]
    """;

    [Fact]
    public void Reads_the_saved_sample()
    {
        var o = OpenOrdersParser.Parse(Sample);
        Assert.True(o.Recognised);
        var r = Assert.Single(o.Orders);
        Assert.Equal("CC31974578", r.OrderRef);
        Assert.Equal("RBLX", r.Symbol);
        Assert.Equal("BUY", r.Side);
        Assert.Equal("Notional", r.OrderType);
        Assert.Equal(32.27m, r.ExpectedValue);
        Assert.Equal(0.60898m, r.Quantity);
        Assert.Equal(0.60898m, r.RemainingQuantity);
        Assert.Equal(52.99m, r.ExpectedPrice);
        Assert.Equal(true, r.Cancelable);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 6, 53, 43, TimeSpan.Zero), r.CreatedUtc);   // 02:53 EDT = 06:53 UTC
    }

    [Fact]
    public void Empty_array_means_no_open_orders()
    {
        var o = OpenOrdersParser.Parse("[]");
        Assert.True(o.Recognised); Assert.Empty(o.Orders);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"x":1}""")]
    [InlineData("""[{"foo":1}]""")]
    public void Unrecognised_shapes_are_flagged(string json) => Assert.False(OpenOrdersParser.Parse(json).Recognised);

    [Theory]
    [InlineData("11/02/2026 09:30:00 EST", 14)]
    [InlineData("10/25/2026 02:53:43 AEDT", -1)]   // unknown zone → null
    public void Time_zone_handling(string s, int expectedUtcHour)
    {
        var t = OpenOrdersParser.ParseViewTradeTime(s);
        if (expectedUtcHour < 0) Assert.Null(t); else Assert.Equal(expectedUtcHour, t!.Value.Hour);
    }
}

public class ViewTradeOpenOrdersServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeViewTrade _vt = new();

    private ViewTradeOpenOrdersService Svc()
    {
        var opts = new ViewTradeTradingOptions("https://sso.test", "https://trading.test", "1585", "2641", SessionCacheMinutes: 0);
        var factory = new FakeFactory(_vt);
        var tokens = new FakeInitialTokens();
        var dir = new ViewTradeUserDirectory(factory, tokens, new ViewTradeFirmOptions("1585", "2641"), new CapturingLogger<ViewTradeUserDirectory>());
        var sessions = new ViewTradeSessionService(factory, tokens, dir, opts, new CapturingLogger<ViewTradeSessionService>());
        return new ViewTradeOpenOrdersService(factory, sessions, new CapturingLogger<ViewTradeOpenOrdersService>());
    }

    [Fact]
    public async Task Calls_open_orders_with_SessionId()
    {
        _vt.Override = p => p == "api/orders/open" ? FakeViewTrade.Json("""[{"symbol":"IBM","ourRef":"A1","transaction":"BUY"}]""") : null;
        var o = await Svc().GetAsync(User, "a@x.com", default);
        Assert.Single(o.Orders);
        var call = _vt.Calls.Last(c => c.Path == "api/orders/open");
        Assert.Equal("trading.test", call.Host);
        Assert.Equal("SessionId " + FakeViewTrade.SessionId, call.Auth);
    }

    [Fact]
    public async Task Server_error_is_a_failure_not_an_empty_list()
    {
        _vt.Override = p => p == "api/orders/open" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
        await Assert.ThrowsAsync<ViewTradePortfolioException>(() => Svc().GetAsync(User, "a@x.com", default));
    }
}
