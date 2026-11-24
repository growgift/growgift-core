using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrowGift.Fulfillment.Shipping.Hosting;

/// <summary>
/// Drives shipments through create → label → book and the fallback reconciliation. Each loop claims one due shipment
/// under a database lease, so several instances (or a restart mid-step) can't process the same shipment twice.
/// Shippit's limit is 60 requests per 60 s per key; one step makes at most two calls and the worker takes at most
/// <c>MaxStepsPerMinute</c> steps, keeping us well under it.
/// </summary>
public sealed class ShipmentWorker(IServiceScopeFactory scopes, ShippitOptions opt, IClock clock, ILogger<ShipmentWorker> log) : BackgroundService
{
    private const int MaxStepsPerMinute = 25;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { ShippitGuard.EnsureStaging(opt); }
        catch (ShippitConfigurationException e) { log.LogError("Shipment worker disabled: {Reason}", e.Message); return; }

        var window = new Queue<DateTimeOffset>();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = clock.UtcNow;
                while (window.Count > 0 && now - window.Peek() > TimeSpan.FromMinutes(1)) window.Dequeue();
                if (window.Count >= MaxStepsPerMinute) { await Task.Delay(TimeSpan.FromSeconds(5), stop); continue; }

                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IShipmentStore>();
                var s = await store.ClaimDueAsync(now, TimeSpan.FromMinutes(2), stop);
                if (s is null) { await Task.Delay(opt.WorkerTick, stop); continue; }

                window.Enqueue(now);
                await scope.ServiceProvider.GetRequiredService<ShipmentProcessor>().StepAsync(s, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Never log the exception message: it can carry request details. Type is enough to investigate.
                log.LogError("Shipment worker step failed ({Type}); will retry", e.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }
}

/// <summary>Registers our webhook with Shippit once (staging only) if a public URL + token are configured and none exists yet.</summary>
public sealed class ShippitWebhookRegistrar(IServiceScopeFactory scopes, ShippitOptions opt, ILogger<ShippitWebhookRegistrar> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!opt.IsConfigured || string.IsNullOrWhiteSpace(opt.WebhookToken) || !ShippitGuard.IsAcceptableWebhookUrl(opt.WebhookUrl))
        {
            log.LogInformation("Shippit webhook auto-registration skipped (needs ApiSecret, WebhookToken and a public https WebhookUrl)");
            return;
        }
        try
        {
            ShippitGuard.EnsureStaging(opt);
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IShippitClient>();
            var list = await client.ListWebhooksAsync(stop);
            if (list.IsOk && list.Value!.Any(w => string.Equals(w.Url, opt.WebhookUrl, StringComparison.OrdinalIgnoreCase)))
            { log.LogInformation("Shippit webhook already registered"); return; }
            if (!list.IsOk) { log.LogWarning("Could not list Shippit webhooks ({Code}); not registering blindly", list.Code); return; }
            var created = await client.CreateWebhookAsync(opt.WebhookUrl!, opt.WebhookToken!, stop);
            if (created.IsOk) log.LogInformation("Registered Shippit staging webhook");
            else log.LogWarning("Shippit webhook registration failed ({Code})", created.Code);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Shippit webhook registration error ({Type})", e.GetType().Name);
        }
    }
}
