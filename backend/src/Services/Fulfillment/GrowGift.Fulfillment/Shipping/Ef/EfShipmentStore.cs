using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GrowGift.Fulfillment.Shipping.Ef;

/// <summary>
/// Postgres-backed store. NOT compiled in the offline test harness (EF/Npgsql unavailable there) — covered by the
/// in-memory store's identical contract in tests, and needs a real-database run before relying on it.
/// </summary>
public sealed class EfShipmentStore(FulfillmentDbContext db) : IShipmentStore
{
    public Task<Shipment?> GetAsync(Guid id, CancellationToken ct) => db.Shipments.FirstOrDefaultAsync(s => s.Id == id, ct);
    public Task<Shipment?> GetByOrderAsync(Guid orderId, CancellationToken ct) => db.Shipments.FirstOrDefaultAsync(s => s.OrderId == orderId, ct);
    public Task<Shipment?> GetByTrackingAsync(string tn, CancellationToken ct) => db.Shipments.FirstOrDefaultAsync(s => s.TrackingNumber == tn, ct);
    public Task<Shipment?> GetByReferenceAsync(string reference, CancellationToken ct) => db.Shipments.FirstOrDefaultAsync(s => s.Reference == reference, ct);

    public async Task<IReadOnlyList<Shipment>> ListByBuyerAsync(Guid buyerId, CancellationToken ct) =>
        await db.Shipments.AsNoTracking().Where(s => s.BuyerId == buyerId).OrderByDescending(s => s.CreatedAtUtc).Take(100).ToListAsync(ct);

    public async Task<IReadOnlyList<ShipmentEvent>> ListEventsAsync(Guid shipmentId, CancellationToken ct) =>
        await db.ShipmentEvents.AsNoTracking().Where(e => e.ShipmentId == shipmentId).OrderBy(e => e.ReceivedAtUtc).ToListAsync(ct);

    public async Task<(Shipment Shipment, bool Created)> CreateIfAbsentAsync(Shipment s, CancellationToken ct)
    {
        var existing = await GetByOrderAsync(s.OrderId, ct);
        if (existing is not null) return (existing, false);
        db.Shipments.Add(s);
        try { await db.SaveChangesAsync(ct); return (s, true); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same paid event raced; the unique OrderId index picked a winner.
            db.ChangeTracker.Clear();
            return ((await GetByOrderAsync(s.OrderId, ct))!, false);
        }
    }

    public async Task<bool> SaveAsync(Shipment s, CancellationToken ct)
    {
        s.RowVersion++;      // EF compares the ORIGINAL value in the UPDATE's WHERE clause, so a concurrent writer makes this affect 0 rows
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();      // the caller reloads fresh state
            return false;
        }
    }

    public async Task<Shipment?> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        var until = now + lease;
        // FOR UPDATE SKIP LOCKED: two workers (or two service instances) never claim the same row.
        // Finished shipments stay claimable only while an email is still owed.
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE fulfillment.shipments SET "LeaseUntilUtc" = {until}, "RowVersion" = "RowVersion" + 1
            WHERE "Id" = (
                SELECT "Id" FROM fulfillment.shipments
                WHERE ( "Status" NOT IN (8, 9, 11) OR ("Status" <> 11 AND ("ReachedMask" & ~"NotifiedMask") <> 0) )
                  AND "NextAttemptAtUtc" <= {now}
                  AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" <= {now})
                ORDER BY "NextAttemptAtUtc"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
        if (ids.Count == 0) return null;
        db.ChangeTracker.Clear();
        return await db.Shipments.FirstOrDefaultAsync(s => s.Id == ids[0], ct);
    }

    public async Task<bool> TryAddEventAsync(ShipmentEvent e, CancellationToken ct)
    {
        // Raw INSERT … ON CONFLICT so a duplicate is a normal "0 rows" result and never disturbs tracked shipment state.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fulfillment.shipment_events ("Id","ShipmentId","EventKey","Source","ShippitState","OccurredAtUtc","ReceivedAtUtc","Outcome")
            VALUES ({e.Id},{e.ShipmentId},{e.EventKey},{e.Source},{e.ShippitState},{e.OccurredAtUtc},{e.ReceivedAtUtc},{e.Outcome})
            ON CONFLICT ("ShipmentId","EventKey") DO NOTHING
            """, ct);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Shipment>> ListSilentAsync(DateTimeOffset silentSince, int take, CancellationToken ct) =>
        await db.Shipments.AsNoTracking()
            .Where(s => (s.Status == ShipmentStatus.Booked || s.Status == ShipmentStatus.InTransit || s.Status == ShipmentStatus.Exception)
                        && (s.LastEventAtUtc ?? s.CreatedAtUtc) <= silentSince)
            .Take(take).ToListAsync(ct);
}
