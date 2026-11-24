using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Gifting.Domain;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Application.Commands;

/// <summary>
/// Invoked by the Stripe webhook endpoint after signature verification.
/// Idempotent: replays of the same webhook find the order already Paid and
/// return success without side effects.
///
/// Looks the order up by OrderId (from the PaymentIntent's own metadata —
/// see CreateCheckoutSession's PaymentIntentData.Metadata), not by a
/// previously-saved StripePaymentIntentId. Stripe's payment_intent field on
/// a Checkout Session can be null immediately after creation, only
/// populating once the customer completes payment — matching by a value
/// that might never have been successfully saved meant a real, paid order
/// could sit in AwaitingPayment forever despite the webhook itself arriving
/// and verifying correctly (confirmed: an order's StripePaymentIntentId
/// column was empty in the database despite Stripe reporting the payment
/// succeeded). The orderId in metadata has no such timing dependency — it's
/// set at session creation and never changes.
/// </summary>
public sealed record ConfirmPaymentCommand(Guid OrderId, string StripePaymentIntentId) : IRequest<Result>;

public sealed class ConfirmPaymentHandler(GiftingDbContext db, IEventPublisher publisher)
    : IRequestHandler<ConfirmPaymentCommand, Result>
{
    public async Task<Result> Handle(ConfirmPaymentCommand request, CancellationToken ct)
    {
        var order = await db.GiftOrders.FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);

        if (order is null)
            return Result.Failure(Error.NotFound("gift_order.not_found", "No order matches this payment."));

        if (order.Status >= GiftOrderStatus.Paid)
            return Result.Success(); // webhook replay — already handled

        var result = order.MarkPaid(request.StripePaymentIntentId);
        if (result.IsFailure) return result;

        await publisher.EnqueueAsync(new GiftOrderPaidV1(
            order.Id, order.BuyerId, order.Symbol,
            order.PurchaseAmount.Amount, order.PurchaseAmount.Currency,
            order.HasPhysicalBox), partitionKey: order.Id.ToString(), ct);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
