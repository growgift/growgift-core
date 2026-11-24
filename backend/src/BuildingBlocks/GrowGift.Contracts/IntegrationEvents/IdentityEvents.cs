namespace GrowGift.Contracts.IntegrationEvents;

[EventDomain("identity")]
public sealed record UserRegisteredV1(Guid UserId, string Email, string Role) : IntegrationEvent;

[EventDomain("identity")]
public sealed record KycCompletedV1(Guid UserId, string PersonaInquiryRef, string Outcome) : IntegrationEvent;
