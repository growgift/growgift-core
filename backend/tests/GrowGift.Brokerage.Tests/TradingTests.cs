using System.Net;
using System.Text.Json;
using GrowGift.Brokerage.Api;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class TradeOrderValidatorTests
{
    [Theory]
    [InlineData("buy", "aapl", 5, true)]
    [InlineData("SELL", " IBM ", 1, true)]
    [InlineData("buy", "BRK.B", 2, true)]
    [InlineData("hold", "AAPL", 1, false)]
    [InlineData(null, "AAPL", 1, false)]
    [InlineData("BUY", "", 1, false)]
    [InlineData("BUY", "AA PL", 1, false)]
    [InlineData("BUY", "AAPL;DROP", 1, false)]
    [InlineData("BUY", "AAPL", 0, false)]
    [InlineData("BUY", "AAPL", -3, false)]
    [InlineData("BUY", "AAPL", 10001, false)]
    public void Validates_side_symbol_quantity(string? side, string symbol, int qty, bool ok) =>
        Assert.Equal(ok, TradeOrderValidator.Validate(side, symbol, qty, 10_000).Ok);

    [Fact]
    public void Rejects_fractional_quantity() =>
        Assert.Equal("trading.fractional_quantity", TradeOrderValidator.Validate("BUY", "AAPL", 1.5m, 10_000).Code);

    [Fact]
    public void Normalises_input()
    {
        var r = TradeOrderValidator.Validate("sell", " ibm ", 3, 10_000).Order!;
        Assert.Equal(OrderSide.Sell, r.Side);
        Assert.Equal("IBM", r.Symbol);
    }

    [Theory]
    [InlineData("abcdefgh", true)]
    [InlineData("short", false)]
    [InlineData("has space in it!", false)]
    [InlineData(null, false)]
    public void Idempotency_key_shape(string? key, bool ok) => Assert.Equal(ok, TradeOrderValidator.IsValidIdempotencyKey(key));
}

