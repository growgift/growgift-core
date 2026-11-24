using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrowGift.Infrastructure.Common.Auditing;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Infrastructure.Auditing;

/// <summary>
/// Hash-chained, append-only audit log. Each row includes the SHA-256 of the
/// previous row so retroactive tampering is detectable; the table itself
/// rejects UPDATE/DELETE via trigger (see infra/docker/init.sql).
/// </summary>
public sealed class PostgresAuditWriter(Persistence.GiftingDbContext db) : IAuditWriter
{
    public async Task WriteAsync(string actor, string action, string subjectType, string subjectId,
        object? metadata = null, string? ipAddress = null, CancellationToken ct = default)
    {
        var metadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata);
        var previous = await db.Database
            .SqlQuery<string>($"SELECT hash_hex AS \"Value\" FROM gifting.audit_log ORDER BY sequence DESC LIMIT 1")
            .FirstOrDefaultAsync(ct) ?? new string('0', 64);

        var atUtc = DateTimeOffset.UtcNow;
        var material = $"{previous}|{actor}|{action}|{subjectType}|{subjectId}|{metadataJson}|{atUtc:O}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

        await db.Database.ExecuteSqlAsync($@"
            INSERT INTO gifting.audit_log
                (id, actor, action, subject_type, subject_id, metadata_json, ip_address, at_utc, previous_hash_hex, hash_hex)
            VALUES
                ({Guid.NewGuid()}, {actor}, {action}, {subjectType}, {subjectId},
                 {metadataJson}::jsonb, {ipAddress}, {atUtc}, {previous}, {hash})", ct);
    }
}
