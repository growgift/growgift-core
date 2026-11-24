using GrowGift.Gifting.Domain;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;

namespace GrowGift.Gifting.Application.Commands;

/// <summary>
/// The cart-checkout equivalent of CreateCheckoutSessionCommand: combines
/// several already-created orders into ONE Stripe Checkout Session (one
/// card charge, one PaymentIntent) rather than a separate payment per item.
/// All the selected orders share the resulting PaymentIntentId — see
/// GiftingDbContext's note on why that index isn't unique — and the
/// webhook (StripeWebhook in GiftingEndpoints) confirms every one of them
/// from the same "orderIds" metadata list this sets.
/// </summary>
public sealed record CreateMultiOrderCheckoutSessionCommand(IReadOnlyList<Guid> OrderIds, Guid BuyerId)
    : IRequest<Result<string>>;

public sealed class CreateMultiOrderCheckoutSessionHandler(GiftingDbContext db, IConfiguration config)
    : IRequestHandler<CreateMultiOrderCheckoutSessionCommand, Result<string>>
{
    public async Task<Result<string>> Handle(CreateMultiOrderCheckoutSessionCommand request, CancellationToken ct)
    {
        if (request.OrderIds.Count == 0)
            return Result.Failure<string>(Error.Validation("gift_order.empty_selection", "Select at least one item to check out."));

        var distinctIds = request.OrderIds.Distinct().ToList();
        var orders = await db.GiftOrders.Where(o => distinctIds.Contains(o.Id)).ToListAsync(ct);

        if (orders.Count != distinctIds.Count)
            return Result.Failure<string>(Error.NotFound("gift_order.not_found", "One or more selected items no longer exist."));
        if (orders.Any(o => o.BuyerId != request.BuyerId))
            return Result.Failure<string>(Error.Forbidden("gift_order.forbidden", "One or more selected items aren't yours."));
        var unpayable = orders.FirstOrDefault(o => o.Status is not (GiftOrderStatus.Draft or GiftOrderStatus.AwaitingPayment));
        if (unpayable is not null)
            return Result.Failure<string>(Error.Conflict(
                "gift_order.not_payable", $"One item is {unpayable.Status} and can no longer be paid."));

        var frontendUrl = (config["Frontend:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
        var lineItems = orders.SelectMany(CheckoutLineItems.BuildFor).ToList();
        var orderIdsCsv = string.Join(",", orders.Select(o => o.Id));

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = $"{frontendUrl}/gift/confirmation?order_ids={orderIdsCsv}&session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = $"{frontendUrl}/cart?cancelled=1",
            LineItems = lineItems,
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string> { ["orderIds"] = orderIdsCsv }
            },
            Metadata = new Dictionary<string, string> { ["orderIds"] = orderIdsCsv }
        };

        Session session;
        try
        {
            session = await new SessionService().CreateAsync(options, cancellationToken: ct);
        }
        catch (Stripe.StripeException e)
        {
            return Result.Failure<string>(Error.Failure(
                "stripe.checkout_session_failed",
                $"Stripe couldn't start checkout: {e.StripeError?.Message ?? e.Message}"));
        }

        foreach (var order in orders)
        {
            var attached = order.Status == GiftOrderStatus.Draft
                ? order.BeginCheckout(session.PaymentIntentId)
                : order.ReattachCheckout(session.PaymentIntentId);
            if (attached.IsFailure) return Result.Failure<string>(attached.Error);
        }

        await db.SaveChangesAsync(ct);
        return session.Url!;
    }
}