public class ViewTradeSessionServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeViewTrade _vt = new();
    private readonly FakeInitialTokens _tokens = new();
    private readonly CapturingLogger<ViewTradeSessionService> _log = new();
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    private readonly CapturingLogger<ViewTradeUserDirectory> _dirLog = new();

    private ViewTradeSessionService Svc(ViewTradeTradingOptions? o = null)
    {
        var factory = new FakeFactory(_vt);
        var dir = new ViewTradeUserDirectory(factory, _tokens, new ViewTradeFirmOptions("1585", "2641"), _dirLog);
        return new(factory, _tokens, dir,
            o ?? new ViewTradeTradingOptions("https://sso.test", "https://trading.test", "1585", "2641"),
            _log, _clock);
    }

    [Fact]
    public async Task Runs_all_five_steps_in_order_with_correct_token_propagation()
    {
        var s = await Svc().GetSessionAsync(User, "a@x.com", default);

        Assert.Equal(new[]
        {
            "api/v2/users/b2c/list", "api/v1/auth/b2c/login/impersonate",
            "api/v1/auth/watchmen/authentication-tokens", "api/auth/v1/sso/platform"
        }, _vt.Calls.Select(c => c.Path));

        // Step 2: GetUser uses the INITIAL token, on the UMA host.
        Assert.Equal("Bearer " + FakeViewTrade.InitialToken, _vt.Calls[0].Auth);
        Assert.Equal("uma.test", _vt.Calls[0].Host);
        Assert.Contains("\"firm_ids\":[1585]", _vt.Calls[0].Body);
        // Step 3: impersonation uses the INITIAL token + the user id from users_list[0].id.
        Assert.Equal("Bearer " + FakeViewTrade.InitialToken, _vt.Calls[1].Auth);
        Assert.Contains("\"user_id\":124246", _vt.Calls[1].Body);
        // Step 4: Watchman uses the IMPERSONATED token + branch id.
        Assert.Equal("Bearer " + FakeViewTrade.ImpersonatedToken, _vt.Calls[2].Auth);
        Assert.Contains("\"branch_id\":2641", _vt.Calls[2].Body);
        // Step 5: SSO is on the SSO host, unauthenticated, and carries the WATCHMAN token.
        Assert.Equal("sso.test", _vt.Calls[3].Host);
        Assert.Null(_vt.Calls[3].Auth);
        Assert.Contains(FakeViewTrade.WatchmanToken, _vt.Calls[3].Body);
        Assert.Contains("\"platform\":\"uma\"", _vt.Calls[3].Body);
        // The result is the SSO session id — not the Watchman token.
        Assert.Equal(FakeViewTrade.SessionId, s.SessionId);
        Assert.NotEqual(FakeViewTrade.WatchmanToken, s.SessionId);
    }

    [Fact]
    public async Task Ignores_substring_matches_for_other_users()
    {
        _vt.UsersJson = """{"users_list":[{"id":999,"email":"zz-a@x.com.au","status":"active","branches":[{"id":2641}]}]}""";
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.UserNotFound, ex.Stage);
        Assert.DoesNotContain(_vt.Calls, c => c.Path.Contains("impersonate")); // never impersonates a stranger
    }

    [Fact]
    public async Task Refuses_to_pick_between_several_matching_users()
    {
        _vt.UsersJson = "{\"users_list\":[{\"id\":1,\"email\":\"a@x.com\",\"status\":\"active\",\"branches\":[{\"id\":2641}]},{\"id\":2,\"email\":\"A@X.com\",\"status\":\"active\",\"branches\":[{\"id\":2641}]}]}";
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.UserAmbiguous, ex.Stage);
        Assert.DoesNotContain(_vt.Calls, c => c.Path.Contains("impersonate"));
    }

    [Fact]
    public async Task Refuses_inactive_users()
    {
        _vt.UsersJson = """{"users_list":[{"id":1,"email":"a@x.com","status":"suspended","branches":[{"id":2641}]}]}""";
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.UserNotFound, ex.Stage);
    }

    [Fact]
    public async Task Refuses_user_outside_configured_branch()
    {
        _vt.UsersJson = """{"users_list":[{"id":1,"email":"a@x.com","status":"active","branches":[{"id":777}]}]}""";
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
    }

    [Fact]
    public async Task Fails_when_sso_stage_is_not_established()
    {
        _vt.SsoStage = "PENDING_MFA";
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.Sso, ex.Stage);
    }

    [Theory]
    [InlineData("api/v1/auth/b2c/login/impersonate", TradingStage.Impersonation)]
    [InlineData("api/v1/auth/watchmen/authentication-tokens", TradingStage.Watchman)]
    [InlineData("api/auth/v1/sso/platform", TradingStage.Sso)]
    public async Task Reports_the_failing_stage(string failingPath, TradingStage stage)
    {
        _vt.Override = p => p == failingPath ? FakeViewTrade.Json("""{"code":42}""", HttpStatusCode.InternalServerError) : null;
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(stage, ex.Stage);
    }

    [Fact]
    public async Task Retries_user_lookup_once_with_a_fresh_initial_token_on_401()
    {
        var first = true;
        _vt.Override = p =>
        {
            if (p != "api/v2/users/b2c/list" || !first) return null;
            first = false;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };
        var s = await Svc().GetSessionAsync(User, "a@x.com", default);
        Assert.Equal(1, _tokens.Invalidations);
        Assert.Equal(FakeViewTrade.SessionId, s.SessionId);
    }

    [Fact]
    public async Task Not_configured_fails_fast_without_calling_viewtrade()
    {
        var ex = await Assert.ThrowsAsync<ViewTradeSessionException>(() =>
            Svc(new ViewTradeTradingOptions(null, "", "1585", "2641")).GetSessionAsync(User, "a@x.com", default));
        Assert.Equal(TradingStage.NotConfigured, ex.Stage);
        Assert.Empty(_vt.Calls);
    }

    [Fact]
    public async Task Reuses_session_per_user_only_while_the_jwt_exp_allows()
    {
        _vt.SessionValue = Jwt.WithExp(_clock.Now.AddHours(1));
        var svc = Svc();
        await svc.GetSessionAsync(User, "a@x.com", default);
        var calls = _vt.Calls.Count;
        await svc.GetSessionAsync(User, "a@x.com", default);
        Assert.Equal(calls, _vt.Calls.Count);                       // cached

        await svc.GetSessionAsync(Guid.NewGuid(), "a@x.com", default);
        Assert.True(_vt.Calls.Count > calls);                        // other user: never shares the session

        calls = _vt.Calls.Count;
        _clock.Now = _clock.Now.AddMinutes(6);                       // past SessionCacheMinutes
        await svc.GetSessionAsync(User, "a@x.com", default);
        Assert.True(_vt.Calls.Count > calls);
    }

    [Fact]
    public async Task Does_not_cache_when_expiry_is_unknown()
    {
        var svc = Svc();                                             // SessionId is not a JWT
        await svc.GetSessionAsync(User, "a@x.com", default);
        var calls = _vt.Calls.Count;
        await svc.GetSessionAsync(User, "a@x.com", default);
        Assert.True(_vt.Calls.Count > calls);
    }

    [Fact]
    public async Task Never_logs_tokens_session_ids_or_response_bodies()
    {
        _vt.SsoStage = "PENDING";
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));
        _vt.Override = p => p.EndsWith("authentication-tokens") ? FakeViewTrade.Json($$"""{"code":7,"echo":"{{FakeViewTrade.ImpersonatedToken}}"}""", HttpStatusCode.BadRequest) : null;
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().GetSessionAsync(User, "a@x.com", default));

        var all = string.Join("\n", _log.Lines.Concat(_dirLog.Lines));
        foreach (var secret in new[] { FakeViewTrade.InitialToken, FakeViewTrade.ImpersonatedToken, FakeViewTrade.WatchmanToken, FakeViewTrade.SessionId })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public void Session_to_string_is_redacted() =>
        Assert.DoesNotContain(FakeViewTrade.SessionId, new TradingSession(FakeViewTrade.SessionId, DateTimeOffset.UtcNow).ToString());
}

