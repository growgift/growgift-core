using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Gifting.Domain;

public enum GiftOrderStatus
{
    Draft = 0,
    AwaitingPayment = 1,
    Paid = 2,
    SharesAcquired = 3,
    CodeIssued = 4,
    Shipped = 5,
    Redeemed = 6,
    Expired = 7,
    Cancelled = 8,
    Refunded = 9
}

/// <summary>
/// Physical means a printed card (optionally with a gift box) goes out via
/// Fulfillment, and a flat postal fee applies. Email means the recipient
/// gets the redemption code directly by email instead — no physical item
/// at all, Fulfillment is skipped entirely, and a flat service fee applies
/// instead of postage. Exactly one of GiftOrder's PostalFee/ServiceFee is
/// ever set, matching which of these was chosen.
/// </summary>
public enum DeliveryMethod { Physical = 0, Email = 1 }

/// <summary>
/// The buyer's purchase. The buyer never owns securities: the order tracks a
/// dollar amount which the Brokerage service converts into an omnibus lot held
/// by GrowGift until redemption. Status transitions are enforced here — the
/// aggregate is the only place allowed to move an order between states.
/// </summary>
public sealed class GiftOrder : AggregateRoot<Guid>
{
    private GiftOrder() { } // EF

    /// <summary>
    /// Server-side prices for each gift box tier — the only source of truth
    /// for what a box actually costs. A client-supplied SKU is looked up
    /// here to derive its price; a client-supplied PRICE is never trusted
    /// for anything that becomes a real Stripe charge. Must stay in sync
    /// with the frontend's own display copy of this list (checkout page),
    /// but the frontend's copy is presentation only — this one is what
    /// actually gets charged.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, decimal> BoxPrices = new Dictionary<string, decimal>
    {
        ["BOX-CLASSIC"] = 12m,
        ["BOX-DELUXE"] = 29m,
        ["BOX-GRAND"] = 49m,
    };

    private static readonly Dictionary<GiftOrderStatus, GiftOrderStatus[]> AllowedTransitions = new()
    {
        [GiftOrderStatus.Draft] = [GiftOrderStatus.AwaitingPayment, GiftOrderStatus.Cancelled],
        [GiftOrderStatus.AwaitingPayment] = [GiftOrderStatus.Paid, GiftOrderStatus.Cancelled],
        [GiftOrderStatus.Paid] = [GiftOrderStatus.CodeIssued, GiftOrderStatus.SharesAcquired, GiftOrderStatus.Refunded],
        [GiftOrderStatus.SharesAcquired] = [GiftOrderStatus.CodeIssued],
        [GiftOrderStatus.CodeIssued] = [GiftOrderStatus.Shipped, GiftOrderStatus.Redeemed, GiftOrderStatus.Expired],
        [GiftOrderStatus.Shipped] = [GiftOrderStatus.Redeemed, GiftOrderStatus.Expired],
        [GiftOrderStatus.Expired] = [GiftOrderStatus.CodeIssued], // reissue path
    };

