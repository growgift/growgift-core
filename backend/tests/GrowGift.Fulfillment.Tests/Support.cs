using System.Net;
using GrowGift.Fulfillment.Shipping;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowGift.Fulfillment.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 27, 1, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan t) => UtcNow += t;
}

public sealed class FakeShippit : IShippitClient
{
    public int Creates, Labels, Books, Trackings;
    public Queue<ShippitResult<ShippitCreated>> CreateReplies = new();
    public Queue<ShippitResult<ShippitLabel>> LabelReplies = new();
    public Queue<ShippitResult<ShippitBooked>> BookReplies = new();
    public Queue<ShippitResult<ShippitTracking>> TrackingReplies = new();
    public ShippitCreateRequest? LastCreate;

    public Task<ShippitResult<ShippitCreated>> CreateOrderAsync(ShippitCreateRequest r, CancellationToken ct)
    { Creates++; LastCreate = r; return Task.FromResult(CreateReplies.Count > 0 ? CreateReplies.Dequeue() : ShippitResult<ShippitCreated>.Ok(new("TN" + Creates, "Courier", "order_placed", null))); }
    public Task<ShippitResult<ShippitLabel>> GetLabelAsync(string tn, CancellationToken ct)
    { Labels++; return Task.FromResult(LabelReplies.Count > 0 ? LabelReplies.Dequeue() : ShippitResult<ShippitLabel>.Ok(new("https://labels.example/x.pdf", "Courier", null, null, "processing"))); }
    public Task<ShippitResult<ShippitBooked>> BookAsync(string tn, CancellationToken ct)
    { Books++; return Task.FromResult(BookReplies.Count > 0 ? BookReplies.Dequeue() : ShippitResult<ShippitBooked>.Ok(new("M1", "10", null))); }
    public Task<ShippitResult<ShippitTracking>> GetTrackingAsync(string tn, CancellationToken ct)
    { Trackings++; return Task.FromResult(TrackingReplies.Count > 0 ? TrackingReplies.Dequeue() : ShippitResult<ShippitTracking>.Ok(new("in_transit", new[] { new ShippitTrackingEvent("in_transit", null) }, null, null))); }
    public Task<ShippitResult<IReadOnlyList<ShippitWebhookInfo>>> ListWebhooksAsync(CancellationToken ct) => Task.FromResult(ShippitResult<IReadOnlyList<ShippitWebhookInfo>>.Ok(Array.Empty<ShippitWebhookInfo>()));
    public Task<ShippitResult<ShippitWebhookInfo>> CreateWebhookAsync(string url, string token, CancellationToken ct) => Task.FromResult(ShippitResult<ShippitWebhookInfo>.Ok(new("w1", url, true)));
}

public sealed class FakeAddresses : IAddressSource
{
    public BuyerAddress? Address = new("Gina Giver", "1 Test St", null, "Sydney", "NSW", "2000", "AU", "0400000000", "gina@example.com");
    public bool Fail;
    public int Calls;
    public Task<(BuyerAddress?, bool)> GetAsync(Guid userId, CancellationToken ct) { Calls++; return Task.FromResult((Fail ? null : Address, Fail)); }
}

public sealed class FakeNotifier : IShipmentNotifier
{
    public List<(Guid Id, NotifyKind Kind, string Key)> Sent = new();
    public bool Fail;
    public Task<bool> SendAsync(Shipment s, NotifyKind kind, string key, CancellationToken ct)
    { if (Fail) return Task.FromResult(false); Sent.Add((s.Id, kind, key)); return Task.FromResult(true); }
}

public sealed class Rig
{
    public FakeClock Clock = new();
    public InMemoryShipmentStore Store;
    public FakeShippit Shippit = new();
    public FakeAddresses Addresses = new();
    public FakeNotifier Notifier = new();
    public ShippitOptions Options = new() { ApiSecret = "test-secret-never-real", WebhookToken = "hook-token", AutoBook = true, BaseBackoff = TimeSpan.FromSeconds(30) };
    public ShipmentProcessor Processor;
    public ShipmentIntake Intake;
    public ShippitWebhookHandler Webhooks;

    public Rig()
    {
        Store = new InMemoryShipmentStore(() => Clock.UtcNow);
        Processor = new ShipmentProcessor(Store, Shippit, Addresses, Notifier, Options, Clock, NullLogger<ShipmentProcessor>.Instance);
        Intake = new ShipmentIntake(Store, Clock);
        Webhooks = new ShippitWebhookHandler(Store, Clock, Options, NullLogger<ShippitWebhookHandler>.Instance);
    }

    public static ShipmentRequest Paid(Guid? orderId = null) =>
        new(orderId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "gina@example.com", "Rae Receiver", "BOX-STD", "Physical", true);

    public async Task<Shipment> NewShipmentAsync()
    {
        var res = await Intake.RegisterAsync(Paid(), default);
        return (await Store.GetAsync(res.ShipmentId!.Value, default))!;
    }

    /// <summary>Runs one worker pass: claim a due shipment and step it (what ShipmentWorker does).</summary>
    public async Task<bool> TickAsync()
    {
        var s = await Store.ClaimDueAsync(Clock.UtcNow, TimeSpan.FromMinutes(2), default);
        if (s is null) return false;
        await Processor.StepAsync(s, default);
        return true;
    }

    /// <summary>Ticks until nothing is due, advancing the clock past any backoff up to <paramref name="maxSteps"/>.</summary>
    public async Task RunAsync(int maxSteps = 30, bool advance = true)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (await TickAsync()) continue;
            if (!advance) return;
            Clock.Advance(TimeSpan.FromMinutes(31));
            if (!await TickAsync()) return;
        }
    }

    public static byte[] Hook(string tn, string state, string? reference = null, string? time = "2026-10-27T02:00:00Z") =>
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tracking_number"] = tn, ["current_state"] = state, ["retailer_reference"] = reference,
            ["status_history"] = new[] { new Dictionary<string, object?> { ["status"] = state, ["time"] = time } }
        }));
}

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Seen = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Seen.Add((r.Method, r.RequestUri!.AbsolutePath, r.Content is null ? null : await r.Content.ReadAsStringAsync(ct), r.Headers.Authorization?.ToString()));
        return f(r);
    }
}
