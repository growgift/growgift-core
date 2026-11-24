using System.Security.Claims;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Microsoft.AspNetCore.Mvc;

namespace GrowGift.Brokerage.Api;

/// <summary>
/// Buy / sell shares for the signed-in user. The browser sends ONLY
/// {side, symbol, quantity} plus an Idempotency-Key. The ViewTrade user, the
/// trading account number and every ViewTrade token/session are resolved here
/// from the verified JWT and never accepted from — or returned to — the client.
/// </summary>
public static class TradingEndpoints
{
    public sealed record OrderBody(string? Side, string? Symbol, decimal Quantity, bool SellAll = false);

    public static IEndpointRouteBuilder MapTradingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/trading/status", GetStatus)
            .RequireAuthorization("Applicant")
            .WithSummary("Whether the caller can trade, and if not, why (no ViewTrade data involved)");
        app.MapPost("/api/v1/trading/orders", PlaceOrder)
            .RequireAuthorization("Trader")
            .WithSummary("Place a market BUY or SELL through ViewTrade (requires Idempotency-Key)");
        app.MapGet("/api/v1/trading/diagnose", Diagnose)
            .RequireAuthorization("Applicant")
            .WithSummary("Development only: runs the ViewTrade sign-in chain stage by stage and reports where it stops (no tokens)");
        return app;
    }

    /// <summary>
    /// Development-only. Forces a fresh run of: user lookup (initial token + GetUser) → impersonate → Watchman → SSO,
    /// then GET api/user/portfolio with the session. Reports per-stage pass/fail with HTTP status and ViewTrade's
    /// numeric code only — never a token, session id or response body. 404 outside Development.
    /// </summary>
    private static async Task<IResult> Diagnose(
        ClaimsPrincipal user, IHostEnvironment env, ViewTradeTradingOptions options,
        IViewTradeSessionService sessions, IViewTradePortfolioService portfolio, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return Results.NotFound();

        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var userId) || string.IsNullOrWhiteSpace(email))
            return Results.Json(new { ok = false, problem = "No identity on the token." }, statusCode: 401);

        string Host(string u) { try { return new Uri(u).Host; } catch { return "(invalid)"; } }
        var config = new
        {
            ssoConfigured = !string.IsNullOrWhiteSpace(options.SsoBaseUrl),
            tradingConfigured = !string.IsNullOrWhiteSpace(options.TradingBaseUrl),
            ssoHost = string.IsNullOrWhiteSpace(options.SsoBaseUrl) ? null : Host(options.SsoBaseUrl),
            tradingHost = string.IsNullOrWhiteSpace(options.TradingBaseUrl) ? null : Host(options.TradingBaseUrl),
            options.FirmId,
            options.BranchId
        };
        if (!options.IsConfigured)
            return Results.Ok(new { ok = false, failedAt = "NotConfigured", config });

        var order = new[] { TradingStage.UserLookup, TradingStage.Impersonation, TradingStage.Watchman, TradingStage.Sso };
        sessions.Invalidate(userId); // always a fresh run, never a cached session
        try
        {
            await sessions.GetSessionAsync(userId, email, ct);
        }
        catch (ViewTradeSessionException ex)
        {
            var idx = Array.IndexOf(order, ex.Stage);
            return Results.Ok(new
            {
                ok = false,
                passed = idx > 0 ? order.Take(idx).Select(x => x.ToString()).ToArray() : Array.Empty<string>(),
                failedAt = ex.Stage.ToString(),
                httpStatus = (int?)ex.StatusCode,
                viewTradeCode = ex.ViewTradeCode,
                config
            });
        }

        try
        {
            var p = await portfolio.GetAsync(userId, email, ct);
            return Results.Ok(new
            {
                ok = true,
                passed = order.Select(x => x.ToString()).Append("Portfolio").ToArray(),
                holdings = p.Positions.Count,
                recognised = p.Recognised,
                config
            });
        }
        catch (ViewTradePortfolioException ex)
        {
            return Results.Ok(new
            {
                ok = false,
                passed = order.Select(x => x.ToString()).ToArray(),
                failedAt = "Portfolio",
                httpStatus = (int?)ex.StatusCode,
                config
            });
        }
        catch (ViewTradeSessionException ex)
        {
            return Results.Ok(new { ok = false, failedAt = ex.Stage.ToString(), httpStatus = (int?)ex.StatusCode, viewTradeCode = ex.ViewTradeCode, config });
        }
    }

    private static IResult GetStatus(ClaimsPrincipal user, ViewTradeTradingOptions options)
    {
        var (ok, reason) = Eligibility(user, options);
        return Results.Ok(new { canTrade = ok, reason, accountRef = ok ? user.FindFirstValue("viewtrade_account") : null });
    }

    public static (bool Ok, string? Reason) Eligibility(ClaimsPrincipal user, ViewTradeTradingOptions options)
    {
        // ASP.NET's JWT handler may rename "role"/"sub"/"email" to ClaimTypes.*, so accept either spelling.
        if (user.Claims.Any(c => (c.Type == "role" || c.Type == ClaimTypes.Role)
                                 && c.Value.Equals("child", StringComparison.OrdinalIgnoreCase)))
            return (false, "child_account");
        if (!string.Equals(user.FindFirstValue("kyc_status"), "approved", StringComparison.OrdinalIgnoreCase))
            return (false, "kyc_required");
        if (string.IsNullOrWhiteSpace(user.FindFirstValue("viewtrade_account")))
            return (false, "no_account");
        if (!options.IsConfigured)
            return (false, "not_configured");
        return (true, null);
    }

    private static async Task<IResult> PlaceOrder(
        [FromBody] OrderBody body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        ViewTradeTradingOptions options,
        IViewTradeOrderService orders,
        IViewTradePortfolioService portfolio,
        CancellationToken ct)
    {
        if (!TradeOrderValidator.IsValidIdempotencyKey(idempotencyKey))
            return Problem(400, "trading.missing_idempotency_key", "Send an Idempotency-Key header (8–64 letters, digits, - or _).");

        var (ok, reason) = Eligibility(user, options);
        if (!ok)
            return reason == "not_configured"
                ? Problem(503, "trading.unavailable", "Trading is temporarily unavailable.")
                : Problem(403, $"trading.{reason}", "Your account isn't eligible to trade.");

        var v = TradeOrderValidator.Validate(body?.Side, body?.Symbol, body?.Quantity ?? 0, options.MaxOrderQuantity, body?.SellAll ?? false);
        if (!v.Ok) return Problem(400, v.Code!, v.Message!);

        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        var account = user.FindFirstValue("viewtrade_account");
        if (!Guid.TryParse(sub, out var userId) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(account))
            return Problem(403, "trading.no_account", "Your account isn't eligible to trade.");

        var order = v.Order!;
        try
        {
            // A repeat of an Idempotency-Key that already has a final outcome is answered from that record. Re-checking
            // holdings first would wrongly reject (or mis-size) the repeat, because the first attempt changed them.
            var prior = await orders.GetFinalOutcomeAsync(userId, idempotencyKey!, ct);
            if (order.Side == OrderSide.Sell && prior is null)
            {
                // Check against ViewTrade's own holdings BEFORE anything is sent or the idempotency key is spent:
                // never oversell, and resolve "sell all" to the exact current quantity on the server.
                ViewTradePortfolio holdings;
                try { holdings = await portfolio.GetAsync(userId, email, ct); }
                catch (ViewTradePortfolioException)
                {
                    return Problem(503, "trading.holdings_unavailable", "We couldn't check your holdings just now, so no sell order was placed. Please try again shortly.");
                }
                var plan = SellPlanner.Plan(holdings.Positions, order.Symbol, order.Quantity, body?.SellAll ?? false);
                if (!plan.Ok) return Problem(422, plan.Code!, plan.Message!);
                if (plan.Notional is null)
                    return Problem(503, "trading.price_unavailable", "We couldn't work out the current value of that holding, so no sell order was placed. Please try again shortly.");
                // Sells go to ViewTrade as a dollar-amount order (the shape its saved SELL request uses).
                order = order with { Quantity = plan.Quantity, Notional = plan.Notional };
            }

            var outcome = await orders.PlaceAsync(userId, email, account, order, idempotencyKey!, ct);
            var payload = new { state = outcome.State.ToString().ToLowerInvariant(), orderRef = outcome.OrderRef, status = outcome.ViewTradeStatus, message = outcome.Message, quantity = order.Quantity > 0 ? (decimal?)order.Quantity : null, symbol = order.Symbol };
            return outcome.State switch
            {
                OrderState.Rejected => Results.Json(payload, statusCode: 422),
                OrderState.Failed => Results.Json(payload, statusCode: 502),
                OrderState.Unconfirmed => Results.Json(payload, statusCode: 202),
                _ => Results.Ok(payload)
            };
        }
        catch (ViewTradeSessionException ex)
        {
            return ex.Stage switch
            {
                TradingStage.NotConfigured => Problem(503, "trading.unavailable", "Trading is temporarily unavailable."),
                TradingStage.UserAmbiguous => Problem(403, "trading.ambiguous_viewtrade_user", "We couldn't safely match your account to a brokerage profile. Please contact support."),
                TradingStage.UserNotFound => Problem(403, "trading.no_viewtrade_user", "We couldn't find a matching brokerage profile for your account."),
                _ => Problem(502, "trading.session_failed", "We couldn't reach the brokerage. No order was placed — please try again shortly.")
            };
        }
    }

    private static IResult Problem(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);
}
