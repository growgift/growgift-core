namespace GrowGift.Contracts.IntegrationEvents;

[EventDomain("fulfillment")]
public sealed record FulfillmentOrderCreatedV1(Guid OrderId, Guid FulfillmentOrderId) : IntegrationEvent;

[EventDomain("fulfillment")]
public sealed record ShipmentDispatchedV1(Guid FulfillmentOrderId, string Carrier, string TrackingNumber) : IntegrationEvent;

[EventDomain("fulfillment")]
public sealed record ShipmentDeliveredV1(Guid FulfillmentOrderId, DateTimeOffset DeliveredAtUtc) : IntegrationEvent;
