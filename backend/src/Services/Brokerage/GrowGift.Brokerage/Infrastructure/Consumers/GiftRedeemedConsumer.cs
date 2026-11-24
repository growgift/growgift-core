using System.Text.Json;
using Confluent.Kafka;
using GrowGift.Brokerage.Domain;
using GrowGift.Brokerage.Infrastructure.Fx;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Infrastructure.Common.Idempotency;
using GrowGift.Infrastructure.Common.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrowGift.Brokerage.Infrastructure.Consumers;

/// <summary>
/// gift-redeemed.v1 → journal transfer omnibus lot → recipient account →
/// ownership-transferred.v1. Same effectively-once pattern as acquisition.
/// </summary>
public sealed class GiftRedeemedConsumer(
    IServiceScopeFactory scopeFactory,
    Func<IConsumer<string, string>> consumerFactory,
    ILogger<GiftRedeemedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    // How long to wait between in-process attempts. A failed message is NOT redelivered by Kafka while this
    // consumer keeps running (the next Consume() returns the next message, and committing that one moves the
    // group past the failed one), so retrying must happen here — otherwise a transient failure silently drops the gift.
    private static readonly TimeSpan[] RetryDelays =
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    private async Task ConsumeLoop(CancellationToken ct)
    {
        using var consumer = consumerFactory();
        consumer.Subscribe("growgift.gifting.gift-redeemed.v1");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                await ProcessAsync(result, ct);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Only broker/commit-level problems reach here; per-message failures are handled in ProcessAsync.
                logger.LogError(ex, "gift-redeemed consumer error; continuing");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> message, CancellationToken ct)
    {
        GiftRedeemedV1? evt;
        try
        {
            evt = JsonSerializer.Deserialize<GiftRedeemedV1>(message.Message.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Skipping an unreadable gift-redeemed message at offset {Offset}.", message.Offset);
            return;
        }
        if (evt is null) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await HandleAsync(evt, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Only the exception TYPE is logged: nothing sensitive, and enough to tell FX from sign-in problems.
                logger.LogError("Gift {GiftId}: purchase attempt {Attempt} failed ({Kind}).", evt.GiftId, attempt + 1, ex.GetType().Name);
                if (attempt >= RetryDelays.Length) break;
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        // Out of attempts. Nothing was confirmed as placed, so record the gift as "needs attention": it then shows on
        // My Profile with a Retry button instead of vanishing, and the consumer moves on to the next gift.
        logger.LogError("Gift {GiftId}: giving up after {Attempts} attempts; recorded as needs_attention.", evt.GiftId, RetryDelays.Length + 1);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.BrokerageDbContext>();
        if (!await db.RedemptionPurchases.AnyAsync(p => p.GiftId == evt.GiftId, ct))
            await RecordAsync(db, evt, "needs_attention", "", null, null, ct);
    }

    private async Task HandleAsync(GiftRedeemedV1 evt, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.BrokerageDbContext>();

        // Durable duplicate guard: one purchase record per gift (GiftId is the PK).
        if (await db.RedemptionPurchases.AnyAsync(p => p.GiftId == evt.GiftId, ct)) return;

        // Buy-at-redemption (Notional BUY as the recipient, AUD→USD) lives in RedemptionBuyer so the user-triggered
        // retry behaves identically. Anything it throws means "nothing was placed": safe to attempt again.
        var buyer = scope.ServiceProvider.GetRequiredService<Redemption.RedemptionBuyer>();
        var result = await buyer.BuyAsync(new Redemption.RedemptionRequest(
            evt.GiftId, evt.RecipientUserId, evt.Symbol, evt.Amount, evt.Currency, evt.ViewTradeAccountRef), "", ct);
        await RecordAsync(db, evt, result.State, result.OrderRef, result.Usd, result.Account, ct);
    }

    private static async Task RecordAsync(Persistence.BrokerageDbContext db, GiftRedeemedV1 evt,
        string orderState, string orderRef, UsdConversion? usd, string? account, CancellationToken ct)
    {
        db.RedemptionPurchases.Add(new Persistence.RedemptionPurchase
        {
            GiftId = evt.GiftId,
            RecipientUserId = evt.RecipientUserId,
            AccountRef = account ?? evt.ViewTradeAccountRef,   // the account actually ordered into, for reconciliation
            Symbol = evt.Symbol,
            NotionalAmount = evt.Amount,
            Currency = evt.Currency,
            FilledQuantity = 0,
            AverageFillPrice = 0,
            BrokerOrderRef = orderRef,
            OrderState = orderState,
            UsdAmount = usd?.UsdAmount,
            FxRate = usd?.Rate,
            PurchasedAtUtc = DateTimeOffset.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Duplicate GiftId (PK) — a concurrent delivery already recorded this purchase.
        }
    }
}
