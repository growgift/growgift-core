using System.Security.Claims;
using GrowGift.Brokerage.Domain;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Microsoft.AspNetCore.Mvc;

namespace GrowGift.Brokerage.Api;

/// <summary>
/// Read-only portfolio view for the profile page. Deliberately thin: no new
/// domain logic, just a pass-through to whatever broker adapter is
/// registered for the caller's account, keyed the same way
/// GiftRedeemedConsumer already resolves one ("viewtrade" — the only
/// provider that exists yet; see IBrokerageProvider's own doc on adding a
/// second one by market/region).
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portfolio", GetPortfolio)
            .RequireAuthorization("Applicant") // any authenticated GrowGift user may ask about their own account
            .WithSummary("The caller's own brokerage holdings, if they have an account yet");
        app.MapGet("/api/v1/trading/open-orders", GetOpenOrders)
            .RequireAuthorization("Applicant")
            .WithSummary("The caller's own open (not yet filled) ViewTrade orders");
        return app;
    }

    private static async Task<IResult> GetOpenOrders(
        ClaimsPrincipal user,
        IViewTradeOpenOrdersService openOrders,
        IExistingAccountStore links,
        ViewTradeTradingOptions options,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var userId))
            return Fail(401, "orders.no_identity", "Please sign in again.");

        // Same linkage rule as the portfolio: Brokerage's own recorded link first, the token claim only as a fallback.
        var local = await links.GetAsync(userId, ct);
        var linked = (local is { IsApproved: true } && !string.IsNullOrWhiteSpace(local.AccountRef))
                     || !string.IsNullOrWhiteSpace(user.FindFirstValue("viewtrade_account"));
        if (!linked) return Results.Ok(new { hasAccount = false, orders = Array.Empty<object>() });
        if (!options.IsConfigured) return Fail(503, "orders.unavailable", "Your open orders aren't available right now.");

        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email)) return Fail(401, "orders.no_identity", "Please sign in again.");

        try
        {
            var o = await openOrders.GetAsync(userId, email, ct);
            return Results.Ok(new
            {
                hasAccount = true,
                recognised = o.Recognised,
                orders = o.Orders.Select(x => new
                {
                    orderRef = x.OrderRef, symbol = x.Symbol, side = x.Side, orderType = x.OrderType, validity = x.Validity,
                    currency = x.Currency, expectedValue = x.ExpectedValue, quantity = x.Quantity,
                    remainingQuantity = x.RemainingQuantity, executedQuantity = x.ExecutedQuantity,
                    expectedPrice = x.ExpectedPrice, createdUtc = x.CreatedUtc, createdRaw = x.CreatedRaw, cancelable = x.Cancelable
                })
            });
        }
        catch (ViewTradeSessionException ex)
        {
            logger.LogWarning("Open orders: trading session failed at {Stage} (HTTP {Status}).", ex.Stage, (int?)ex.StatusCode);
            return ex.Stage switch
            {
                TradingStage.UserNotFound => Fail(404, "orders.no_viewtrade_user", "We couldn't find a ViewTrade profile for your email."),
                TradingStage.UserAmbiguous => Fail(409, "orders.ambiguous_viewtrade_user", "We couldn't safely match your account to a ViewTrade profile."),
                _ => Fail(502, $"orders.session_failed.{ex.Stage.ToString().ToLowerInvariant()}", "We couldn't sign in to ViewTrade to load your open orders.")
            };
        }
        catch (ViewTradePortfolioException ex)
        {
            return Fail(502, $"orders.viewtrade_{(int?)ex.StatusCode ?? 0}", "ViewTrade couldn't return your open orders right now.");
        }
    }

    private static async Task<IResult> GetPortfolio(
        ClaimsPrincipal user,
        IViewTradePortfolioService portfolio,
        IExistingAccountStore links,
        ViewTradeTradingOptions options,
        IHostEnvironment env,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var userId))
            return Fail(401, "portfolio.no_identity", "Please sign in again.");

        // The link recorded by Brokerage itself is authoritative and immediate. The JWT claim lags (Identity learns of a
        // new link asynchronously via an event), so it is only a fallback — a freshly linked existing account must not
        // have to wait for a token refresh to show its portfolio.
        var local = await links.GetAsync(userId, ct);
        var accountRef = local is { IsApproved: true } && !string.IsNullOrWhiteSpace(local.AccountRef)
            ? local.AccountRef
            : user.FindFirstValue("viewtrade_account");
        if (string.IsNullOrWhiteSpace(accountRef))
            return Results.Ok(new { hasAccount = false, holdings = Array.Empty<object>(), cashBalance = 0m });

        // Note: no special case for the Development "simulate verification" VT-DEV-… numbers. The portfolio call
        // takes no account id — it is answered for the ViewTrade user behind the verified email — so it still
        // works as a check of the sign-in chain even when the local account number is a placeholder.

        if (!options.IsConfigured)
            return Fail(503, "portfolio.unavailable", "Your portfolio isn't available right now.");

        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email))
            return Fail(401, "portfolio.no_identity", "Please sign in again.");

        try
        {
            // Same authentication chain as trading: the user, the session and the account all come from the verified token.
            var p = await portfolio.GetAsync(userId, email, ct);
            return Results.Ok(new
            {
                hasAccount = true,
                accountRef,
                cashBalance = p.Cash ?? 0m,
                holdings = p.Positions.Select(h => new
                {
                    symbol = h.Symbol,
                    quantity = h.Quantity,
                    marketValue = h.MarketValue,   // null = ViewTrade hasn't priced it
                    costBasis = h.CostBasis ?? 0m,
                    avgPrice = h.AvgPrice,
                    lastPrice = h.LastPrice,
                    changePercent = h.ChangePercent,
                    unrealisedPnl = h.UnrealisedPnl,
                    unrealisedPnlPercent = h.UnrealisedPnlPercent,
                    currency = h.Currency
                }),
                recognised = p.Recognised,
                // Development only: ViewTrade's own reply, so an unrecognised shape can be fixed from a real sample.
                raw = env.IsDevelopment() && !p.Recognised ? p.RawJson : null
            });
        }
        catch (ViewTradeSessionException ex)
        {
            // Stage names only — they say WHICH step of the sign-in chain failed, without any token.
            logger.LogWarning("Portfolio: trading session failed at {Stage} (HTTP {Status}).", ex.Stage, (int?)ex.StatusCode);
            return ex.Stage switch
            {
                TradingStage.UserNotFound => Fail(404, "portfolio.no_viewtrade_user", "We couldn't find a ViewTrade profile for your email."),
                TradingStage.UserAmbiguous => Fail(409, "portfolio.ambiguous_viewtrade_user", "We couldn't safely match your account to a ViewTrade profile."),
                _ => Fail(502, $"portfolio.session_failed.{ex.Stage.ToString().ToLowerInvariant()}", "We couldn't sign in to ViewTrade to load your portfolio.")
            };
        }
        catch (ViewTradePortfolioException ex)
        {
            return Fail(502, $"portfolio.viewtrade_{(int?)ex.StatusCode ?? 0}", "ViewTrade couldn't return your portfolio right now.");
        }
    }

    private static IResult Fail(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);
}
