using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GrowGift.Infrastructure.Common.Idempotency;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

public interface IViewTradeOrderService
{
    Task<OrderOutcome> PlaceAsync(Guid growGiftUserId, string email, string accountNumber,
        PlaceOrderRequest order, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Market BUY of a dollar amount ("Notional" order: expectedValue = dollars, validity DAY — the
    /// PlaceEquity sample for dollar orders). Used when a redeemed gift is bought into the recipient's
    /// account. Same duplicate/timeout rules as <see cref="PlaceAsync"/>.
    /// </summary>
    Task<OrderOutcome> PlaceNotionalBuyAsync(Guid growGiftUserId, long viewTradeUserId, string accountNumber,
        string symbol, decimal dollarAmount, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// The recorded outcome for this user's Idempotency-Key, if the order was already attempted and is final for
    /// that key (anything but "Failed", which is safe to try again). Lets a caller answer a repeat request from the
    /// record instead of re-checking state that the first attempt itself changed (e.g. holdings after a sell).
    /// </summary>
    Task<OrderOutcome?> GetFinalOutcomeAsync(Guid growGiftUserId, string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Places an equity order through ViewTrade's Trading host:
///   POST api/orders/v2/place/equity   Authorization: SessionId &lt;sessionId&gt;
/// BUY (PlaceEquity) and SELL share one endpoint; order.transaction is "BUY" or "SELL".
/// Confirmed against the saved Postman requests: quantity orders (MARKET, GTC, whole shares) and dollar orders
/// (Notional, DAY, expectedValue). A SELL as a dollar order (RBLX, expectedValue 10) returned an orderRef. Sells
/// are therefore sent as dollar orders; a SELL by share quantity has never been shown to work.
///
/// Duplicate protection: the caller's Idempotency-Key is claimed atomically
/// in Redis BEFORE anything is sent, and the outcome is stored against it.
/// A repeat of the same key — double click, browser retry, or a retry after a
/// timeout — therefore never reaches ViewTrade a second time; it gets the
/// stored outcome (or "still being processed"). Nothing in here retries a
/// request that might have been received: a timeout, a 5xx or an unreadable
/// reply is surfaced as <see cref="OrderState.Unconfirmed"/> for the user to
/// check, never silently resubmitted.
///
/// The ONE automatic retry is after a 401/403 from the Trading host: that
/// means ViewTrade rejected the session before processing the order, so
/// re-establishing the session and sending once more can't double-place.
/// </summary>
public sealed class ViewTradeOrderService(
    IHttpClientFactory httpFactory,
    IViewTradeSessionService sessions,
    IIdempotencyStore idempotency,
    ILogger<ViewTradeOrderService> logger) : IViewTradeOrderService
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(24);

    public Task<OrderOutcome> PlaceAsync(Guid userId, string email, string account,
        PlaceOrderRequest order, string idempotencyKey, CancellationToken ct)
    {
        var side = order.Side == OrderSide.Buy ? "BUY" : "SELL";
        // Only the fields for the chosen order type are sent (no null placeholders).
        var inner = new Dictionary<string, object>
        {
            ["quote"] = new { symbol = order.Symbol },
            ["transaction"] = side,
        };
        if (order.Notional is { } dollars)
        {
            // Dollar order: the shape ViewTrade accepted for SELL in the saved PlaceEquitySell request.
            inner["orderType"] = "Notional";
            inner["validity"] = "DAY";
            inner["expectedValue"] = dollars;
        }
        else
        {
            inner["orderType"] = "MARKET";
            inner["validity"] = "GTC";
            inner["quantity"] = order.Quantity;
        }
        var payload = new { requireNbbo = false, account = new { accountNumber = account }, order = inner };
        var what = order.Notional is { } d ? $"{side} {order.Symbol} notional {d}" : $"{side} {order.Symbol} x{order.Quantity}";
        return PlaceCoreAsync(userId, idempotencyKey, c => sessions.GetSessionAsync(userId, email, c), payload, what, ct);
    }

    public Task<OrderOutcome> PlaceNotionalBuyAsync(Guid userId, long viewTradeUserId, string account,
        string symbol, decimal dollarAmount, string idempotencyKey, CancellationToken ct) =>
        PlaceCoreAsync(userId, idempotencyKey,
            c => sessions.GetSessionForViewTradeUserAsync(userId, viewTradeUserId, c),
            new
            {
                requireNbbo = false,
                account = new { accountNumber = account },
                order = new
                {
                    quote = new { symbol },
                    transaction = "BUY",
                    orderType = "Notional",
                    validity = "DAY",
                    expectedValue = dollarAmount
                }
            },
            $"BUY {symbol} notional {dollarAmount}", ct);

    public async Task<OrderOutcome?> GetFinalOutcomeAsync(Guid userId, string idempotencyKey, CancellationToken ct)
    {
        var prior = await idempotency.GetCachedResponseAsync($"trade:{userId:N}:{idempotencyKey}", ct);
        var outcome = prior is null ? null : TryDeserialize(prior);
        return outcome is { State: not OrderState.Failed } ? outcome : null;
    }

    private async Task<OrderOutcome> PlaceCoreAsync(Guid userId, string idempotencyKey,
        Func<CancellationToken, Task<TradingSession>> getSession, object payload, string what, CancellationToken ct)
    {
        // Scoped by user so one user's key can never collide with (or replay) another's.
        var key = $"trade:{userId:N}:{idempotencyKey}";

        if (!await idempotency.TryClaimAsync(key, KeyTtl, ct))
        {
            var prior = await idempotency.GetCachedResponseAsync(key, ct);
            var replay = prior is null ? null : TryDeserialize(prior);
            // "Failed" is only ever recorded when NOTHING was placed (session couldn't be established, or the
            // Trading host refused the session before processing). Re-running that is safe, so a repeat of the
            // same key is allowed to try again instead of replaying the failure. Every other outcome — including
            // Unconfirmed — is final for the key and is replayed, never re-sent.
            if (replay is not { State: OrderState.Failed })
                return replay ?? new OrderOutcome(OrderState.Unconfirmed, null, null,
                    "This order is already being processed. Check your order history before trying again.");
        }

        OrderOutcome outcome;
        try
        {
            outcome = await SendAsync(getSession, payload, userId, ct);
        }
        catch (ViewTradeSessionException ex)
        {
            // Failed before any order was sent, so nothing was placed. Record that definitively against the key.
            logger.LogWarning("Trading session failed at {Stage} for user {UserId}.", ex.Stage, userId);
            await idempotency.StoreResponseAsync(key, JsonSerializer.Serialize(new OrderOutcome(
                OrderState.Failed, null, null, "We couldn't start a trading session. No order was placed.")),
                KeyTtl, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller went away. We can't know whether the order was sent, so keep the claim and record that.
            outcome = Unconfirmed();
        }

        // Always record the outcome — including Unconfirmed — so a repeat can never resubmit.
        await idempotency.StoreResponseAsync(key, JsonSerializer.Serialize(outcome), KeyTtl, CancellationToken.None);
        logger.LogInformation("Order {What} for user {UserId} → {State}.", what, userId, outcome.State);
        return outcome;
    }

    private async Task<OrderOutcome> SendAsync(Func<CancellationToken, Task<TradingSession>> getSession,
        object payload, Guid userId, CancellationToken ct)
    {
        var session = await getSession(ct);
        var (status, body, sent) = await PostOrderAsync(session, payload, ct);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Rejected at the door → not processed → safe to re-authenticate once and resend.
            sessions.Invalidate(userId);
            session = await getSession(ct);
            (status, body, sent) = await PostOrderAsync(session, payload, ct);
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                sessions.Invalidate(userId);
                return new OrderOutcome(OrderState.Failed, null, null,
                    "Your trading session couldn't be established. Please try again shortly.");
            }
        }

        var outcome = Interpret(status, body, sent);
        if (outcome.State is OrderState.Unconfirmed or OrderState.Rejected)
        {
            // Structure + ViewTrade's own short message only — never tokens (an order reply carries none).
            logger.LogWarning("ViewTrade order reply → {State}: HTTP {Status}; reply shape: {Shape}; reason: {Reason}",
                outcome.State, status is null ? "none (no response)" : ((int)status.Value).ToString(), DescribeShape(body), SafeReason(body));
        }
        return outcome;
    }

    /// <summary>"{status:Boolean, data:{orderId:Number}, message:String}" — names and kinds, no values.</summary>
    internal static string DescribeShape(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty)";
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Describe(doc.RootElement, 0);
        }
        catch (JsonException) { return $"(not JSON, {body.Length} chars)"; }

        static string Describe(JsonElement e, int depth)
        {
            if (e.ValueKind == JsonValueKind.Array) return $"[{e.GetArrayLength()} items]";
            if (e.ValueKind != JsonValueKind.Object) return e.ValueKind.ToString();
            if (depth >= 2) return "{…}";
            return "{" + string.Join(", ", e.EnumerateObject().Take(15).Select(p => $"{p.Name}:{Describe(p.Value, depth + 1)}")) + "}";
        }
    }

    private async Task<(HttpStatusCode? Status, string Body, bool MaybeSent)> PostOrderAsync(
        TradingSession session, object payload, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("ViewTradeTrading");
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/orders/v2/place/equity")
        { Content = JsonContent.Create(payload) };
        // Trading uses the "SessionId" scheme (not Bearer) — see the PlaceEquity Postman request.
        req.Headers.TryAddWithoutValidation("Authorization", $"SessionId {session.SessionId}");

        try
        {
            using var resp = await client.SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Timeout / connection drop: the order MAY have been accepted. Never retry from here.
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning("Order request to ViewTrade did not complete ({Kind}).", ex.GetType().Name);
            return (null, string.Empty, true);
        }
    }

    /// <summary>Maps a ViewTrade reply onto an honest state. Pure → unit-tested.</summary>
    internal static OrderOutcome Interpret(HttpStatusCode? status, string body, bool maybeSent)
    {
        if (status is null) return Unconfirmed();

        var code = (int)status.Value;
        if (code is >= 200 and < 300)
        {
            var (orderRef, vtStatus) = ParseSuccess(body);
            if (orderRef is null)
            {
                // The body itself says "status/success: false" → definitively not placed, and ViewTrade's own message explains why.
                if (BodyReportsFailure(body)) return new OrderOutcome(OrderState.Rejected, null, null, SafeReason(body));
                return Unconfirmed(); // 2xx without a reference and no failure flag: it may well be placed
            }
            var state = MapStatus(vtStatus);
            return new OrderOutcome(state, orderRef, vtStatus, state switch
            {
                OrderState.Filled => "Your order has been filled.",
                OrderState.PartiallyFilled => "Your order has been partly filled.",
                OrderState.Rejected => "ViewTrade rejected this order.",
                OrderState.Pending => "Your order is pending at the exchange.",
                _ => "Your order was accepted by ViewTrade. It hasn't necessarily been executed yet."
            });
        }

        // 4xx (other than auth, handled by the caller): ViewTrade refused it — nothing was placed.
        if (code is >= 400 and < 500)
            return new OrderOutcome(OrderState.Rejected, null, null, SafeReason(body));

        // 5xx or anything else: ambiguous — the order may or may not exist.
        return Unconfirmed();
    }

    private static OrderOutcome Unconfirmed() => new(OrderState.Unconfirmed, null, null,
        "We couldn't confirm whether this order went through. Please check your order history before trying again — do not resubmit yet.");

    private static (string? OrderRef, string? Status) ParseSuccess(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? orderRef = null, status = null;
            // Sample reply is {"OrderRef":"XX123456"}, but the real one may nest it, so also look one level down
            // under conventional wrappers. References may be strings or numbers.
            foreach (var scope in Scopes(doc.RootElement))
                foreach (var p in scope.EnumerateObject())
                {
                    var isRef = p.Name.Equals("OrderRef", StringComparison.OrdinalIgnoreCase) ||
                                p.Name.Equals("orderId", StringComparison.OrdinalIgnoreCase);
                    if (isRef && orderRef is null)
                    {
                        var v = p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.Number => p.Value.GetRawText(),
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(v)) orderRef = v;
                    }
                    else if (p.Value.ValueKind == JsonValueKind.String && status is null &&
                             (p.Name.Equals("orderStatus", StringComparison.OrdinalIgnoreCase) ||
                              (p.Name.Equals("status", StringComparison.OrdinalIgnoreCase))))
                        status = p.Value.GetString();
                }
            return (orderRef, status);
        }
        catch (JsonException) { return (null, null); }

