using GrowGift.Fulfillment.Shipping;
using Xunit;

namespace GrowGift.Fulfillment.Tests;

public class LifecycleTests
{
    // ---- intake ----
    [Fact] public async Task Paid_physical_order_creates_one_shipment()
    {
        var r = new Rig(); var req = Rig.Paid();
        var a = await r.Intake.RegisterAsync(req, default);
        Assert.Equal(IntakeStatus.Created, a.Status); Assert.Equal(1, r.Store.Count);
        Assert.Equal(0, r.Shippit.Creates);   // intake never calls Shippit
    }
    [Fact] public async Task Redelivered_paid_event_does_not_create_a_second_shipment()
    {
        var r = new Rig(); var req = Rig.Paid();
        var a = await r.Intake.RegisterAsync(req, default); var b = await r.Intake.RegisterAsync(req, default);
        Assert.Equal(IntakeStatus.AlreadyExists, b.Status); Assert.Equal(a.ShipmentId, b.ShipmentId); Assert.Equal(1, r.Store.Count);
    }
    [Fact] public async Task Parallel_duplicate_events_still_make_one_shipment()
    {
        var r = new Rig(); var req = Rig.Paid();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => r.Intake.RegisterAsync(req, default))));
        Assert.Equal(1, r.Store.Count);
    }
    [Fact] public async Task Digital_gift_never_ships()
    {
        var r = new Rig();
        var res = await r.Intake.RegisterAsync(Rig.Paid() with { DeliveryMethod = "Email" }, default);
        Assert.Equal(IntakeStatus.NotPhysical, res.Status); Assert.Equal(0, r.Store.Count);
    }
    [Fact] public async Task Unpaid_order_never_ships()
    {
        var r = new Rig();
        var res = await r.Intake.RegisterAsync(Rig.Paid() with { PaymentConfirmed = false }, default);
        Assert.Equal(IntakeStatus.PaymentNotConfirmed, res.Status); Assert.Equal(0, r.Store.Count);
    }

    // ---- happy path ----
    [Fact] public async Task Happy_path_create_label_book_exactly_once()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Ok(new("https://l/x.pdf", "Courier", null, null, "processing")));
        await r.RunAsync();
        var done = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.Booked, done.Status);
        Assert.Equal(1, r.Shippit.Creates); Assert.Equal(1, r.Shippit.Labels); Assert.Equal(1, r.Shippit.Books);
        Assert.Equal("TN1", done.TrackingNumber); Assert.Equal("https://l/x.pdf", done.LabelUrl); Assert.Equal("M1", done.ManifestNumber);
        Assert.Equal(s.Reference, r.Shippit.LastCreate!.Reference);
    }
    [Fact] public async Task Rerunning_a_finished_shipment_never_recreates_it()
    {
        var r = new Rig(); await r.NewShipmentAsync(); await r.RunAsync();
        for (var i = 0; i < 5; i++) { r.Clock.Advance(TimeSpan.FromHours(1)); await r.TickAsync(); }
        Assert.Equal(1, r.Shippit.Creates); Assert.Equal(1, r.Shippit.Books);
    }
    [Fact] public async Task Label_still_processing_is_retried_then_succeeds()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        for (var i = 0; i < 3; i++) r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Fail(ShippitOutcome.StillProcessing, "label_failed_order_processing", 422));
        await r.RunAsync();
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status);
        Assert.Equal(4, r.Shippit.Labels); Assert.Equal(1, r.Shippit.Creates);
    }
    [Fact] public async Task Label_retries_are_bounded_then_flagged()
    {
        var r = new Rig(); r.Options.LabelMaxAttempts = 3; var s = await r.NewShipmentAsync();
        for (var i = 0; i < 10; i++) r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Fail(ShippitOutcome.StillProcessing, "label_failed_still_generating", 422));
        await r.RunAsync();
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.NeedsAttention, d.Status); Assert.Equal("label_failed", d.Alert); Assert.Equal(3, r.Shippit.Labels);
        Assert.Equal(0, r.Shippit.Books);
    }
    [Fact] public async Task Order_booked_at_creation_skips_booking()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Ok(new("https://l/x.pdf", "Courier", "M9", "99", "order_placed")));
        await r.RunAsync();
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status); Assert.Equal(0, r.Shippit.Books);
    }
    [Fact] public async Task Manual_booking_mode_stops_after_label()
    {
        var r = new Rig(); r.Options.AutoBook = false; var s = await r.NewShipmentAsync(); await r.RunAsync(10, advance: false);
        Assert.Equal(ShipmentStatus.Labelled, (await r.Store.GetAsync(s.Id, default))!.Status); Assert.Equal(0, r.Shippit.Books);
    }
    [Fact] public async Task Book_timeout_is_verified_by_a_read_not_blindly_repeated()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.BookReplies.Enqueue(ShippitResult<ShippitBooked>.Fail(ShippitOutcome.Unknown, "transport.timeout"));
        r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Ok(new("https://l/x.pdf", "C", null, null, "x")));          // initial label
        r.Shippit.LabelReplies.Enqueue(ShippitResult<ShippitLabel>.Ok(new("https://l/x.pdf", "C", "M5", "55", "booked")));   // verify read shows it IS booked
        await r.RunAsync();
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.Booked, d.Status); Assert.Equal("M5", d.ManifestNumber); Assert.Equal(1, r.Shippit.Books);
    }

    // ---- create safety ----
    [Fact] public async Task Create_timeout_is_never_resent()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.CreateReplies.Enqueue(ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Unknown, "transport.timeout"));
        await r.RunAsync(40);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.CreateUnconfirmed, d.Status); Assert.Equal(1, r.Shippit.Creates);
        Assert.Equal("create_unconfirmed", d.Alert);
        Assert.Null(d.TrackingNumber);
    }
    [Fact] public async Task Server_error_on_create_is_also_unconfirmed()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.CreateReplies.Enqueue(ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Unknown, "server_500", 500));
        await r.RunAsync(40);
        Assert.Equal(ShipmentStatus.CreateUnconfirmed, (await r.Store.GetAsync(s.Id, default))!.Status); Assert.Equal(1, r.Shippit.Creates);
    }
    [Fact] public async Task Crash_after_persisting_in_flight_is_not_recreated()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        // simulate: worker saved CreateInFlight, sent the request, then the process died
        s.Status = ShipmentStatus.CreateInFlight; s.LeaseUntilUtc = r.Clock.UtcNow.AddMinutes(5); s.NextAttemptAtUtc = r.Clock.UtcNow;
        Assert.True(await r.Store.SaveAsync(s, default));
        r.Clock.Advance(TimeSpan.FromMinutes(10));
        await r.RunAsync(20);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.CreateUnconfirmed, d.Status); Assert.Equal(0, r.Shippit.Creates);
    }
    [Fact] public async Task A_live_lease_is_respected()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        s.Status = ShipmentStatus.CreateInFlight; s.LeaseUntilUtc = r.Clock.UtcNow.AddMinutes(5);
        await r.Store.SaveAsync(s, default);
        Assert.False(await r.TickAsync());       // not due while another worker holds the lease
    }
    [Fact] public async Task Webhook_with_our_reference_resolves_an_unconfirmed_create()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.CreateReplies.Enqueue(ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Unknown, "transport.timeout"));
        await r.RunAsync(5, advance: false);
        var res = await r.Webhooks.HandleAsync("hook-token", Rig.Hook("SHP123", "order_placed", s.Reference), default);
        Assert.Equal(WebhookResult.Accepted, res);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal("SHP123", d.TrackingNumber); Assert.True(d.Status >= ShipmentStatus.Created); Assert.Null(d.Alert);
        await r.RunAsync();
        Assert.Equal(1, r.Shippit.Creates);      // adopted, not re-created
    }
    [Fact] public async Task Rate_limit_on_create_is_safe_to_retry()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.CreateReplies.Enqueue(ShippitResult<ShippitCreated>.Fail(ShippitOutcome.RetrySafe, "rate_limited", 429, TimeSpan.FromSeconds(5)));
        await r.RunAsync();
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status); Assert.Equal(2, r.Shippit.Creates);
    }
    [Fact] public async Task Rejected_create_needs_attention_with_a_sanitised_code()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync();
        r.Shippit.CreateReplies.Enqueue(ShippitResult<ShippitCreated>.Fail(ShippitOutcome.Permanent, "invalid", 422));
        await r.RunAsync();
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.NeedsAttention, d.Status); Assert.Equal("invalid", d.LastError); Assert.Equal(1, r.Shippit.Creates);
    }
    [Fact] public async Task Missing_address_waits_and_flags_support_without_calling_shippit()
    {
        var r = new Rig(); r.Addresses.Address = null; var s = await r.NewShipmentAsync();
        await r.RunAsync(6);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.AwaitingAddress, d.Status); Assert.Equal("address_missing", d.Alert); Assert.Equal(0, r.Shippit.Creates);
        // customer adds an address → next pass proceeds
        r.Addresses.Address = new("A B", "1 St", null, "Sydney", "NSW", "2000", "AU", null, "a@b.c");
        await r.RunAsync();
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status);
    }
    [Fact] public async Task Invalid_address_is_stopped_locally()
    {
        var r = new Rig(); r.Addresses.Address = new("A B", "1 St", null, "Sydney", "XX", "2000", "AU", null, "a@b.c");
        var s = await r.NewShipmentAsync(); await r.RunAsync();
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.NeedsAttention, d.Status); Assert.Equal("address.state_invalid", d.LastError); Assert.Equal(0, r.Shippit.Creates);
    }
    [Fact] public async Task Missing_secret_makes_no_calls()
    {
        var r = new Rig(); r.Options.ApiSecret = null; await r.NewShipmentAsync(); await r.RunAsync(6);
        Assert.Equal(0, r.Shippit.Creates);
    }

    // ---- fallback reconciliation ----
    [Fact] public async Task Reconcile_only_polls_shipments_that_went_quiet()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync(); await r.RunAsync();
        r.Clock.Advance(TimeSpan.FromMinutes(31)); await r.TickAsync();
        Assert.Equal(0, r.Shippit.Trackings);                    // recent activity → no polling
        r.Clock.Advance(TimeSpan.FromHours(7)); await r.TickAsync();
        Assert.Equal(1, r.Shippit.Trackings);
        Assert.Equal(ShipmentStatus.InTransit, (await r.Store.GetAsync(s.Id, default))!.Status);
    }
    [Fact] public async Task Repeated_identical_reconcile_does_not_duplicate_events_or_emails()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync(); await r.RunAsync();
        for (var i = 0; i < 4; i++) { r.Clock.Advance(TimeSpan.FromHours(7)); await r.TickAsync(); }
        Assert.Equal(1, r.Store.AllEvents.Count(e => e.ShipmentId == s.Id && e.ShippitState == "in_transit"));
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.InTransit));
    }

    // ---- notifications ----
    [Fact] public async Task Each_milestone_emails_once()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync(); await r.RunAsync();
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "in_transit"), default);
        await r.TickAsync();
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "completed", time: "2026-10-28T02:00:00Z"), default);
        for (var i = 0; i < 3; i++) { r.Clock.Advance(TimeSpan.FromMinutes(40)); await r.TickAsync(); }
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.Booked));
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.InTransit));
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.Delivered));
        Assert.Equal(r.Notifier.Sent.Count, r.Notifier.Sent.Select(x => x.Key).Distinct().Count());   // stable idempotency keys
    }
    [Fact] public async Task Email_failure_never_blocks_shipping_and_is_retried()
    {
        var r = new Rig(); r.Notifier.Fail = true; var s = await r.NewShipmentAsync(); await r.RunAsync(10, advance: false);
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status);
        Assert.Empty(r.Notifier.Sent);
        r.Notifier.Fail = false; r.Clock.Advance(TimeSpan.FromMinutes(2)); await r.TickAsync();
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.Booked));
    }
}
