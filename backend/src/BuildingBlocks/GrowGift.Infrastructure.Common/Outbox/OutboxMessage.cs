namespace GrowGift.Infrastructure.Common.Outbox;

/// <summary>
/// Transactional outbox row. Written in the same DB transaction as the state
/// change, then relayed to Kafka by <see cref="OutboxRelayService"/>. This
/// guarantees at-least-once publication; consumers must be idempotent.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Type { get; init; } = default!;        // CLR type name of the integration event
    public string Payload { get; init; } = default!;     // JSON
    public string Topic { get; init; } = default!;
    public string? PartitionKey { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
