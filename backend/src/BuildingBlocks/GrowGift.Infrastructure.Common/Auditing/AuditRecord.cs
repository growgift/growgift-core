namespace GrowGift.Infrastructure.Common.Auditing;

/// <summary>
/// Append-only audit row. The table is protected by a Postgres trigger that
/// rejects UPDATE and DELETE (see infra/docker/init.sql), and each row chains
/// the SHA-256 of the previous row for tamper evidence.
/// </summary>
public sealed class AuditRecord
{
    public long Sequence { get; init; }                 // bigserial, assigned by DB
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Actor { get; init; } = default!;       // user id or "system"
    public string Action { get; init; } = default!;      // e.g. gift.redeemed
    public string SubjectType { get; init; } = default!;
    public string SubjectId { get; init; } = default!;
    public string? MetadataJson { get; init; }
    public string? IpAddress { get; init; }
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PreviousHashHex { get; init; } = default!;
    public string HashHex { get; init; } = default!;
}

public interface IAuditWriter
{
    Task WriteAsync(string actor, string action, string subjectType, string subjectId,
        object? metadata = null, string? ipAddress = null, CancellationToken ct = default);
}
