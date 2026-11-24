namespace GrowGift.Fulfillment.Shipping;

/// <summary>Test double with the same contract as the EF store (unique order, unique event key, optimistic version, leases).</summary>
public sealed class InMemoryShipmentStore : IShipmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (Shipment S, int V)> _rows = new();
    private readonly HashSet<(Guid, string)> _events = new();
    private readonly List<ShipmentEvent> _eventList = new();
    private readonly Func<DateTimeOffset> _now;
    public InMemoryShipmentStore(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    private static Shipment Clone(Shipment s) => (Shipment)Cloner.Invoke(s, null)!;
    private static readonly System.Reflection.MethodInfo Cloner = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    public int Count { get { lock (_gate) return _rows.Count; } }
    public IReadOnlyList<ShipmentEvent> AllEvents { get { lock (_gate) return _eventList.ToList(); } }

    public Task<Shipment?> GetAsync(Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_rows.TryGetValue(id, out var r) ? Wrap(r) : null); }
    public Task<Shipment?> GetByOrderAsync(Guid orderId, CancellationToken ct) { lock (_gate) return Task.FromResult(Find(x => x.OrderId == orderId)); }
    public Task<Shipment?> GetByTrackingAsync(string tn, CancellationToken ct) { lock (_gate) return Task.FromResult(Find(x => string.Equals(x.TrackingNumber, tn, StringComparison.OrdinalIgnoreCase))); }
    public Task<Shipment?> GetByReferenceAsync(string reference, CancellationToken ct) { lock (_gate) return Task.FromResult(Find(x => x.Reference == reference)); }
    public Task<IReadOnlyList<Shipment>> ListByBuyerAsync(Guid buyerId, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<Shipment>>(_rows.Values.Where(r => r.S.BuyerId == buyerId).Select(Wrap).OrderByDescending(x => x!.CreatedAtUtc).ToList()!); }
    public Task<IReadOnlyList<ShipmentEvent>> ListEventsAsync(Guid id, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<ShipmentEvent>>(_eventList.Where(e => e.ShipmentId == id).OrderBy(e => e.ReceivedAtUtc).ToList()); }

    public Task<(Shipment, bool)> CreateIfAbsentAsync(Shipment s, CancellationToken ct)
    {
        lock (_gate)
        {
            var ex = Find(x => x.OrderId == s.OrderId);
            if (ex is not null) return Task.FromResult((ex, false));
            _rows[s.Id] = (Clone(s), 1);
            return Task.FromResult((Find(x => x.Id == s.Id)!, true));
        }
    }

    public Task<bool> SaveAsync(Shipment s, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(s.Id, out var cur) || cur.V != s.RowVersion) return Task.FromResult(false);
            var copy = Clone(s); copy.RowVersion = cur.V + 1;
            _rows[s.Id] = (copy, cur.V + 1);
            s.RowVersion = cur.V + 1;
            return Task.FromResult(true);
        }
    }

    public Task<Shipment?> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        lock (_gate)
        {
            var due = _rows.Values.Select(r => r.S)
                .Where(s => (s.Status is not (ShipmentStatus.Delivered or ShipmentStatus.Cancelled or ShipmentStatus.NeedsAttention)
                             || (s.Status != ShipmentStatus.NeedsAttention && s.PendingNotifications != NotifyKind.None))   // finished shipments stay claimable until their emails are out
                            && s.NextAttemptAtUtc <= now
                            && (s.LeaseUntilUtc is null || s.LeaseUntilUtc <= now))
                .OrderBy(s => s.NextAttemptAtUtc).FirstOrDefault();
            if (due is null) return Task.FromResult<Shipment?>(null);
            var row = _rows[due.Id];
            var claimed = Clone(row.S); claimed.LeaseUntilUtc = now + lease; claimed.RowVersion = row.V + 1;
            _rows[due.Id] = (claimed, row.V + 1);
            return Task.FromResult<Shipment?>(Clone(claimed));
        }
    }

    public Task<bool> TryAddEventAsync(ShipmentEvent e, CancellationToken ct)
    {
        lock (_gate) { if (!_events.Add((e.ShipmentId, e.EventKey))) return Task.FromResult(false); _eventList.Add(e); return Task.FromResult(true); }
    }

    public Task<IReadOnlyList<Shipment>> ListSilentAsync(DateTimeOffset silentSince, int take, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Shipment>>(_rows.Values.Select(r => r.S)
                .Where(s => s.Status is ShipmentStatus.Booked or ShipmentStatus.InTransit or ShipmentStatus.Exception
                            && (s.LastEventAtUtc ?? s.CreatedAtUtc) <= silentSince).Take(take).Select(Clone).ToList());
    }

    private Shipment? Find(Func<Shipment, bool> p) { var r = _rows.Values.FirstOrDefault(x => p(x.S)); return r.S is null ? null : Wrap(r); }
    private static Shipment? Wrap((Shipment S, int V) r) { var c = Clone(r.S); c.RowVersion = r.V; return c; }
}
