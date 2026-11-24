namespace GrowGift.Contracts.IntegrationEvents;

/// <summary>
/// Integration events are the public, versioned contract between services.
/// They are published to Kafka via the transactional outbox. Never mutate a
/// published contract: add V2 records instead.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Tags an integration event with the service that publishes it, so
/// TopicNaming (in GrowGift.Infrastructure.Common) can compute the correct
/// Kafka topic. Every event needs this — without it, TopicNaming falls back
/// to the "platform" domain, which won't match a consumer subscribed to a
/// more specific hardcoded topic string.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventDomainAttribute(string domain) : Attribute
{
    public string Domain { get; } = domain;
}