public class ViewTradeOrderServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeViewTrade _vt = new();
    private readonly CapturingLogger<ViewTradeSessionService> _slog = new();
    private readonly CapturingLogger<ViewTradeOrderService> _olog = new();
    private readonly MemoryIdempotency _idem = new();

    private ViewTradeOrderService Svc()
    {
        var opts = new ViewTradeTradingOptions("https://sso.test", "https://trading.test", "1585", "2641", SessionCacheMinutes: 0);
        var factory = new FakeFactory(_vt);
        var tokens = new FakeInitialTokens();
        var dir = new ViewTradeUserDirectory(factory, tokens, new ViewTradeFirmOptions("1585", "2641"), new CapturingLogger<ViewTradeUserDirectory>());
        var sessions = new ViewTradeSessionService(factory, tokens, dir, opts, _slog);
        return new ViewTradeOrderService(factory, sessions, _idem, _olog);
    }

    private static PlaceOrderRequest Buy(string sym = "IBM", decimal q = 5) => new(OrderSide.Buy, sym, q);
    private Captured LastOrder => _vt.Calls.Last(c => c.Path == "api/orders/v2/place/equity");

    [Theory]
    [InlineData("""{"OrderRef":"XX1"}""", "XX1")]
    [InlineData("""{"orderId":98765}""", "98765")]
    [InlineData("""{"data":{"orderId":"A-7"}}""", "A-7")]
    [InlineData("""{"status":true,"result":{"OrderRef":"R9"}}""", "R9")]
    public void Order_reference_is_found_in_the_shapes_ViewTrade_might_use(string body, string expected) =>
        Assert.Equal(expected, ViewTradeOrderService.Interpret(HttpStatusCode.OK, body, true).OrderRef);

    [Fact]
    public void A_2xx_that_says_status_false_is_rejected_not_unconfirmed()
    {
        var o = ViewTradeOrderService.Interpret(HttpStatusCode.OK, """{"status":false,"message":"Market is closed"}""", true);
        Assert.Equal(OrderState.Rejected, o.State);
        Assert.Equal("Market is closed", o.Message);
    }

    [Fact]
    public void A_2xx_with_no_reference_and_no_failure_flag_stays_unconfirmed() =>
        Assert.Equal(OrderState.Unconfirmed, ViewTradeOrderService.Interpret(HttpStatusCode.OK, """{"something":"else"}""", true).State);

    [Fact]
    public void Reply_shape_has_names_and_kinds_but_no_values() =>
        Assert.Equal("{status:True, data:{orderId:Number}, message:String}",
            ViewTradeOrderService.DescribeShape("""{"status":true,"data":{"orderId":123456789},"message":"secret-ish"}"""));

    [Fact]
    public async Task Buy_posts_BUY_with_SessionId_header_and_server_side_account()
    {
        var o = await Svc().PlaceAsync(User, "a@x.com", "ORBS-01-004099", Buy(), "key-00000001", default);

        Assert.Equal(OrderState.Accepted, o.State);
        Assert.Equal("XX123456", o.OrderRef);
        Assert.Equal("trading.test", LastOrder.Host);
        Assert.Equal("SessionId " + FakeViewTrade.SessionId, LastOrder.Auth);
        using var doc = JsonDocument.Parse(LastOrder.Body);
        var root = doc.RootElement;
        Assert.Equal("ORBS-01-004099", root.GetProperty("account").GetProperty("accountNumber").GetString());
        Assert.Equal("BUY", root.GetProperty("order").GetProperty("transaction").GetString());
        Assert.Equal("IBM", root.GetProperty("order").GetProperty("quote").GetProperty("symbol").GetString());
        Assert.Equal(5, root.GetProperty("order").GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Sell_posts_SELL()
    {
        await Svc().PlaceAsync(User, "a@x.com", "ORBS-01-004099", new(OrderSide.Sell, "IBM", 2), "key-00000002", default);
        Assert.Contains("\"transaction\":\"SELL\"", LastOrder.Body);
    }

    [Fact]
    public async Task Sell_with_a_dollar_amount_sends_the_Notional_DAY_shape_from_the_saved_Postman_SELL()
    {
        await Svc().PlaceAsync(User, "a@x.com", "ORBS-01-004099", new(OrderSide.Sell, "RBLX", 0.25m, 10m), "key-00000009", default);
        var b = LastOrder.Body;
        Assert.Contains("\"transaction\":\"SELL\"", b);
        Assert.Contains("\"orderType\":\"Notional\"", b);
        Assert.Contains("\"validity\":\"DAY\"", b);
        Assert.Contains("\"expectedValue\":10", b);
        Assert.DoesNotContain("quantity", b);
        Assert.DoesNotContain("null", b);
    }

    [Fact]
    public async Task Same_key_never_reaches_viewtrade_twice()
    {
        var svc = Svc();
        var a = await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000003", default);
        var b = await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000003", default);
        Assert.Equal(1, _vt.OrderCalls);
        Assert.Equal(a.OrderRef, b.OrderRef);
    }

    [Fact]
    public async Task Concurrent_double_submit_sends_one_order()
    {
        var svc = Svc();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000004", default)));
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Same_key_from_a_different_user_is_independent()
    {
        var svc = Svc();
        await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000005", default);
        await svc.PlaceAsync(Guid.NewGuid(), "a@x.com", "ACC2", Buy(), "key-00000005", default);
        Assert.Equal(2, _vt.OrderCalls);
    }

    [Fact]
    public async Task Timeout_is_unconfirmed_and_is_never_resubmitted()
    {
        _vt.Order = _ => throw new TaskCanceledException("timeout");
        var svc = Svc();
        var first = await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000006", default);
        var again = await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000006", default);
        Assert.Equal(OrderState.Unconfirmed, first.State);
        Assert.Equal(OrderState.Unconfirmed, again.State);
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Server_error_is_unconfirmed_not_failed()
    {
        _vt.Order = _ => FakeViewTrade.Json("{}", HttpStatusCode.BadGateway);
        var o = await Svc().PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000007", default);
        Assert.Equal(OrderState.Unconfirmed, o.State);
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Client_error_is_rejected_with_viewtrade_message()
    {
        _vt.Order = _ => FakeViewTrade.Json("""{"message":"Insufficient holdings"}""", HttpStatusCode.UnprocessableEntity);
        var o = await Svc().PlaceAsync(User, "a@x.com", "ACC", new(OrderSide.Sell, "IBM", 99), "key-00000008", default);
        Assert.Equal(OrderState.Rejected, o.State);
        Assert.Equal("Insufficient holdings", o.Message);
    }

    [Fact]
    public async Task Auth_failure_reauthenticates_once_then_sends_once()
    {
        _vt.Order = n => n == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : FakeViewTrade.Json("""{"OrderRef":"XX9"}""");
        var o = await Svc().PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000009", default);
        Assert.Equal(OrderState.Accepted, o.State);
        Assert.Equal(2, _vt.OrderCalls);
        Assert.Equal(2, _vt.Calls.Count(c => c.Path == "api/auth/v1/sso/platform"));
    }

    [Fact]
    public async Task Persistent_auth_failure_is_failed_not_looped()
    {
        _vt.Order = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var o = await Svc().PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000010", default);
        Assert.Equal(OrderState.Failed, o.State);
        Assert.Equal(2, _vt.OrderCalls);
    }

    [Fact]
    public async Task Session_failure_sends_no_order_and_the_same_key_may_try_again_once_it_recovers()
    {
        var broken = true;
        _vt.Override = p => broken && p.Contains("impersonate") ? new HttpResponseMessage(HttpStatusCode.BadRequest) : null;
        var svc = Svc();
        await Assert.ThrowsAsync<ViewTradeSessionException>(() =>
            svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000011", default));
        Assert.Equal(0, _vt.OrderCalls);                       // nothing was sent

        broken = false;                                        // ViewTrade recovers; "Failed" means nothing was placed → safe to retry
        var again = await svc.PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000011", default);
        Assert.Equal(OrderState.Accepted, again.State);
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Notional_buy_sends_dollar_amount_as_a_DAY_Notional_order_with_SessionId()
    {
        var o = await Svc().PlaceNotionalBuyAsync(User, 124246, "ORBS-01-004099", "IBM", 30m, "gg-gift-1", default);
        Assert.Equal(OrderState.Accepted, o.State);
        Assert.Equal("XX123456", o.OrderRef);
        Assert.Equal("SessionId " + FakeViewTrade.SessionId, LastOrder.Auth);
        using var doc = JsonDocument.Parse(LastOrder.Body);
        var order = doc.RootElement.GetProperty("order");
        Assert.Equal("BUY", order.GetProperty("transaction").GetString());
        Assert.Equal("Notional", order.GetProperty("orderType").GetString());
        Assert.Equal("DAY", order.GetProperty("validity").GetString());
        Assert.Equal(30m, order.GetProperty("expectedValue").GetDecimal());
        Assert.Equal("IBM", order.GetProperty("quote").GetProperty("symbol").GetString());
        Assert.Equal("ORBS-01-004099", doc.RootElement.GetProperty("account").GetProperty("accountNumber").GetString());
    }

    [Fact]
    public async Task Notional_buy_uses_the_stored_viewtrade_user_id_and_never_searches_by_email()
    {
        await Svc().PlaceNotionalBuyAsync(User, 555, "ACC", "IBM", 30m, "gg-gift-2", default);
        Assert.DoesNotContain(_vt.Calls, c => c.Path == "api/v2/users/b2c/list");
        var imp = _vt.Calls.Single(c => c.Path.Contains("impersonate"));
        Assert.Contains("\"user_id\":555", imp.Body);
    }

    [Fact]
    public async Task Notional_buy_for_the_same_gift_is_never_placed_twice()
    {
        var svc = Svc();
        await svc.PlaceNotionalBuyAsync(User, 555, "ACC", "IBM", 30m, "gg-gift-3", default);
        await svc.PlaceNotionalBuyAsync(User, 555, "ACC", "IBM", 30m, "gg-gift-3", default);
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Notional_buy_timeout_is_unconfirmed_and_never_resent()
    {
        _vt.Order = _ => throw new TaskCanceledException("timeout");
        var svc = Svc();
        var a = await svc.PlaceNotionalBuyAsync(User, 555, "ACC", "IBM", 30m, "gg-gift-4", default);
        var b = await svc.PlaceNotionalBuyAsync(User, 555, "ACC", "IBM", 30m, "gg-gift-4", default);
        Assert.Equal(OrderState.Unconfirmed, a.State);
        Assert.Equal(OrderState.Unconfirmed, b.State);
        Assert.Equal(1, _vt.OrderCalls);
    }

    [Fact]
    public async Task Logs_never_contain_session_material()
    {
        await Svc().PlaceAsync(User, "a@x.com", "ACC", Buy(), "key-00000012", default);
        var all = string.Join("\n", _olog.Lines.Concat(_slog.Lines));
        foreach (var secret in new[] { FakeViewTrade.InitialToken, FakeViewTrade.ImpersonatedToken, FakeViewTrade.WatchmanToken, FakeViewTrade.SessionId })
            Assert.DoesNotContain(secret, all);
    }

    [Theory]
    [InlineData("filled", OrderState.Filled)]
    [InlineData("PARTIALLY_FILLED", OrderState.PartiallyFilled)]
    [InlineData("rejected", OrderState.Rejected)]
    [InlineData("pending", OrderState.Pending)]
    [InlineData(null, OrderState.Accepted)]
    [InlineData("some-new-word", OrderState.Accepted)]
    public void Maps_status_vocabulary_conservatively(string? vt, OrderState expected) =>
        Assert.Equal(expected, ViewTradeOrderService.MapStatus(vt));

    [Fact]
    public void Success_without_order_ref_is_unconfirmed() =>
        Assert.Equal(OrderState.Unconfirmed, ViewTradeOrderService.Interpret(HttpStatusCode.OK, "{}", true).State);

    [Fact]
    public void Accepted_message_does_not_claim_execution() =>
        Assert.Contains("hasn't necessarily been executed",
            ViewTradeOrderService.Interpret(HttpStatusCode.OK, """{"OrderRef":"A1"}""", true).Message);
}

public class TradingEligibilityTests
{
    private static readonly ViewTradeTradingOptions Configured = new("https://s", "https://t", "1", "2");

    private static System.Security.Claims.ClaimsPrincipal P(string role, string kyc, string? account) =>
        new(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("role", role),
            new System.Security.Claims.Claim("kyc_status", kyc),
        }.Concat(account is null ? [] : [new System.Security.Claims.Claim("viewtrade_account", account)]), "test"));

    [Fact] public void Eligible_user_can_trade() => Assert.True(TradingEndpoints.Eligibility(P("parent", "approved", "ACC"), Configured).Ok);
    [Fact] public void Child_cannot_trade() => Assert.Equal("child_account", TradingEndpoints.Eligibility(P("child", "approved", "ACC"), Configured).Reason);
    [Fact] public void Unapproved_kyc_cannot_trade() => Assert.Equal("kyc_required", TradingEndpoints.Eligibility(P("parent", "pending", "ACC"), Configured).Reason);
    [Fact] public void No_account_cannot_trade() => Assert.Equal("no_account", TradingEndpoints.Eligibility(P("parent", "approved", null), Configured).Reason);
    [Fact] public void Unconfigured_host_is_reported() => Assert.Equal("not_configured",
        TradingEndpoints.Eligibility(P("parent", "approved", "ACC"), new ViewTradeTradingOptions(null, null, null, null)).Reason);
}
