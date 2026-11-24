namespace GrowGift.Contracts.IntegrationEvents;

[EventDomain("gifting")]
public sealed record GiftOrderPaidV1(
    Guid OrderId,
    Guid BuyerId,
    string Symbol,
    decimal DollarAmount,
    string Currency,
    bool HasPhysicalBox) : IntegrationEvent;

[EventDomain("gifting")]
public sealed record SharesAcquiredV1(
    Guid OrderId,
    string Symbol,
    decimal Quantity,
    decimal AverageFillPrice,
    string OmnibusLotRef) : IntegrationEvent;

[EventDomain("gifting")]
public sealed record RedemptionCodeIssuedV1(
    Guid OrderId,
    Guid GiftId,
    string CodeHashHex,
    DateTimeOffset ExpiresAtUtc) : IntegrationEvent;

[EventDomain("gifting")]
public sealed record GiftRedeemedV1(
    Guid GiftId,
    Guid RecipientUserId,
    Guid? BeneficiaryChildId,
    string ViewTradeAccountRef,
    string Symbol,
    decimal Amount,
    string Currency) : IntegrationEvent;

[EventDomain("gifting")]
public sealed record OwnershipTransferredV1(
    Guid GiftId,
    string Symbol,
    decimal Quantity,
    string FromOmnibusLotRef,
    string ToViewTradeAccountRef) : IntegrationEvent;
