using System.Security.Claims;
using GrowGift.Gifting.Application.Commands;
using GrowGift.Gifting.Application.Queries;
using GrowGift.Gifting.Domain;
using GrowGift.Infrastructure.Common.Idempotency;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace GrowGift.Gifting.Api;

public static class GiftingEndpoints
{
    public static IEndpointRouteBuilder MapGiftingEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // ---- Buyer ----
        v1.MapPost("/orders", CreateOrder)
            .RequireAuthorization("Buyer")
            .WithSummary("Create a draft gift order");

        v1.MapGet("/orders", ListOrders)
            .RequireAuthorization("Buyer")
            .WithSummary("List the caller's gift orders");

        v1.MapGet("/orders/{orderId:guid}/code", GetRevealedCode)
            .RequireAuthorization("Buyer")
            .WithSummary("Get the redemption code for a paid order, once issued");

        v1.MapGet("/gifts/received", ListReceivedGifts)
            .RequireAuthorization("Buyer") // "Buyer" = any authenticated user; this is about the caller's own redemptions
            .WithSummary("List gifts the caller has redeemed");

        // Internal (service-to-service, no auth — same trust model as the
        // Identity internal endpoints Family already calls). Family verifies
        // the caller actually owns/is this child before making this call —
        // this endpoint has no independent way to check that itself, since
        // Gifting has no concept of families/parents/partners at all.
        v1.MapGet("/internal/v1/gifts/for-child/{childId:guid}", async (Guid childId, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetGiftsForChildQuery(childId), ct)));

        // ---- Recipient ----
        v1.MapPost("/gifts/preview", PreviewGift)
            .WithSummary("Look up what a gift code contains, without redeeming it — no auth required");

        v1.MapPost("/gifts/redeem", Redeem)
            .RequireAuthorization("KycApproved")
            .WithSummary("Redeem a gift code (requires completed KYC + brokerage account)");

        v1.MapPost("/orders/{orderId:guid}/checkout-session", CreateCheckoutSession)
            .RequireAuthorization("Buyer")
            .WithSummary("Create a Stripe Checkout Session for an order");

        v1.MapPost("/orders/checkout-session", CreateMultiOrderCheckoutSession)
            .RequireAuthorization("Buyer")
            .WithSummary("Create a single combined Stripe Checkout Session for several selected cart items");

        // Fallback to the webhook: the confirmation page calls this with the
        // Stripe session id it was redirected back with. We ask Stripe
        // directly whether that session was actually paid, and confirm the
        // caller's orders if so. This is what keeps checkout working when
        // the webhook can't reach us (e.g. `stripe listen` isn't running
        // locally) — and it's safe because the answer comes from Stripe's
        // API, never from the client.
        v1.MapPost("/orders/sync-payment", SyncPayment)
            .RequireAuthorization("Buyer")
            .WithSummary("Confirm payment by asking Stripe about a Checkout Session (webhook fallback)");

        // ---- Stripe webhook (no auth; signature-verified) ----
        v1.MapPost("/webhooks/stripe", StripeWebhook)
            .AllowAnonymous()
            .WithSummary("Stripe payment webhook");

        return app;
    }

    private static async Task<IResult> CreateOrder(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateOrderRequest body,
        ClaimsPrincipal user,
        ISender sender,
        IIdempotencyStore idempotency,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Results.BadRequest(new { error = new { code = "idempotency_key.missing", message = "Idempotency-Key header is required for financial operations." } });

        // Lapsed subscription: no NEW gift purchases. Redeeming, viewing and selling are never gated.
        if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
            return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);

        // The gift box has to go somewhere — checked here via a JWT claim
        // (set at login from the buyer's Identity profile) rather than a
        // synchronous call back to Identity for every order. Only applies
        // to physical delivery — an email-delivery order never ships
        // anything, so this was wrongly blocking every such order from a
        // buyer who'd never saved a shipping address (which is the normal
        // case for a brand-new signup going straight for the email option).
        if (body.DeliveryMethod == DeliveryMethod.Physical && user.FindFirstValue("has_shipping_address") != "true")
            return Results.BadRequest(new
            {
                error = "shipping_address_missing",
                message = "Add a shipping address to your account before buying a gift — we need somewhere to send the box."
            });

        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var buyerEmail = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email")
            ?? throw new InvalidOperationException("Caller's token has no email claim.");
        var key = $"orders:create:{buyerId}:{idempotencyKey}";

        var cached = await idempotency.GetCachedResponseAsync(key, ct);
        if (cached is not null)
            return Results.Content(cached, "application/json", statusCode: StatusCodes.Status200OK);

        if (!await idempotency.TryClaimAsync(key, TimeSpan.FromHours(24), ct))
            return Results.Conflict(new { error = new { code = "idempotency_key.in_flight", message = "A request with this idempotency key is in flight." } });

        var result = await sender.Send(new CreateGiftOrderCommand(
            buyerId, buyerEmail, body.Symbol, body.Amount, body.Currency ?? "USD", body.RecipientName,
            body.GreetingMessage, body.BoxSku, body.DeliveryMethod, body.RecipientEmail), ct);

        if (result.IsFailure) return result.Error.ToProblem();

        // Echoes back what the buyer will actually be charged (see
        // CreateCheckoutSession, which charges from these same
        // server-computed values, never a client-supplied total) so the
        // review/confirmation UI can display an authoritative total rather
        // than only its own client-side arithmetic.
        var created = result.Value;
        var payload = new
        {
            id = created.Id,
            status = "Draft",
            symbol = body.Symbol.ToUpperInvariant(),
            amount = new { amount = created.Amount, currency = created.Currency },
            recipientName = body.RecipientName,
            deliveryMethod = body.DeliveryMethod.ToString(),
            boxSku = body.BoxSku,
            boxPrice = created.BoxPrice,
            postalFee = created.PostalFee,
            serviceFee = created.ServiceFee,
            totalAmount = created.TotalAmount
        };
        var response = System.Text.Json.JsonSerializer.Serialize(payload);
        await idempotency.StoreResponseAsync(key, response, TimeSpan.FromHours(24), ct);
        return Results.Created($"/api/v1/orders/{created.Id}", payload);
    }

    private static async Task<IResult> CreateCheckoutSession(
        Guid orderId, ClaimsPrincipal user, ISender sender, CancellationToken ct)
    {
        if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
            return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);

        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await sender.Send(new CreateCheckoutSessionCommand(orderId, buyerId), ct);
        return result.IsFailure ? result.Error.ToProblem() : Results.Ok(new { checkoutUrl = result.Value });
    }

    private static async Task<IResult> CreateMultiOrderCheckoutSession(
        [FromBody] CheckoutSelectionRequest body, ClaimsPrincipal user, ISender sender, CancellationToken ct)
    {
        if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
            return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);

        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await sender.Send(new CreateMultiOrderCheckoutSessionCommand(body.OrderIds, buyerId), ct);
        return result.IsFailure ? result.Error.ToProblem() : Results.Ok(new { checkoutUrl = result.Value });
    }

    private static async Task<IResult> SyncPayment(
        [FromBody] SyncPaymentRequest body, ClaimsPrincipal user, ISender sender,
        Infrastructure.Persistence.GiftingDbContext db, ILogger<Program> logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SessionId) || !body.SessionId.StartsWith("cs_"))
            return Results.BadRequest(new { error = new { code = "payment.bad_session", message = "Missing or invalid Stripe session id." } });

        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        Stripe.Checkout.Session session;
        try
        {
            session = await new Stripe.Checkout.SessionService().GetAsync(body.SessionId, cancellationToken: ct);
        }
        catch (Stripe.StripeException e)
        {
            logger.LogWarning(e, "Could not retrieve Stripe session {SessionId}", body.SessionId);
            return Results.Json(new { error = new { code = "payment.stripe_unreachable", message = "We couldn't reach our payment provider to confirm this payment. Please try again shortly." } }, statusCode: 502);
        }

        if (session.PaymentStatus != "paid")
            return Results.Ok(new { paid = false, paymentStatus = session.PaymentStatus, sessionStatus = session.Status });

        session.Metadata.TryGetValue("orderIds", out var raw);
        var sessionOrderIds = (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var toConfirm = body.OrderIds.Where(sessionOrderIds.Contains).Distinct().ToList();

        var owned = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.GiftOrders.Where(o => toConfirm.Contains(o.Id) && o.BuyerId == buyerId).Select(o => o.Id), ct);

        foreach (var orderId in owned)
        {
            var result = await sender.Send(new ConfirmPaymentCommand(orderId, session.PaymentIntentId ?? session.Id), ct);
            if (result.IsFailure)
                logger.LogWarning("sync-payment: confirm failed for order {OrderId}: {Code}", orderId, result.Error.Code);
            else
                logger.LogInformation("sync-payment: order {OrderId} confirmed paid via Stripe session {SessionId}", orderId, session.Id);
        }

        return Results.Ok(new { paid = true, confirmed = owned });
    }

    private static async Task<IResult> ListOrders(ClaimsPrincipal user, ISender sender,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var orders = await sender.Send(new GetBuyerOrdersQuery(buyerId,
            page < 1 ? 1 : page, pageSize < 1 ? 20 : pageSize), ct);
        return Results.Ok(orders);
    }

    private static async Task<IResult> GetRevealedCode(
        Guid orderId, ClaimsPrincipal user, ISender sender, CancellationToken ct)
    {
        var buyerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await sender.Send(new GetRevealedCodeQuery(orderId, buyerId), ct);
        return result.IsFailure ? result.Error.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> ListReceivedGifts(ClaimsPrincipal user, ISender sender,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var gifts = await sender.Send(new GetReceivedGiftsQuery(userId,
            page < 1 ? 1 : page, pageSize < 1 ? 20 : pageSize), ct);
        return Results.Ok(gifts);
    }

    private static async Task<IResult> PreviewGift([FromBody] PreviewGiftRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new PreviewGiftQuery(body.Code), ct);
        return result.IsFailure ? result.Error.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> Redeem(
        [FromBody] RedeemRequest body, ClaimsPrincipal user, HttpContext http,
        ISender sender, CancellationToken ct)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var accountRef = user.FindFirstValue("viewtrade_account")
            ?? throw new InvalidOperationException("KycApproved policy guarantees this claim.");

        var result = await sender.Send(new RedeemGiftCommand(
            body.Code, userId, body.BeneficiaryChildId, accountRef,
            http.Connection.RemoteIpAddress?.ToString()), ct);

        return result.IsFailure ? result.Error.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> StripeWebhook(HttpRequest request,
        Infrastructure.Payments.IStripeWebhookVerifier verifier, ISender sender,
        ILogger<Program> logger, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        var signature = request.Headers["Stripe-Signature"].ToString();

        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = verifier.VerifyAndParse(payload, signature);
        }
        catch (Stripe.StripeException e)
        {
            logger.LogWarning(e, "Stripe webhook signature verification failed.");
            return Results.BadRequest();
        }

        // Temporary diagnostic: a 200 from this endpoint doesn't distinguish
        // "order marked Paid" from "event type/object didn't match, fell
        // straight through" from "ConfirmPaymentCommand returned NotFound"
        // — all three currently produce the exact same 200 with zero log
        // output, which is why an order can silently stay AwaitingPayment
        // despite Stripe (and stripe listen) reporting a clean 200.
        logger.LogInformation("Stripe webhook received: type={Type} objectMatchesPaymentIntent={Matches}",
            stripeEvent.Type, stripeEvent.Data.Object is Stripe.PaymentIntent);

        if (stripeEvent.Type == "payment_intent.succeeded" &&
            stripeEvent.Data.Object is Stripe.PaymentIntent intent)
        {
            // "orderIds" is a comma-joined list even for a single-order
            // checkout (see CreateCheckoutSessionCommand) — one parsing
            // path shared with a combined cart checkout
            // (CreateMultiOrderCheckoutSessionCommand), rather than two
            // different metadata shapes depending on which command created
            // the session.
            if (!intent.Metadata.TryGetValue("orderIds", out var orderIdsRaw))
            {
                logger.LogWarning("PaymentIntent {PaymentIntentId} succeeded but has no orderIds in metadata — cannot confirm any order.", intent.Id);
                return Results.Ok(); // not retriable; retrying won't add a missing metadata field
            }

            var orderIds = orderIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToList();

            if (orderIds.Count == 0)
            {
                logger.LogWarning("PaymentIntent {PaymentIntentId} succeeded but orderIds metadata had no parseable GUIDs — cannot confirm any order.", intent.Id);
                return Results.Ok();
            }

            // Every order from the same combined payment is confirmed
            // independently — one failing (e.g. an unexpected status on
            // just that row) doesn't stop the others from being marked
            // paid; each failure is logged on its own.
            var anyRetriableFailure = false;
            foreach (var orderId in orderIds)
            {
                logger.LogInformation("Confirming payment for order {OrderId} (PaymentIntent {PaymentIntentId})", orderId, intent.Id);
                var result = await sender.Send(new ConfirmPaymentCommand(orderId, intent.Id), ct);
                if (result.IsFailure)
                {
                    logger.LogWarning("ConfirmPaymentCommand failed for order {OrderId}: {Code} — {Message}",
                        orderId, result.Error.Code, result.Error.Message);
                    if (result.Error.Type != ErrorType.NotFound)
                        anyRetriableFailure = true;
                }
                else
                {
                    logger.LogInformation("Order {OrderId} confirmed paid (PaymentIntent {PaymentIntentId})", orderId, intent.Id);
                }
            }
            if (anyRetriableFailure)
                return Results.Problem(statusCode: 500); // Stripe will retry the whole event
        }

        return Results.Ok();
    }

    public sealed record CreateOrderRequest(
        string Symbol, decimal Amount, string? Currency, string RecipientName,
        string? GreetingMessage, string? BoxSku, DeliveryMethod DeliveryMethod = DeliveryMethod.Physical,
        string? RecipientEmail = null);

    public sealed record SyncPaymentRequest(string SessionId, List<Guid> OrderIds);

    public sealed record CheckoutSelectionRequest(List<Guid> OrderIds);

    public sealed record PreviewGiftRequest(string Code);

    public sealed record RedeemRequest(string Code, Guid? BeneficiaryChildId);

    private static IResult ToProblem(this Error error) => error.Type switch
    {
        ErrorType.Validation => Results.BadRequest(new { error = new { code = error.Code, message = error.Message } }),
        ErrorType.NotFound => Results.NotFound(new { error = new { code = error.Code, message = error.Message } }),
        ErrorType.Conflict => Results.Conflict(new { error = new { code = error.Code, message = error.Message } }),
        ErrorType.Unauthorized => Results.Unauthorized(),
        ErrorType.Forbidden => Results.Forbid(),
        _ => Results.Json(new { error = new { code = error.Code, message = error.Message } }, statusCode: 500)
    };
}