    public Guid BuyerId { get; private set; }
    public string BuyerEmail { get; private set; } = default!;
    public string Symbol { get; private set; } = default!;
    public Money PurchaseAmount { get; private set; } = default!;
    public string? RecipientName { get; private set; }
    public string? RecipientEmail { get; private set; }
    public string? GreetingMessage { get; private set; }
    public DeliveryMethod DeliveryMethod { get; private set; }
    public string? BoxSku { get; private set; }
    public Money? BoxPrice { get; private set; }
    // Exactly one of these is set, matching DeliveryMethod — see its own doc.
    // Both nullable (rather than PostalFee always-set, defaulting to zero
    // for email orders) so a query/response can tell "doesn't apply" apart
    // from "applies, and happens to be configured at zero" without a
    // separate flag.
    public Money? PostalFee { get; private set; }
    public Money? ServiceFee { get; private set; }
    public bool HasPhysicalBox => BoxSku is not null;
    public GiftOrderStatus Status { get; private set; }
    public string? StripePaymentIntentId { get; private set; }
    public string? OmnibusLotRef { get; private set; }
    public decimal? AcquiredQuantity { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>What the buyer actually pays: share amount + box (if any) + postal/service fee.</summary>
    public Money TotalAmount
    {
        get
        {
            var total = PurchaseAmount;
            if (BoxPrice is { } box) total = total.Add(box);
            if (PostalFee is { } postal) total = total.Add(postal);
            if (ServiceFee is { } service) total = total.Add(service);
            return total;
        }
    }

    public static Result<GiftOrder> Create(
        Guid buyerId, string buyerEmail, string symbol, Money amount, string? recipientName, string? greeting,
        string? boxSku, DeliveryMethod deliveryMethod, string? recipientEmail,
        decimal postalFeeAmount, decimal serviceFeeAmount)
    {
        if (string.IsNullOrWhiteSpace(recipientName) || recipientName.Length > 60)
            return Result.Failure<GiftOrder>(Error.Validation(
                "gift_order.recipient_name", "Recipient name is required and must be 60 characters or fewer."));

        if (amount.IsZero || amount.IsNegative)
            return Result.Failure<GiftOrder>(Error.Validation(
                "gift_order.amount", "Purchase amount must be positive."));

        if (greeting is { Length: > 500 })
            return Result.Failure<GiftOrder>(Error.Validation(
                "gift_order.greeting", "Greeting message must be 500 characters or fewer."));

        if (deliveryMethod == DeliveryMethod.Email)
        {
            if (string.IsNullOrWhiteSpace(recipientEmail) || !recipientEmail.Contains('@') || recipientEmail.Length > 254)
                return Result.Failure<GiftOrder>(Error.Validation(
                    "gift_order.recipient_email", "A valid recipient email is required for email delivery."));
            if (boxSku is not null)
                return Result.Failure<GiftOrder>(Error.Validation(
                    "gift_order.box_sku", "A gift box isn't applicable when delivering by email."));
        }

        Money? boxPrice = null;
        if (boxSku is not null)
        {
            if (!BoxPrices.TryGetValue(boxSku, out var price))
                return Result.Failure<GiftOrder>(Error.Validation(
                    "gift_order.box_sku", $"'{boxSku}' isn't a recognized gift box."));
            boxPrice = Money.Of(price, amount.Currency);
        }

        return new GiftOrder
        {
            Id = Guid.NewGuid(),
            BuyerId = buyerId,
            BuyerEmail = buyerEmail,
            Symbol = symbol.ToUpperInvariant(),
            PurchaseAmount = amount,
            RecipientName = recipientName.Trim(),
            RecipientEmail = deliveryMethod == DeliveryMethod.Email ? recipientEmail!.Trim().ToLowerInvariant() : null,
            GreetingMessage = greeting,
            DeliveryMethod = deliveryMethod,
            BoxSku = deliveryMethod == DeliveryMethod.Physical ? boxSku : null,
            BoxPrice = deliveryMethod == DeliveryMethod.Physical ? boxPrice : null,
            PostalFee = deliveryMethod == DeliveryMethod.Physical ? Money.Of(postalFeeAmount, amount.Currency) : null,
            ServiceFee = deliveryMethod == DeliveryMethod.Email ? Money.Of(serviceFeeAmount, amount.Currency) : null,
            Status = GiftOrderStatus.Draft,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public Result BeginCheckout(string stripePaymentIntentId)
    {
        var result = TransitionTo(GiftOrderStatus.AwaitingPayment);
        if (result.IsFailure) return result;
        StripePaymentIntentId = stripePaymentIntentId;
        return Result.Success();
    }

    /// <summary>
    /// Regenerates the Stripe PaymentIntent for an order that's already
    /// AwaitingPayment (e.g. the buyer abandoned checkout and tries again).
    /// No status transition — just points at the new intent.
    /// </summary>
    public Result ReattachCheckout(string stripePaymentIntentId)
    {
        if (Status != GiftOrderStatus.AwaitingPayment)
            return Result.Failure(Error.Conflict(
                "gift_order.invalid_transition", $"Cannot reattach checkout while order is {Status}."));
        StripePaymentIntentId = stripePaymentIntentId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result MarkPaid(string stripePaymentIntentId)
    {
        var result = TransitionTo(GiftOrderStatus.Paid);
        if (result.IsFailure) return result;
        // Recorded here, not just at checkout-session creation (BeginCheckout/
        // ReattachCheckout) — Stripe's payment_intent field on a Checkout
        // Session can be null immediately after creation, only populating
        // once the customer actually completes payment. Relying solely on
        // that earlier write meant this could stay empty forever for an
        // order that legitimately gets paid; this is the point where we
        // definitely have the real, confirmed id (it's literally the
        // PaymentIntent the webhook just told us succeeded).
        StripePaymentIntentId = stripePaymentIntentId;
        Raise(new Events.GiftOrderPaid(Id, BuyerId, Symbol, PurchaseAmount.Amount, PurchaseAmount.Currency, HasPhysicalBox));
        return Result.Success();
    }

    public Result RecordAcquisition(decimal quantity, string omnibusLotRef)
    {
        var result = TransitionTo(GiftOrderStatus.SharesAcquired);
        if (result.IsFailure) return result;
        AcquiredQuantity = quantity;
        OmnibusLotRef = omnibusLotRef;
        return Result.Success();
    }

    public Result MarkCodeIssued() => TransitionTo(GiftOrderStatus.CodeIssued);
    public Result MarkShipped() => TransitionTo(GiftOrderStatus.Shipped);
    public Result MarkRedeemed() => TransitionTo(GiftOrderStatus.Redeemed);
    public Result MarkExpired() => TransitionTo(GiftOrderStatus.Expired);

    private Result TransitionTo(GiftOrderStatus next)
    {
        if (!AllowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
            return Result.Failure(Error.Conflict(
                "gift_order.invalid_transition",
                $"Cannot move order from {Status} to {next}."));

        Status = next;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public static class Events
    {
        public sealed record GiftOrderPaid(
            Guid OrderId, Guid BuyerId, string Symbol,
            decimal Amount, string Currency, bool HasPhysicalBox) : DomainEvent;
    }
}
