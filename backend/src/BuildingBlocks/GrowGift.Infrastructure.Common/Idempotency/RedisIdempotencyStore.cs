using StackExchange.Redis;

namespace GrowGift.Infrastructure.Common.Idempotency;

public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis) : IIdempotencyStore
{
    private const string Prefix = "idem:";

    public async Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        return await db.StringSetAsync(Prefix + key, "claimed", ttl, When.NotExists);
    }

    public async Task<string?> GetCachedResponseAsync(string key, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = await db.StringGetAsync(Prefix + key + ":response");
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public async Task StoreResponseAsync(string key, string responseJson, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(Prefix + key + ":response", responseJson, ttl);
    }
}
