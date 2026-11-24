using System.Net;
using System.Text;
using GrowGift.Fulfillment.Shipping;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowGift.Fulfillment.Tests;

public class ClientTests
{
    private const string Secret = "SUPER-SECRET-STAGING-KEY";
    private static (ShippitClient c, StubHandler h) Make(Func<HttpRequestMessage, HttpResponseMessage> f)
    {
        var h = new StubHandler(f);
        var http = new HttpClient(h);
        ShippitClient.Configure(http, new ShippitOptions { ApiSecret = Secret });
        return (new ShippitClient(http, NullLogger<ShippitClient>.Instance), h);
    }
    private static HttpResponseMessage J(HttpStatusCode c, string body) => new(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static ShippitCreateRequest Req() => new("GG-ABC", "BOX", new("Ann Lee", "1 Secret St", "Unit 4", "Sydney", "nsw", "2000", "AU", "0400111222", "ann@example.com"),
        "standard", "No", new ParcelSpec { WeightKg = 1.2m });

    [Fact] public async Task Create_sends_wrapped_order_to_staging_with_bearer()
    {
        var (c, h) = Make(_ => J(HttpStatusCode.OK, "{\"response\":{\"tracking_number\":\"PP123\",\"courier_name\":\"X\",\"state\":\"order_placed\"}}"));
        var r = await c.CreateOrderAsync(Req(), default);
        Assert.True(r.IsOk); Assert.Equal("PP123", r.Value!.TrackingNumber);
        var seen = Assert.Single(h.Seen);
        Assert.Equal("/api/3/orders", seen.Path); Assert.Equal("Bearer " + Secret, seen.Auth);
        Assert.Contains("\"order\":{", seen.Body); Assert.Contains("\"retailer_invoice\":\"GG-ABC\"", seen.Body);
        Assert.Contains("\"delivery_state\":\"NSW\"", seen.Body); Assert.Contains("\"delivery_address\":\"1 Secret St, Unit 4\"", seen.Body);
    }
    [Fact] public async Task Flat_create_reply_is_understood_too()
    {
        var (c, _) = Make(_ => J(HttpStatusCode.OK, "{\"tracking_number\":\"FLAT1\"}"));
        Assert.Equal("FLAT1", (await c.CreateOrderAsync(Req(), default)).Value!.TrackingNumber);
    }
    [Fact] public async Task Create_2xx_without_tracking_number_is_unknown_not_failure()
    {
        var (c, _) = Make(_ => J(HttpStatusCode.OK, "{\"response\":{}}"));
        Assert.Equal(ShippitOutcome.Unknown, (await c.CreateOrderAsync(Req(), default)).Outcome);
    }
    [Fact] public async Task Create_500_is_unknown_so_it_is_never_blindly_resent()
        => Assert.Equal(ShippitOutcome.Unknown, (await Make(_ => J(HttpStatusCode.InternalServerError, "{\"error\":\"system\"}")).c.CreateOrderAsync(Req(), default)).Outcome);
    [Fact] public async Task Create_timeout_is_unknown()
        => Assert.Equal(ShippitOutcome.Unknown, (await Make(_ => throw new TaskCanceledException()).c.CreateOrderAsync(Req(), default)).Outcome);
    [Fact] public async Task Create_connection_error_is_unknown()
        => Assert.Equal(ShippitOutcome.Unknown, (await Make(_ => throw new HttpRequestException("boom")).c.CreateOrderAsync(Req(), default)).Outcome);
    [Fact] public async Task Create_422_is_permanent_and_code_only()
    {
        var (c, _) = Make(_ => J((HttpStatusCode)422, "{\"error\":\"invalid\",\"errors\":{\"delivery_address\":[\"1 Secret St is not valid\"]}}"));
        var r = await c.CreateOrderAsync(Req(), default);
        Assert.Equal(ShippitOutcome.Permanent, r.Outcome); Assert.Equal("invalid", r.Code);
        Assert.DoesNotContain("Secret", r.Code);
    }
    [Fact] public async Task Free_text_errors_that_could_echo_an_address_are_dropped()
    {
        var (c, _) = Make(_ => J((HttpStatusCode)400, "{\"error\":\"Address 1 Secret St, Sydney is bad\"}"));
        var r = await c.CreateOrderAsync(Req(), default);
        Assert.Equal("rejected_400", r.Code);
    }
    [Fact] public async Task Html_error_body_is_survivable()
    {
        var (c, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("<html>nope</html>") });
        Assert.Equal(ShippitOutcome.Permanent, (await c.CreateOrderAsync(Req(), default)).Outcome);
    }
    [Fact] public async Task Create_429_is_safe_to_retry_with_retry_after()
    {
        var (c, _) = Make(_ => { var m = J((HttpStatusCode)429, "{}"); m.Headers.RetryAfter = new(TimeSpan.FromSeconds(17)); return m; });
        var r = await c.CreateOrderAsync(Req(), default);
        Assert.Equal(ShippitOutcome.RetrySafe, r.Outcome); Assert.Equal(TimeSpan.FromSeconds(17), r.RetryAfter);
    }
    [Fact] public async Task Wrong_merchant_403_is_permanent()
        => Assert.Equal(ShippitOutcome.Permanent, (await Make(_ => J(HttpStatusCode.Forbidden, "{\"error\":\"invalid_merchant_account\"}")).c.CreateOrderAsync(Req(), default)).Outcome);

