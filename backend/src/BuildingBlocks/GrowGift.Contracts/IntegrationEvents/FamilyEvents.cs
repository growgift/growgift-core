namespace GrowGift.Contracts.IntegrationEvents;

[EventDomain("family")]
public sealed record ChildTurned18V1(Guid ChildId, Guid ParentUserId) : IntegrationEvent;

[EventDomain("family")]
public sealed record TaskRewardApprovedV1(
    Guid TaskId,
    Guid ChildId,
    decimal RewardAmount,
    string Currency) : IntegrationEvent;
