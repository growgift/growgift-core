using FluentValidation;
using GrowGift.Gifting.Domain;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace GrowGift.Gifting.Application.Commands;

public sealed record CreateGiftOrderCommand(
    Guid BuyerId,
    string BuyerEmail,
    string Symbol,
    decimal Amount,
    string Currency,
    string RecipientName,
    string? GreetingMessage,
    string? BoxSku,
    DeliveryMethod DeliveryMethod,
    string? RecipientEmail) : IRequest<Result<CreatedGiftOrderDto>>;

public sealed record CreatedGiftOrderDto(
    Guid Id, decimal Amount, string Currency, decimal? BoxPrice,
    decimal? PostalFee, decimal? ServiceFee, decimal TotalAmount);

public sealed class CreateGiftOrderValidator : AbstractValidator<CreateGiftOrderCommand>
{
    public CreateGiftOrderValidator()
    {
        RuleFor(x => x.Symbol).NotEmpty().MaximumLength(10).Matches("^[A-Za-z.]+$");
        RuleFor(x => x.RecipientName).NotEmpty().MaximumLength(60);
        // $20–$4,500 AUD — the preset amount buttons on the frontend
        // (currently $25/$50/$100/$250) all already fall inside this
        // range, so this applies uniformly to the whole field regardless
        // of whether it came from a preset or the custom-amount input;
        // there's no way to tell those apart server-side, nor any reason
        // the limit should differ between them.
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(20m).LessThanOrEqualTo(4_500m);
        RuleFor(x => x.Currency).Length(3);
        RuleFor(x => x.GreetingMessage).MaximumLength(500);
        RuleFor(x => x.BuyerEmail).NotEmpty().EmailAddress();
        // The price for whichever SKU is looked up server-side in
        // GiftOrder.Create (GiftOrder.BoxPrices) — validated here only for
        // early, clear feedback; the actual trust boundary is there, not
        // this check.
        RuleFor(x => x.BoxSku).Must(sku => sku is null || GiftOrder.BoxPrices.ContainsKey(sku))
            .WithMessage("'{PropertyValue}' isn't a recognized gift box.");
        RuleFor(x => x.RecipientEmail).NotEmpty().EmailAddress()
            .When(x => x.DeliveryMethod == DeliveryMethod.Email)
            .WithMessage("A valid recipient email is required for email delivery.");
        RuleFor(x => x.BoxSku).Null()
            .When(x => x.DeliveryMethod == DeliveryMethod.Email)
            .WithMessage("A gift box isn't applicable when delivering by email.");
    }
}

public sealed class CreateGiftOrderHandler(GiftingDbContext db, IConfiguration config)
    : IRequestHandler<CreateGiftOrderCommand, Result<CreatedGiftOrderDto>>
{
    public async Task<Result<CreatedGiftOrderDto>> Handle(CreateGiftOrderCommand request, CancellationToken ct)
    {
        // Configurable without a code change — see appsettings.json's Fees
        // section. Read here (the application layer), not inside GiftOrder
        // itself, so the domain model stays free of any direct dependency
        // on IConfiguration; these are just plain decimal parameters to it.
        var postalFeeAmount = config.GetValue<decimal?>("Fees:PostalCharge") ?? 6m;
        var serviceFeeAmount = config.GetValue<decimal?>("Fees:ServiceCharge") ?? 7m;

        var orderResult = GiftOrder.Create(
            request.BuyerId,
            request.BuyerEmail,
            request.Symbol,
            Money.Of(request.Amount, request.Currency),
            request.RecipientName,
            request.GreetingMessage,
            request.BoxSku,
            request.DeliveryMethod,
            request.RecipientEmail,
            postalFeeAmount,
            serviceFeeAmount);

        if (orderResult.IsFailure)
            return Result.Failure<CreatedGiftOrderDto>(orderResult.Error);

        var order = orderResult.Value;
        db.GiftOrders.Add(order);
        await db.SaveChangesAsync(ct);

        // Echoes back exactly what was persisted (and what
        // CreateCheckoutSession will actually charge from) rather than
        // recomputing the same fee lookup a second time here — a single
        // source of truth for what these numbers are, rather than two
        // places that could silently drift apart.
        return new CreatedGiftOrderDto(
            order.Id, order.PurchaseAmount.Amount, order.PurchaseAmount.Currency,
            order.BoxPrice?.Amount, order.PostalFee?.Amount, order.ServiceFee?.Amount,
            order.TotalAmount.Amount);
    }
}
