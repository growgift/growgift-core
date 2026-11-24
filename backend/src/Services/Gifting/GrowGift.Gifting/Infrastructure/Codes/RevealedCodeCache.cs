using StackExchange.Redis;

namespace GrowGift.Gifting.Infrastructure.Codes;

/// <summary>
/// Holds the plaintext redemption code so the buyer can see it both on the
/// confirmation page right after paying and later in their profile's
/// "gifts you've given" list — a deliberate relaxation of this codebase's
/// usual rule that the plaintext code is never persisted (see
/// IssueRedemptionCode's and GiftOrderPaidConsumer's own docs: normally it
/// exists only in memory, handed to Fulfillment/Notifications, then
/// dropped). Originally a 30-minute window for just the confirmation-page
/// case; widened to Ttl's current value once the profile page needed the
/// same data indefinitely — a buyer reasonably expects to see their own
/// gift's code whenever they check back, not just in the minutes after
/// purchase. Still scoped as narrowly as that need allows: Redis (not the
/// primary database), and only reachable by the order's own buyer (see the
/// /orders/{id}/code endpoint's ownership check).
/// </summary>
public interface IRevealedCodeCache
{
    Task SetAsync(Guid orderId, string plaintextCode, CancellationToken ct = default);
    Task<string?> TryGetAsync(Guid orderId, CancellationToken ct = default);
}

public sealed class RedisRevealedCodeCache(IConnectionMultiplexer redis) : IRevealedCodeCache
{
    private const string Prefix = "revealed-code:";

    // Originally 30 minutes — just long enough for the confirmation page's
    // immediate-reveal use case. Now also backs the profile page's
    // permanent "gifts you've given" view, where a buyer reasonably expects
    // to see their own gift's code whenever they check back — 30 minutes
    // doesn't serve that at all. This is a genuine, deliberate widening of
    // the original "brief, one-time" exception (see this class's own doc)
    // into something closer to ongoing storage; 1 year in practice covers
    // essentially the lifetime of an ungifted/unredeemed code without
    // literally being "forever."
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(365);

    public async Task SetAsync(Guid orderId, string plaintextCode, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(Prefix + orderId, plaintextCode, Ttl);
    }

    public async Task<string?> TryGetAsync(Guid orderId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = await db.StringGetAsync(Prefix + orderId);
        return value.IsNullOrEmpty ? null : value.ToString();
    }
}
