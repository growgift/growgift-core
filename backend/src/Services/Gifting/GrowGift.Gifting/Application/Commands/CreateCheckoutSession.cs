using GrowGift.Gifting.Domain;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;

namespace GrowGift.Gifting.Application.Commands;

/// <summary>
/// Creates a Stripe-hosted Checkout Session for an order and returns its URL.
/// Deliberately does not set PaymentMethodTypes: leaving it unset lets Stripe
/// Checkout automatically offer every payment method enabled on the account
/// (card, Apple Pay, Google Pay, Link, etc.) based on the buyer's browser and
/// device — no separate wallet integration or domain verification required,
/// since the hosted page is served from Stripe's own domain.
/// </summary>
public sealed record CreateCheckoutSessionCommand(Guid OrderId, Guid BuyerId) : IRequest<Result<string>>;

public sealed class CreateCheckoutSessionHandler(GiftingDbContext db, IConfiguration config)
    : IRequestHandler<CreateCheckoutSessionCommand, Result<string>>
{
    public async Task<Result<string>> Handle(CreateCheckoutSessionCommand request, CancellationToken ct)
    {
        var order = await db.GiftOrders.FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null)
            return Result.Failure<string>(Error.NotFound("gift_order.not_found", "No such order."));
        if (order.BuyerId != request.BuyerId)
            return Result.Failure<string>(Error.Forbidden("gift_order.forbidden", "This isn't your order."));
        if (order.Status is not (GiftOrderStatus.Draft or GiftOrderStatus.AwaitingPayment))
            return Result.Failure<string>(Error.Conflict(
                "gift_order.not_payable", $"Order is {order.Status} and can no longer be paid."));

        var frontendUrl = (config["Frontend:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
        var lineItems = CheckoutLineItems.BuildFor(order);

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = order.Id.ToString(),
            SuccessUrl = $"{frontendUrl}/gift/confirmation?order_id={order.Id}&session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = $"{frontendUrl}/gift/new?cancelled=1",
            LineItems = lineItems,
            // "orderIds" (plural key, single value here) rather than a
            // separate "orderId" scheme, so the webhook has exactly one
            // parsing path shared with CreateMultiOrderCheckoutSession
            // instead of two different metadata shapes to handle.
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string> { ["orderIds"] = order.Id.ToString() }
            },
            Metadata = new Dictionary<string, string> { ["orderIds"] = order.Id.ToString() }
        };

        Session session;
        try
        {
            session = await new SessionService().CreateAsync(options, cancellationToken: ct);
        }
        catch (Stripe.StripeException e)
        {
            // Surface Stripe's own message (e.g. "Invalid API Key provided",
            // "Invalid currency", bad amount, etc.) instead of a raw 500 —
            // these are almost always a local configuration problem, not a
            // bug, and the real cause is only visible in e.StripeError.
            return Result.Failure<string>(Error.Failure(
                "stripe.checkout_session_failed",
                $"Stripe couldn't start checkout: {e.StripeError?.Message ?? e.Message}"));
        }

        var attached = order.Status == GiftOrderStatus.Draft
            ? order.BeginCheckout(session.PaymentIntentId)
            : order.ReattachCheckout(session.PaymentIntentId);
        if (attached.IsFailure) return Result.Failure<string>(attached.Error);

        await db.SaveChangesAsync(ct);
        return session.Url!;
    }
}

/// <summary>
/// Shared between the single-order and cart (multi-order) checkout
/// commands so the two never drift apart on what actually gets charged for
/// a given order — box/postal/service fee logic lives in exactly one place.
/// </summary>
public static class CheckoutLineItems
{
    public static List<SessionLineItemOptions> BuildFor(GiftOrder order)
    {
        var lineItems = new List<SessionLineItemOptions>
        {
            new()
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = order.PurchaseAmount.Currency.ToLowerInvariant(),
                    UnitAmountDecimal = order.PurchaseAmount.Amount * 100m, // Stripe wants minor units
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = $"GrowGift — {order.PurchaseAmount.Amount:0.00} {order.PurchaseAmount.Currency} of {order.Symbol}",
                        Description = $"A gift for {order.RecipientName} — real, fractional shares, purchased at redemption."
                    }
                }
            }
        };
        // Box and postal/service fee are separate line items (rather than
        // folded into the share amount above) so the buyer's Stripe receipt
        // itemizes exactly what they paid for — all come from the order's
        // own persisted, server-computed values, never recomputed from
        // anything client-supplied at this point.
        if (order.BoxPrice is { } boxPrice)
        {
            lineItems.Add(new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = boxPrice.Currency.ToLowerInvariant(),
                    UnitAmountDecimal = boxPrice.Amount * 100m,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = $"Gift box ({order.BoxSku})",
                        Description = "Physical packaging for the redemption card."
                    }
                }
            });
        }
        // Postal fee (physical delivery) and service fee (email delivery)
        // are mutually exclusive — GiftOrder.Create only ever sets one of
        // them, matching the chosen DeliveryMethod — so at most one of
        // these two blocks adds a line item, never both.
        if (order.PostalFee is { } postalFee)
        {
            lineItems.Add(new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = postalFee.Currency.ToLowerInvariant(),
                    UnitAmountDecimal = postalFee.Amount * 100m,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = "Postage",
                        Description = "Delivery of the redemption card."
                    }
                }
            });
        }
        if (order.ServiceFee is { } serviceFee)
        {
            lineItems.Add(new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = serviceFee.Currency.ToLowerInvariant(),
                    UnitAmountDecimal = serviceFee.Amount * 100m,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = "Service fee",
                        Description = "Digital delivery of the redemption code by email."
                    }
                }
            });
        }
        return lineItems;
    }
}
