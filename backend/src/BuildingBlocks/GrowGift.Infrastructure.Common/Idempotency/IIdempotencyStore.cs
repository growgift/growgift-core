namespace GrowGift.Infrastructure.Common.Idempotency;

/// <summary>
/// Redis-backed idempotency for API commands and Kafka consumers. A key is
/// claimed atomically; the second claim within the TTL is rejected, making
/// financial commands safe to retry.
/// </summary>
public interface IIdempotencyStore
{
    Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct = default);
    Task<string?> GetCachedResponseAsync(string key, CancellationToken ct = default);
    Task StoreResponseAsync(string key, string responseJson, TimeSpan ttl, CancellationToken ct = default);
}
