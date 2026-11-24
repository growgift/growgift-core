using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GrowGift.Fulfillment.Shipping;
using Xunit;

namespace GrowGift.Fulfillment.Tests;

public class ViewAndContractTests
{
    private static string Repo([CallerFilePath] string f = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, "..", "..", ".."));   // …/backend/tests/X → repo root
    private static string Src(string rel) => File.ReadAllText(Path.Combine(Repo(), "backend", "src", rel.Replace('/', Path.DirectorySeparatorChar)));

    // ---- customer view leaks nothing it shouldn't ----
    private static Shipment Full() => new()
    {
        Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), BuyerId = Guid.NewGuid(), BuyerEmail = "buyer@example.com", Reference = "GG-X",
        ToName = "Sam Secret", ToLine1 = "99 Hidden Lane", ToLine2 = "Flat 2", ToCity = "Sydney", ToState = "NSW", ToPostcode = "2000", ToPhone = "0411222333",
        TrackingNumber = "PP1", CourierName = "Courier", TrackingUrl = "https://track.example/PP1", LabelUrl = "https://labels.example/signed?sig=abc",
        ManifestNumber = "M1", LastError = "rejected_422", Alert = "create_rejected", Status = ShipmentStatus.InTransit
    };

    [Fact] public void Customer_view_has_no_street_address_phone_email_label_or_internal_error()
    {
        var json = JsonSerializer.Serialize(ShipmentView.ForCustomer(Full(), new[] { new ShipmentEvent { ShippitState = "in_transit", Outcome = "applied", OccurredAtUtc = DateTimeOffset.UtcNow } }));
        foreach (var banned in new[] { "Hidden Lane", "Flat 2", "Sam Secret", "0411222333", "buyer@example.com", "labels.example", "rejected_422", "create_rejected", "M1", "GG-X" })
            Assert.DoesNotContain(banned, json);
        Assert.Contains("PP1", json); Assert.Contains("Sydney", json);
    }
    [Fact] public void Admin_view_has_no_street_address_or_label_url()
    {
        var json = JsonSerializer.Serialize(ShipmentView.ForAdmin(Full()));
        foreach (var banned in new[] { "Hidden Lane", "0411222333", "buyer@example.com", "labels.example" }) Assert.DoesNotContain(banned, json);
        Assert.Contains("rejected_422", json);
    }
    [Fact] public void Every_status_has_customer_wording() { foreach (var st in Enum.GetValues<ShipmentStatus>()) Assert.True(ShipmentView.CustomerStatus(st).Length > 3); }

    [Fact] public void Retry_never_recreates_an_order_that_already_has_a_tracking_number()
    {
        var s = Full(); s.Status = ShipmentStatus.NeedsAttention; s.ManifestNumber = null; s.LabelUrl = null;
        Assert.Equal(ShipmentStatus.Created, ShipmentView.ResumeStep(s));
        s.LabelUrl = "x"; Assert.Equal(ShipmentStatus.Labelled, ShipmentView.ResumeStep(s));
        s.ManifestNumber = "M"; Assert.Equal(ShipmentStatus.Booked, ShipmentView.ResumeStep(s));
    }
    [Fact] public void Retry_without_tracking_resumes_at_the_failed_early_step()
    {
        var s = new Shipment { Status = ShipmentStatus.NeedsAttention, LastError = "address.state_invalid", ToLine1 = "1 St" };
        Assert.Equal(ShipmentStatus.AwaitingAddress, ShipmentView.ResumeStep(s));
        s.LastError = "rejected_422"; Assert.Equal(ShipmentStatus.ReadyToCreate, ShipmentView.ResumeStep(s));
        s.ToLine1 = null; Assert.Equal(ShipmentStatus.AwaitingAddress, ShipmentView.ResumeStep(s));
    }
    [Fact] public void Label_urls_are_refreshed_before_the_seven_day_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(ShipmentView.LabelIsStale(new Shipment(), now));
        Assert.False(ShipmentView.LabelIsStale(new Shipment { LabelUrl = "x", LabelFetchedAtUtc = now.AddDays(-2) }, now));
        Assert.True(ShipmentView.LabelIsStale(new Shipment { LabelUrl = "x", LabelFetchedAtUtc = now.AddDays(-6.5) }, now));
    }

    // ---- SQL / entity / index contracts ----
    [Fact] public void Sql_has_a_column_for_every_entity_property()
    {
        string TableSql(string table) { var i = ShippingSchema.Sql.IndexOf($"CREATE TABLE IF NOT EXISTS fulfillment.{table}", StringComparison.Ordinal); var j = ShippingSchema.Sql.IndexOf(");", i, StringComparison.Ordinal); return ShippingSchema.Sql[i..j]; }
        var computed = new[] { "IsTerminal", "PendingNotifications" };
        foreach (var (type, table) in new[] { (typeof(Shipment), "shipments"), (typeof(ShipmentEvent), "shipment_events") })
        {
            var sql = TableSql(table);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && !computed.Contains(p.Name)))
                Assert.Contains($"\"{p.Name}\"", sql);
        }
    }
    [Fact] public void Sql_has_the_unique_indexes_that_make_duplicates_impossible()
    {
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_shipments_order", ShippingSchema.Sql);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_shipments_reference", ShippingSchema.Sql);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_shipment_events_key", ShippingSchema.Sql);
        Assert.Contains("(\"ShipmentId\", \"EventKey\")", ShippingSchema.Sql);
    }
    [Fact] public void Published_sql_file_matches_the_code()
    {
        var file = File.ReadAllText(Path.Combine(Repo(), "infra", "sql", "fulfillment_shipping.sql"));
        Assert.Contains(ShippingSchema.Sql.Trim(), file.Replace("\r\n", "\n"));
    }
    [Fact] public void Hardcoded_status_numbers_in_sql_match_the_enum()
    {
        Assert.Equal(8, (int)ShipmentStatus.Delivered); Assert.Equal(9, (int)ShipmentStatus.Cancelled); Assert.Equal(11, (int)ShipmentStatus.NeedsAttention);
        Assert.Contains("NOT IN (8, 9, 11)", ShippingSchema.Sql);
        Assert.Contains("NOT IN (8, 9, 11)", Src("Services/Fulfillment/GrowGift.Fulfillment/Shipping/Ef/EfShipmentStore.cs"));
        Assert.Contains("<> 11", Src("Services/Fulfillment/GrowGift.Fulfillment/Shipping/Ef/EfShipmentStore.cs"));
    }
    [Fact] public void Notify_flag_bits_match_the_claim_query_mask()
    {
        Assert.Equal(1, (int)NotifyKind.Booked); Assert.Equal(2, (int)NotifyKind.InTransit); Assert.Equal(4, (int)NotifyKind.Delivered); Assert.Equal(8, (int)NotifyKind.Exception);
    }

    // ---- configuration & secret hygiene (static checks on the repo) ----
    [Fact] public void Shipped_appsettings_are_staging_and_carry_no_secret()
    {
        var text = Src("Services/Fulfillment/GrowGift.Fulfillment/appsettings.json");
        using var doc = JsonDocument.Parse(text);
        var sh = doc.RootElement.GetProperty("Shippit");
        var o = new ShippitOptions { Environment = sh.GetProperty("Environment").GetString()!, BaseUrl = sh.GetProperty("BaseUrl").GetString()! };
        ShippitGuard.EnsureStaging(o);
        Assert.False(sh.TryGetProperty("ApiSecret", out _)); Assert.False(sh.TryGetProperty("WebhookToken", out _));
        Assert.DoesNotContain("Bearer", text);
    }
    [Fact] public void No_source_file_names_the_production_host_or_logs_the_secret()
    {
        var dir = Path.Combine(Repo(), "backend", "src", "Services", "Fulfillment");
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(dir, "appsettings*.json", SearchOption.AllDirectories)))
        {
            var t = File.ReadAllText(f);
            Assert.DoesNotContain("https://app.shippit.com", t);
            Assert.DoesNotContain("api.shippit.com", t);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(t, @"Log\w*\([^;]*\.ApiSecret"));
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(t, @"Log\w*\([^;]*(ToLine1|ToLine2|ToPostcode|ToPhone|\.Body)"));
        }
    }
    [Fact] public void Fulfillment_program_runs_the_staging_guard_and_secures_endpoints()
    {
        var p = Src("Services/Fulfillment/GrowGift.Fulfillment/Program.cs");
        Assert.Contains("ShippitGuard.EnsureStaging(shippitOptions)", p);
        Assert.Contains("SHIPPIT_API_SECRET", p);
        Assert.DoesNotContain("MapGet(\"/api/v1/shipments/{orderId:guid}\"", p);     // the old unauthenticated endpoint is gone
        var e = Src("Services/Fulfillment/GrowGift.Fulfillment/Api/ShippingEndpoints.cs");
        Assert.Contains("RequireAuthorization(\"Authenticated\")", e);
        Assert.Contains("RequireAuthorization(\"ShippingAdmin\")", e);
        Assert.Contains("if (s is null || s.BuyerId != uid) return Results.NotFound();", e);
        var labelAt = e.IndexOf("/label", StringComparison.Ordinal); var adminAt = e.IndexOf("var admin", StringComparison.Ordinal);
        Assert.True(labelAt > adminAt);                                               // label lives only under the admin group
    }
    [Fact] public void Gateway_never_forwards_internal_paths_and_routes_shipping()
    {
        Assert.Contains("\"/internal/\"", Src("Gateway/GrowGift.Gateway/Program.cs"));
        Assert.Contains("/api/v1/shipping/{**rest}", Src("Gateway/GrowGift.Gateway/appsettings.json"));
    }
    [Fact] public void Identity_address_endpoint_requires_the_internal_key_and_fails_closed()
    {
        var t = Src("Services/Identity/GrowGift.Identity/Api/IdentityEndpoints.cs");
        var i = t.IndexOf("/internal/v1/users/{userId:guid}/shipping-address", StringComparison.Ordinal);
        Assert.True(i > 0);
        var body = t[i..Math.Min(t.Length, i + 1800)];
        Assert.Contains("X-Internal-Key", body); Assert.Contains("FixedTimeEquals", body); Assert.Contains("503", body);
    }
    [Fact] public void Gifting_only_sends_shipping_inputs_for_paid_physical_orders()
    {
        var t = Src("Services/Gifting/GrowGift.Gifting/Infrastructure/Consumers/GiftOrderPaidConsumer.cs");
        var guard = t.IndexOf("order.DeliveryMethod == GrowGift.Gifting.Domain.DeliveryMethod.Physical", StringComparison.Ordinal);
        var send = t.IndexOf("paymentConfirmed = true", StringComparison.Ordinal);
        Assert.True(guard > 0 && send > guard);
    }
}
