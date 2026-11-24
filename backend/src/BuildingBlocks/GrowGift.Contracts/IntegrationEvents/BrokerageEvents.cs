namespace GrowGift.Contracts.IntegrationEvents;

/// <summary>
/// Emitted by Brokerage once a ViewTrade onboarding application has been
/// fully submitted and ViewTrade has responded with an account reference.
/// Status is "approved" (account is live) or "pending" (ViewTrade is still
/// reviewing — Identity should NOT mark KYC approved yet in that case).
/// </summary>
[EventDomain("brokerage")]
public sealed record AccountOpenedV1(Guid UserId, string AccountRef, string Status) : IntegrationEvent;
