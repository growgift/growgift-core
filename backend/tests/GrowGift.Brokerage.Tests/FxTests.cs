using System.Net;
using GrowGift.Brokerage.Infrastructure.Fx;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class FxRateProviderTests
{
    private readonly FakeViewTrade _fx = new();
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    private FxRateProvider Svc(Dictionary<string, decimal>? fixedRates = null) =>
        new(new FakeFactory(_fx), new FxOptions("https://fx.test/", 10, fixedRates), _clock);

    private void Rate(string json, HttpStatusCode code = HttpStatusCode.OK) => _fx.Override = _ => FakeViewTrade.Json(json, code);

    [Fact]
    public async Task Usd_passes_through_without_calling_the_rate_source()
    {
        var r = await Svc().ToUsdAsync(30m, "usd", default);
        Assert.Equal(30m, r.UsdAmount);
        Assert.Equal(1m, r.Rate);
        Assert.Empty(_fx.Calls);
    }

    [Fact]
    public async Task Aud_is_converted_with_the_fetched_rate_and_rounded_down_to_cents()
    {
        Rate("{\"rate\":0.6543}");
        var r = await Svc().ToUsdAsync(50m, "AUD", default);        // 32.715 → 32.71, never 32.72
        Assert.Equal(32.71m, r.UsdAmount);
        Assert.Equal(0.6543m, r.Rate);
        Assert.Equal("v2/rate/aud/usd", _fx.Calls[0].Path);
        Assert.Equal("fx.test", _fx.Calls[0].Host);
    }

    [Fact]
    public async Task Rate_is_cached_for_the_configured_minutes_then_refetched()
    {
        Rate("{\"rate\":0.65}");
        var svc = Svc();
        await svc.ToUsdAsync(10m, "AUD", default);
        await svc.ToUsdAsync(20m, "AUD", default);
        Assert.Equal(1, _fx.Calls.Count);
        _clock.Now = _clock.Now.AddMinutes(11);
        await svc.ToUsdAsync(20m, "AUD", default);
        Assert.Equal(2, _fx.Calls.Count);
    }

    [Fact]
    public async Task A_fixed_rate_overrides_the_network()
    {
        var r = await Svc(new() { ["AUD"] = 0.5m }).ToUsdAsync(40m, "AUD", default);
        Assert.Equal(20m, r.UsdAmount);
        Assert.Empty(_fx.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rate\":\"0.65\"}")]
    [InlineData("<html>")]
    [InlineData("{\"rate\":0}")]
    [InlineData("{\"rate\":6.5}")]       // 10x glitch — outside the AUD sanity band
    [InlineData("{\"rate\":-0.65}")]
    public async Task Unreadable_or_implausible_rates_are_refused(string body)
    {
        Rate(body);
        await Assert.ThrowsAsync<FxUnavailableException>(() => Svc().ToUsdAsync(50m, "AUD", default));
    }

    [Fact]
    public async Task Source_errors_and_timeouts_are_refused_not_guessed()
    {
        Rate("{}", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<FxUnavailableException>(() => Svc().ToUsdAsync(50m, "AUD", default));
        _fx.Override = _ => throw new TaskCanceledException("timeout");
        await Assert.ThrowsAsync<FxUnavailableException>(() => Svc().ToUsdAsync(50m, "AUD", default));
    }

    [Fact]
    public async Task Rejects_garbage_currency_codes()
    {
        await Assert.ThrowsAsync<FxUnavailableException>(() => Svc().ToUsdAsync(50m, "A$D", default));
        await Assert.ThrowsAsync<FxUnavailableException>(() => Svc().ToUsdAsync(50m, "", default));
    }
}