    [Fact] public async Task Label_ok_reads_url_and_manifest()
    {
        var (c, h) = Make(_ => J(HttpStatusCode.OK, "{\"response\":{\"id\":1,\"qualified_url\":\"https://l/x.pdf\",\"order\":{\"courier_name\":\"AusPost\",\"manifest_number\":\"M7\",\"manifest_id\":\"70\",\"state\":\"x\"}}}"));
        var r = await c.GetLabelAsync("PP123", default);
        Assert.Equal("https://l/x.pdf", r.Value!.LabelUrl); Assert.Equal("M7", r.Value.ManifestNumber);
        Assert.Equal("/api/3/orders/PP123/label", h.Seen[0].Path);
    }
    [Theory]
    [InlineData("label_failed_order_processing")]
    [InlineData("order_not_available_to_label")]
    [InlineData("label_failed_still_generating")]
    public async Task Label_processing_codes_mean_poll_again(string code)
        => Assert.Equal(ShippitOutcome.StillProcessing, (await Make(_ => J((HttpStatusCode)422, "{\"error\":\"" + code + "\"}")).c.GetLabelAsync("T", default)).Outcome);
    [Theory]
    [InlineData("label_failed_missing_courier")]
    [InlineData("address_invalid")]
    [InlineData("label_failed_failed_request")]
    public async Task Other_label_errors_are_permanent(string code)
        => Assert.Equal(ShippitOutcome.Permanent, (await Make(_ => J((HttpStatusCode)422, "{\"error\":\"" + code + "\"}")).c.GetLabelAsync("T", default)).Outcome);
    [Fact] public async Task Label_get_failures_are_retry_safe()
    {
        Assert.Equal(ShippitOutcome.RetrySafe, (await Make(_ => throw new TaskCanceledException()).c.GetLabelAsync("T", default)).Outcome);
        Assert.Equal(ShippitOutcome.RetrySafe, (await Make(_ => J(HttpStatusCode.BadGateway, "")).c.GetLabelAsync("T", default)).Outcome);
    }

    [Fact] public async Task Book_posts_tracking_numbers_and_reads_manifest()
    {
        var (c, h) = Make(_ => J(HttpStatusCode.OK, "{\"response\":[{\"courier\":\"X\",\"manifest\":\"M1\",\"manifest_id\":\"11\",\"success\":true}],\"count\":1}"));
        var r = await c.BookAsync("PP123", default);
        Assert.Equal("M1", r.Value!.ManifestNumber); Assert.Contains("\"orders\":[\"PP123\"]", h.Seen[0].Body); Assert.Equal("/api/3/book", h.Seen[0].Path);
    }
    [Fact] public async Task Book_200_with_success_false_is_a_failure()
        => Assert.Equal(ShippitOutcome.Permanent, (await Make(_ => J(HttpStatusCode.OK, "{\"response\":[{\"success\":false,\"error\":\"carrier_down\"}]}")).c.BookAsync("T", default)).Outcome);
    [Fact] public async Task Book_timeout_is_unknown()
        => Assert.Equal(ShippitOutcome.Unknown, (await Make(_ => throw new TaskCanceledException()).c.BookAsync("T", default)).Outcome);

    [Fact] public async Task Tracking_reads_current_state_and_history()
    {
        var (c, _) = Make(_ => J(HttpStatusCode.OK, "{\"response\":{\"track\":[{\"status\":\"order_placed\",\"time\":\"2026-10-27T01:00:00Z\"},{\"status\":\"in_transit\",\"time\":\"2026-10-27T05:00:00Z\"}],\"courier_name\":\"X\"}}"));
        var r = await c.GetTrackingAsync("T", default);
        Assert.Equal("in_transit", r.Value!.State); Assert.Equal(2, r.Value.History.Count);
    }

    [Fact] public async Task Webhook_registration_carries_the_token_header_and_hides_the_address()
    {
        var (c, h) = Make(_ => J(HttpStatusCode.Created, "{\"response\":{\"publishable_id\":\"abc\"}}"));
        var r = await c.CreateWebhookAsync("https://hooks.example/x", "tok-123", default);
        Assert.Equal("abc", r.Value!.Id);
        Assert.Contains("X-GrowGift-Webhook-Token", h.Seen[0].Body); Assert.Contains("\"hide_delivery_address\":true", h.Seen[0].Body);
        Assert.Contains("\"in_transit\"", h.Seen[0].Body);
    }

    [Fact] public async Task The_secret_never_appears_in_any_result()
    {
        var results = new List<string>();
        foreach (var f in new Func<HttpRequestMessage, HttpResponseMessage>[] {
            _ => J(HttpStatusCode.Forbidden, "{\"error\":\"" + Secret + "\"}"),                  // hostile server echoing the key
            _ => J(HttpStatusCode.InternalServerError, "Bearer " + Secret),
            _ => throw new HttpRequestException("auth " + Secret) })
        {
            var (c, _) = Make(f);
            results.Add(System.Text.Json.JsonSerializer.Serialize(await c.CreateOrderAsync(Req(), default)));
            results.Add(System.Text.Json.JsonSerializer.Serialize(await c.GetLabelAsync("T", default)));
        }
        foreach (var s in results) Assert.DoesNotContain(Secret, s);
    }
}
