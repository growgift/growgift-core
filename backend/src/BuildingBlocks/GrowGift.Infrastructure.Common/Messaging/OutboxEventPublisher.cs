using System.Text.Json;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Infrastructure.Common.Outbox;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Infrastructure.Common.Messaging;

public sealed class OutboxEventPublisher<TDbContext>(TDbContext db) : IEventPublisher
    where TDbContext : DbContext
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public Task EnqueueAsync<TEvent>(TEvent integrationEvent, string? partitionKey = null, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var eventType = integrationEvent.GetType();
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = eventType.FullName!,
            Payload = JsonSerializer.Serialize(integrationEvent, eventType, Options),
            Topic = TopicNaming.For(eventType),
            PartitionKey = partitionKey,
            OccurredAtUtc = integrationEvent.OccurredAtUtc
        });
        return Task.CompletedTask;
    }
}

public static class TopicNaming
{
    /// <summary>GiftOrderPaidV1 -> growgift.gifting.gift-order-paid.v1</summary>
    public static string For(Type eventType)
    {
        var name = eventType.Name;
        var version = "v1";
        var vIndex = name.LastIndexOf('V');
        if (vIndex > 0 && int.TryParse(name[(vIndex + 1)..], out _))
        {
            version = name[vIndex..].ToLowerInvariant();
            name = name[..vIndex];
        }

        var kebab = string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        // BUG FIX: this used to check eventType.Namespace!.Contains("Gifting"),
        // but every integration event lives in the single shared namespace
        // GrowGift.Contracts.IntegrationEvents — "GrowGift" never contains
        // "Gifting" as a substring, so that check was always false and every
        // event's topic silently resolved to the "platform" domain. Consumers
        // subscribe to hardcoded "growgift.gifting.*" topics, so this meant
        // GiftOrderPaidV1 and GiftRedeemedV1 were published to
        // "growgift.platform.*" while their consumers listened on
        // "growgift.gifting.*" — different topics that never met. Fixed by
        // reading an explicit [EventDomain] attribute instead of guessing
        // from the namespace.
        var domainAttr = eventType.GetCustomAttributes(typeof(EventDomainAttribute), inherit: false)
            .Cast<EventDomainAttribute>().FirstOrDefault();
        var domain = domainAttr?.Domain ?? "platform";
        return $"growgift.{domain}.{kebab}.{version}";
    }
}
