using Microsoft.Extensions.Logging;

namespace GrowGift.Fulfillment.Shipping;

/// <summary>
/// Moves one shipment forward by at most one step. Every outward call is preceded by a persisted state change, and
/// an uncertain create is NEVER resent: it parks in <see cref="ShipmentStatus.CreateUnconfirmed"/> until a webhook
/// (matched on our reference) or a person resolves it. Nothing here logs addresses, bodies or secrets.
/// </summary>
public sealed class ShipmentProcessor(
    IShipmentStore store, IShippitClient shippit, IAddressSource addresses, IShipmentNotifier notifier,
    ShippitOptions opt, IClock clock, ILogger<ShipmentProcessor> log)
{
    public async Task StepAsync(Shipment s, CancellationToken ct)
    {
        var now = clock.UtcNow;
        switch (s.Status)
        {
            case ShipmentStatus.AwaitingAddress: await LoadAddressAsync(s, now, ct); break;
            case ShipmentStatus.ReadyToCreate: await CreateAsync(s, now, ct); break;
            case ShipmentStatus.CreateInFlight: await RecoverInFlightAsync(s, now, ct); break;
            case ShipmentStatus.CreateUnconfirmed: await WatchUnconfirmedAsync(s, now, ct); break;
            case ShipmentStatus.Created: await LabelAsync(s, now, ct); break;
            case ShipmentStatus.Labelled: await BookAsync(s, now, ct); break;
            case ShipmentStatus.Booked or ShipmentStatus.InTransit or ShipmentStatus.Exception: await ReconcileAsync(s, now, ct); break;
        }
        await NotifyAsync(s, ct);
    }

    // ---------- 0. address ----------
    private async Task LoadAddressAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        var (addr, failed) = await addresses.GetAsync(s.BuyerId, ct);
        if (failed) { await Backoff(s, now, "address.lookup_failed", ct, escalateAfter: 30); return; }
        if (addr is null) { s.Alert = "address_missing"; await Backoff(s, now, "address.missing", ct, escalateAfter: int.MaxValue, maxDelay: TimeSpan.FromHours(1)); return; }

        s.ToName = addr.FullName; s.ToLine1 = addr.Line1; s.ToLine2 = addr.Line2; s.ToCity = addr.City;
        s.ToState = addr.Region; s.ToPostcode = addr.PostalCode; s.ToCountry = addr.CountryCode; s.ToPhone = addr.Phone;
        if (!string.IsNullOrWhiteSpace(addr.Email)) s.BuyerEmail = addr.Email;

        var check = AddressValidator.Check(ToShippit(s));
        if (!check.Ok) { Fail(s, check.Code!, now, alert: "address_invalid"); await Save(s, ct); return; }
        s.Alert = null;
        s.Advance(ShipmentStatus.ReadyToCreate, now);
        await Save(s, ct);
    }

    // ---------- 1. create ----------
    private async Task CreateAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        var check = AddressValidator.Check(ToShippit(s));
        if (!check.Ok) { Fail(s, check.Code!, now, "address_invalid"); await Save(s, ct); return; }
        if (!opt.IsConfigured) { await Backoff(s, now, "shippit.not_configured", ct, escalateAfter: int.MaxValue); return; }

        // PERSIST BEFORE SENDING. If we crash after the request leaves, the row says "in flight" and recovery
        // treats it as unconfirmed rather than creating a second order.
        s.Status = ShipmentStatus.CreateInFlight;
        s.Attempts++;
        s.UpdatedAtUtc = now;
        s.LeaseUntilUtc = now.AddMinutes(5);
        if (!await store.SaveAsync(s, ct)) return;          // lost a race — another worker/webhook owns it now

        var req = new ShippitCreateRequest(s.Reference, s.BoxSku, ToShippit(s), opt.CourierType, opt.AuthorityToLeave, opt.ParcelFor(s.BoxSku));
        var r = await shippit.CreateOrderAsync(req, ct);
        now = clock.UtcNow;

        switch (r.Outcome)
        {
            case ShippitOutcome.Ok:
                s.TrackingNumber = r.Value!.TrackingNumber;
                s.CourierName = r.Value.CourierName ?? s.CourierName;
                s.TrackingUrl = r.Value.TrackingUrl ?? s.TrackingUrl;
                s.ShippitState = r.Value.State;
                s.Advance(ShipmentStatus.Created, now);
                s.NextAttemptAtUtc = now;               // label immediately
                break;
            case ShippitOutcome.RetrySafe:               // e.g. 429: definitely not processed
                s.Status = ShipmentStatus.ReadyToCreate;
                s.LastError = r.Code;
                s.NextAttemptAtUtc = now + (r.RetryAfter ?? Delay(s.Attempts));
                if (s.Attempts >= opt.MaxTransientAttempts) Fail(s, r.Code ?? "create.retries_exhausted", now, "create_failed");
                break;
            case ShippitOutcome.Permanent:
                Fail(s, r.Code ?? "create.rejected", now, "create_rejected");
                break;
            default:                                     // Unknown / NotFound / StillProcessing — we cannot tell if Shippit created it
                s.Status = ShipmentStatus.CreateUnconfirmed;
                s.Alert = "create_unconfirmed";
                s.LastError = r.Code ?? "create.unconfirmed";
                s.NextAttemptAtUtc = now + opt.UnconfirmedAlertAfter;
                log.LogWarning("Shipment {ShipmentId}: create outcome unknown ({Code}); not resending", s.Id, s.LastError);
                break;
        }
        s.LeaseUntilUtc = null;
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    /// <summary>A crash/restart left the row "in flight" with an expired lease. We can't know what happened → unconfirmed.</summary>
    private async Task RecoverInFlightAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        // Only reachable once the creating worker's lease has expired (the store won't hand out a live one).
        s.Status = ShipmentStatus.CreateUnconfirmed;
        s.Alert = "create_unconfirmed";
        s.LastError = "create.interrupted";
        s.NextAttemptAtUtc = now + opt.UnconfirmedAlertAfter;
        s.LeaseUntilUtc = null; s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    private async Task WatchUnconfirmedAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        // Nothing automatic can safely resolve this: the order endpoint has no documented lookup by reference.
        // A webhook carrying our retailer_reference adopts the tracking number; otherwise a person decides.
        s.Alert = "create_unconfirmed";
        s.NextAttemptAtUtc = now + opt.UnconfirmedAlertAfter;
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    // ---------- 2. label ----------
    private async Task LabelAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        if (s.TrackingNumber is null) { Fail(s, "label.no_tracking_number", now, "state_corrupt"); await Save(s, ct); return; }
        s.Attempts++;
        var r = await shippit.GetLabelAsync(s.TrackingNumber, ct);
        now = clock.UtcNow;
        switch (r.Outcome)
        {
            case ShippitOutcome.Ok:
                s.LabelUrl = r.Value!.LabelUrl;
                s.LabelFetchedAtUtc = now;
                s.CourierName = r.Value.CourierName ?? s.CourierName;
                s.ManifestNumber = r.Value.ManifestNumber ?? s.ManifestNumber;
                s.ManifestId = r.Value.ManifestId ?? s.ManifestId;
                // Some service types are booked when the order is created; then there is nothing left to book.
                s.Advance(s.ManifestNumber is not null ? ShipmentStatus.Booked : ShipmentStatus.Labelled, now);
                if (s.Status == ShipmentStatus.Booked) { s.Reach(NotifyKind.Booked); s.LastEventAtUtc = now; }
                break;
            case ShippitOutcome.StillProcessing:
            case ShippitOutcome.RetrySafe:
                s.LastError = r.Code;
                s.NextAttemptAtUtc = now + (r.RetryAfter ?? Delay(s.Attempts));
                if (s.Attempts >= opt.LabelMaxAttempts) Fail(s, r.Code ?? "label.retries_exhausted", now, "label_failed");
                break;
            default:
                Fail(s, r.Code ?? "label.failed", now, "label_failed");
                break;
        }
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    // ---------- 3. book ----------
    private async Task BookAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        if (!opt.AutoBook) { s.NextAttemptAtUtc = now.AddHours(1); await Save(s, ct); return; }  // manual booking mode
        if (s.TrackingNumber is null) { Fail(s, "book.no_tracking_number", now, "state_corrupt"); await Save(s, ct); return; }
        s.Attempts++;
        var r = await shippit.BookAsync(s.TrackingNumber, ct);
        now = clock.UtcNow;
        switch (r.Outcome)
        {
            case ShippitOutcome.Ok:
                s.ManifestNumber = r.Value!.ManifestNumber ?? s.ManifestNumber;
                s.ManifestId = r.Value.ManifestId ?? s.ManifestId;
                s.Advance(ShipmentStatus.Booked, now);
                s.Reach(NotifyKind.Booked); s.LastEventAtUtc = now;
                break;
            case ShippitOutcome.RetrySafe:
                s.LastError = r.Code; s.NextAttemptAtUtc = now + (r.RetryAfter ?? Delay(s.Attempts));
                if (s.Attempts >= opt.BookMaxAttempts) Fail(s, r.Code ?? "book.retries_exhausted", now, "book_failed");
                break;
            default:
                // Unknown (timeout/5xx) or "not available to book" (maybe already booked): verify with a read before deciding.
                await VerifyBookedAsync(s, r.Code ?? "book.unconfirmed", now, ct);
                return;
        }
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    private async Task VerifyBookedAsync(Shipment s, string code, DateTimeOffset now, CancellationToken ct)
    {
        var check = await shippit.GetLabelAsync(s.TrackingNumber!, ct);
        now = clock.UtcNow;
        if (check.IsOk && check.Value!.ManifestNumber is { } m)
        {
            s.ManifestNumber = m; s.ManifestId = check.Value.ManifestId ?? s.ManifestId;
            s.Advance(ShipmentStatus.Booked, now); s.Reach(NotifyKind.Booked); s.LastEventAtUtc = now;
        }
        else
        {
            s.LastError = code;
            s.NextAttemptAtUtc = now + Delay(s.Attempts);
            if (s.Attempts >= opt.BookMaxAttempts) Fail(s, code, now, "book_failed");
        }
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    // ---------- 4. fallback reconciliation (webhooks are primary) ----------
    private async Task ReconcileAsync(Shipment s, DateTimeOffset now, CancellationToken ct)
    {
        if (s.TrackingNumber is null) return;
        var silentFor = now - (s.LastEventAtUtc ?? s.LastApiOkAtUtc ?? s.CreatedAtUtc);
        if (silentFor < opt.ReconcileAfterSilence) { s.NextAttemptAtUtc = now + opt.ReconcileInterval; await Save(s, ct); return; }

        var r = await shippit.GetTrackingAsync(s.TrackingNumber, ct);
        now = clock.UtcNow;
        if (r.IsOk && r.Value!.State is { } state)
        {
            var key = $"rec|{ShippitStateMap.Normalise(state)}|{r.Value.History.Count}";
            var occurred = r.Value.History.LastOrDefault()?.Time ?? now;
            await ShipmentEventApplier.ApplyAsync(s, store, state, key, "reconcile", occurred, now, ct);
            s.LastReconciledAtUtc = now;
        }
        s.NextAttemptAtUtc = now + (r.IsOk ? opt.ReconcileInterval : Delay(Math.Max(1, ++s.Attempts)));
        s.UpdatedAtUtc = now;
        await Save(s, ct);
    }

    // ---------- notifications ----------
    private async Task NotifyAsync(Shipment s, CancellationToken ct)
    {
        foreach (var kind in new[] { NotifyKind.Booked, NotifyKind.InTransit, NotifyKind.Delivered, NotifyKind.Exception })
        {
            if ((s.PendingNotifications & kind) == 0) continue;
            bool ok;
            try { ok = await notifier.SendAsync(s, kind, $"shipment-{s.Id:N}-{kind}", ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Shipment {ShipmentId}: notify {Kind} failed ({Type})", s.Id, kind, e.GetType().Name); ok = false; }
            if (!ok)                                         // retried shortly; shipping is never blocked by email
            {
                s.NextAttemptAtUtc = clock.UtcNow.AddMinutes(1);
                await store.SaveAsync(s, ct);
                return;
            }
            s.NotifiedMask |= (int)kind;
            await store.SaveAsync(s, ct);
        }
    }

    // ---------- helpers ----------
    internal static ShippitAddress ToShippit(Shipment s) =>
        new(s.ToName ?? "", s.ToLine1 ?? "", s.ToLine2, s.ToCity ?? "", s.ToState ?? "", s.ToPostcode ?? "", s.ToCountry ?? "AU", s.ToPhone, s.BuyerEmail);

    private TimeSpan Delay(int attempt)
    {
        var secs = opt.BaseBackoff.TotalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 12));
        return TimeSpan.FromSeconds(Math.Min(secs, opt.MaxBackoff.TotalSeconds));
    }

    private async Task Backoff(Shipment s, DateTimeOffset now, string code, CancellationToken ct, int escalateAfter, TimeSpan? maxDelay = null)
    {
        s.Attempts++;
        s.LastError = code;
        var d = Delay(s.Attempts);
        s.NextAttemptAtUtc = now + (maxDelay is { } m && d > m ? m : d);
        s.UpdatedAtUtc = now;
        if (s.Attempts >= escalateAfter) Fail(s, code, now, "retries_exhausted");
        await Save(s, ct);
    }

    private static void Fail(Shipment s, string code, DateTimeOffset now, string alert)
    {
        s.Status = ShipmentStatus.NeedsAttention;
        s.LastError = code;
        s.Alert = alert;
        s.LeaseUntilUtc = null;
        s.UpdatedAtUtc = now;
    }

    private async Task Save(Shipment s, CancellationToken ct)
    {
        if (s.Status != ShipmentStatus.CreateInFlight) s.LeaseUntilUtc = null;   // release the worker lease
        if (!await store.SaveAsync(s, ct))
            log.LogInformation("Shipment {ShipmentId}: concurrent update; step skipped and will be re-evaluated", s.Id);
    }
}
