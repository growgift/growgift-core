using GrowGift.Contracts.IntegrationEvents;

namespace GrowGift.Infrastructure.Common.Messaging;

/// <summary>
/// Enqueues an integration event into the outbox within the ambient EF Core
/// transaction. Actual Kafka publication happens asynchronously via the relay.
/// </summary>
public interface IEventPublisher
{
    Task EnqueueAsync<TEvent>(TEvent integrationEvent, string? partitionKey = null, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
}