        static IEnumerable<JsonElement> Scopes(JsonElement root)
        {
            yield return root;
            foreach (var name in new[] { "data", "result", "order", "response" })
                if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object)
                    yield return inner;
        }
    }

    /// <summary>A 2xx whose body says outright that it failed ("status": false / "success": false) means no order exists.</summary>
    internal static bool BodyReportsFailure(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "status", "success" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False) return true;
            return false;
        }
        catch (JsonException) { return false; }
    }

    // The status vocabulary isn't in the Postman collection (the sample reply is just {OrderRef}).
    // Only unambiguous words are mapped; anything unrecognised stays "Accepted" rather than being guessed.
    internal static OrderState MapStatus(string? s)
    {
        var v = s?.Trim().Replace("_", "").Replace(" ", "").ToLowerInvariant();
        return v switch
        {
            "filled" or "executed" or "completed" => OrderState.Filled,
            "partiallyfilled" or "partialfill" or "partial" => OrderState.PartiallyFilled,
            "rejected" or "denied" => OrderState.Rejected,
            "pending" or "pendingnew" or "open" or "working" => OrderState.Pending,
            _ => OrderState.Accepted
        };
    }

    private static string SafeReason(string body)
    {
        // Show ViewTrade's own message when it is a short plain string; never echo arbitrary bodies.
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "message", "error", "detail" })
                    if (doc.RootElement.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        var text = m.GetString()!.Trim();
                        if (text.Length is > 0 and <= 200) return text;
                    }
        }
        catch (JsonException) { }
        return "ViewTrade rejected this order. Check the symbol, quantity and your holdings, then try again.";
    }

    private static OrderOutcome? TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<OrderOutcome>(json); } catch (JsonException) { return null; }
    }
}
