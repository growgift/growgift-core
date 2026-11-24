using GrowGift.Fulfillment.Shipping;
using Xunit;

namespace GrowGift.Fulfillment.Tests;

public class WebhookTests
{
    private static async Task<(Rig r, Shipment s)> Booked()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync(); await r.RunAsync();
        return (r, (await r.Store.GetAsync(s.Id, default))!);
    }

    [Fact] public async Task Missing_or_wrong_token_is_rejected_and_changes_nothing()
    {
        var (r, s) = await Booked();
        Assert.Equal(WebhookResult.Unauthorized, await r.Webhooks.HandleAsync(null, Rig.Hook("TN1", "completed"), default));
        Assert.Equal(WebhookResult.Unauthorized, await r.Webhooks.HandleAsync("nope", Rig.Hook("TN1", "completed"), default));
        Assert.Equal(ShipmentStatus.Booked, (await r.Store.GetAsync(s.Id, default))!.Status);
    }
    [Fact] public async Task No_configured_token_means_everything_is_refused()
    {
        var (r, _) = await Booked(); r.Options.WebhookToken = null;
        Assert.Equal(WebhookResult.NotConfigured, await r.Webhooks.HandleAsync("anything", Rig.Hook("TN1", "completed"), default));
        Assert.Equal(WebhookResult.NotConfigured, await r.Webhooks.HandleAsync(null, Rig.Hook("TN1", "completed"), default));
    }
    [Fact] public async Task Valid_event_advances_status()
    {
        var (r, s) = await Booked();
        Assert.Equal(WebhookResult.Accepted, await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "in_transit"), default));
        Assert.Equal(ShipmentStatus.InTransit, (await r.Store.GetAsync(s.Id, default))!.Status);
    }
    [Fact] public async Task Replayed_webhook_is_a_noop()
    {
        var (r, s) = await Booked(); var body = Rig.Hook("TN1", "in_transit");
        Assert.Equal(WebhookResult.Accepted, await r.Webhooks.HandleAsync("hook-token", body, default));
        for (var i = 0; i < 4; i++) Assert.Equal(WebhookResult.Duplicate, await r.Webhooks.HandleAsync("hook-token", body, default));
        Assert.Equal(1, r.Store.AllEvents.Count(e => e.ShippitState == "in_transit"));
        await r.TickAsync();
        Assert.Equal(1, r.Notifier.Sent.Count(x => x.Kind == NotifyKind.InTransit));
    }
    [Fact] public async Task Out_of_order_events_do_not_regress_status()
    {
        var (r, s) = await Booked();
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "completed", time: "2026-10-28T09:00:00Z"), default);
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "in_transit", time: "2026-10-28T01:00:00Z"), default);   // arrives late
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "order_placed", time: "2026-10-27T05:00:00Z"), default);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.Delivered, d.Status);
        Assert.Equal(3, r.Store.AllEvents.Count);   // still recorded for audit
        Assert.True(r.Store.AllEvents.Count(e => e.Outcome == "ignored_terminal") >= 2);
    }
    [Fact] public async Task Exception_then_recovery()
    {
        var (r, s) = await Booked();
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "in_transit", time: "2026-10-27T03:00:00Z"), default);
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "delivery_failed", time: "2026-10-27T04:00:00Z"), default);
        Assert.Equal(ShipmentStatus.Exception, (await r.Store.GetAsync(s.Id, default))!.Status);
        Assert.Equal("carrier_exception", (await r.Store.GetAsync(s.Id, default))!.Alert);
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook("TN1", "with_driver", time: "2026-10-27T05:00:00Z"), default);
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal(ShipmentStatus.InTransit, d.Status); Assert.Null(d.Alert);
    }
    [Fact] public async Task Unknown_shipment_is_acknowledged_but_changes_nothing()
    {
        var (r, _) = await Booked(); var before = r.Store.AllEvents.Count;
        Assert.Equal(WebhookResult.Unmatched, await r.Webhooks.HandleAsync("hook-token", Rig.Hook("OTHER", "completed", "GG-NOPE"), default));
        Assert.Equal(before, r.Store.AllEvents.Count);
    }
    [Fact] public async Task Tracking_number_mismatch_cannot_hijack_a_shipment()
    {
        var (r, s) = await Booked();       // has TN1, reference GG-…
        Assert.Equal(WebhookResult.Unmatched, await r.Webhooks.HandleAsync("hook-token", Rig.Hook("EVIL9", "completed", s.Reference), default));
        var d = (await r.Store.GetAsync(s.Id, default))!;
        Assert.Equal("TN1", d.TrackingNumber); Assert.Equal(ShipmentStatus.Booked, d.Status);
    }
    [Fact] public async Task Malformed_and_oversize_bodies_are_rejected()
    {
        var (r, _) = await Booked();
        Assert.Equal(WebhookResult.BadRequest, await r.Webhooks.HandleAsync("hook-token", System.Text.Encoding.UTF8.GetBytes("{not json"), default));
        Assert.Equal(WebhookResult.BadRequest, await r.Webhooks.HandleAsync("hook-token", System.Text.Encoding.UTF8.GetBytes("[]"), default));
        Assert.Equal(WebhookResult.BadRequest, await r.Webhooks.HandleAsync("hook-token", System.Text.Encoding.UTF8.GetBytes("{\"current_state\":\"x\"}"), default));
        Assert.Equal(WebhookResult.TooLarge, await r.Webhooks.HandleAsync("hook-token", new byte[ShippitWebhookHandler.MaxBodyBytes + 1], default));
    }
    [Fact] public async Task Webhook_that_arrives_before_the_label_step_still_applies()
    {
        var r = new Rig(); var s = await r.NewShipmentAsync(); await r.TickAsync(); await r.TickAsync();   // → Created
        var cur = (await r.Store.GetAsync(s.Id, default))!; Assert.Equal(ShipmentStatus.Created, cur.Status);
        await r.Webhooks.HandleAsync("hook-token", Rig.Hook(cur.TrackingNumber!, "in_transit"), default);
        Assert.Equal(ShipmentStatus.InTransit, (await r.Store.GetAsync(s.Id, default))!.Status);
    }
    [Fact] public void Token_comparison_works() { Assert.True(ShippitWebhookHandler.TokenMatches("a", "a")); Assert.False(ShippitWebhookHandler.TokenMatches("a", "b")); Assert.False(ShippitWebhookHandler.TokenMatches(null, "b")); }
}
