using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Gifting.Application.Commands;
using GrowGift.Gifting.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Idempotency;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Gifting.Infrastructure.Consumers;

/// <summary>
/// gift-order-paid.v1 → issue the redemption code (buy-at-redemption model:
/// payment alone triggers code issuance; no shares are acquired up front).
/// The plaintext code exists only in memory here: it is passed directly to
/// the Fulfillment service over mTLS for printing (and to Notifications for
/// the buyer's digital copy), then dropped. Only the salted hash is persisted.
/// </summary>
public sealed class GiftOrderPaidConsumer(
    IServiceScopeFactory scopeFactory,
    IConsumer<string, string> consumer,
    IHttpClientFactory httpFactory,
    ILogger<GiftOrderPaidConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private async Task ConsumeLoop(CancellationToken ct)
    {
        consumer.Subscribe("growgift.gifting.gift-order-paid.v1");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(ct);
                await HandleAsync(result, ct);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process gift-order-paid message; will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleAsync(ConsumeResult<string, string> message, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize<GiftOrderPaidV1>(message.Message.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        using var scope = scopeFactory.CreateScope();
        var idempotency = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        if (!await idempotency.TryClaimAsync($"gifting:issue:{evt.EventId}", TimeSpan.FromDays(7), ct))
            return;

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var issued = await sender.Send(new IssueRedemptionCodeCommand(evt.OrderId), ct);

        if (issued.IsFailure)
        {
            logger.LogWarning("Code issuance skipped for order {OrderId}: {Error}",
                evt.OrderId, issued.Error.Code);
            return;
        }

        // Lets the buyer see their own code on the confirmation page — see
        // RevealedCodeCache's doc for why this is a deliberate, narrow
        // exception to "the plaintext is never persisted."
        var revealCache = scope.ServiceProvider.GetRequiredService<GrowGift.Gifting.Infrastructure.Codes.IRevealedCodeCache>();
        await revealCache.SetAsync(evt.OrderId, issued.Value.PlaintextCode, ct);

        // Same synchronous-handoff treatment as Fulfillment/Notifications
        // below: GiftOrderPaidV1 doesn't carry delivery method, box, or
        // recipient-email details (widening a contract shared across
        // services for one consumer's needs felt like the wrong tradeoff),
        // so this reads the full order from Gifting's own database instead
        // — already in scope, and it's this service's own aggregate.
        var db = scope.ServiceProvider.GetRequiredService<GiftingDbContext>();
        var order = await db.GiftOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == evt.OrderId, ct);

        // Physical delivery only — an email-delivery order has no printed
        // card at all, so there's nothing for Fulfillment to produce.
        if (order is not null && order.DeliveryMethod == GrowGift.Gifting.Domain.DeliveryMethod.Physical)
        {
            var fulfillment = httpFactory.CreateClient("fulfillment");
            var response = await fulfillment.PostAsJsonAsync("/internal/v1/fulfillment-orders", new
            {
                orderId = evt.OrderId,
                giftId = issued.Value.GiftId,
                plaintextCode = issued.Value.PlaintextCode,
                expiresAtUtc = issued.Value.ExpiresAtUtc,
                // Courier shipment inputs. This branch only runs from the order-PAID consumer for a Physical order,
                // which is what makes paymentConfirmed true — Fulfillment re-checks both flags and refuses otherwise.
                buyerId = order.BuyerId,
                buyerEmail = order.BuyerEmail,
                recipientName = order.RecipientName,
                boxSku = order.BoxSku,
                deliveryMethod = "Physical",
                paymentConfirmed = true
            }, ct);
            response.EnsureSuccessStatusCode();
        }

        // The buyer's receipt email: if it fails to send, that's logged but
        // not fatal to code issuance — the buyer already has (or will have)
        // the code on the confirmation page and in their profile regardless
        // of whether this specific email goes out.
        if (order is not null)
        {
            try
            {
                var notifications = httpFactory.CreateClient("notifications");
                var receiptResponse = await notifications.PostAsJsonAsync("/internal/v1/notifications/gift-order-receipt", new
                {
                    orderId = order.Id,
                    buyerEmail = order.BuyerEmail,
                    recipientName = order.RecipientName,
                    symbol = order.Symbol,
                    amount = order.PurchaseAmount.Amount,
                    currency = order.PurchaseAmount.Currency,
                    boxSku = order.BoxSku,
                    boxPrice = order.BoxPrice?.Amount,
                    postalFee = order.PostalFee?.Amount,
                    serviceFee = order.ServiceFee?.Amount,
                    redemptionCode = issued.Value.PlaintextCode
                }, ct);
                if (!receiptResponse.IsSuccessStatusCode)
                    logger.LogWarning("Receipt email failed for order {OrderId}: {Status}", order.Id, receiptResponse.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Receipt email send threw for order {OrderId}", order.Id);
            }

            // Email-delivery orders only: this is the recipient's actual
            // delivery mechanism, not just a courtesy copy — there's no
            // printed card coming, so if this fails, the recipient has no
            // other way to get the code. Still non-fatal here (code
            // issuance itself already succeeded and the buyer has their own
            // copy above), but worth flagging more loudly than the buyer
            // receipt's failure, since a buyer can always re-share their
            // own copy — the recipient has no equivalent fallback.
            if (order.DeliveryMethod == GrowGift.Gifting.Domain.DeliveryMethod.Email && order.RecipientEmail is not null)
            {
                try
                {
                    var notifications = httpFactory.CreateClient("notifications");
                    var arrivedResponse = await notifications.PostAsJsonAsync("/internal/v1/notifications/gift-arrived", new
                    {
                        orderId = order.Id,
                        recipientEmail = order.RecipientEmail,
                        symbol = order.Symbol,
                        amount = order.PurchaseAmount.Amount,
                        currency = order.PurchaseAmount.Currency,
                        redemptionCode = issued.Value.PlaintextCode,
                        greetingMessage = order.GreetingMessage
                    }, ct);
                    if (!arrivedResponse.IsSuccessStatusCode)
                        logger.LogError(
                            "Gift-arrived email failed for order {OrderId} — this is the recipient's ONLY delivery mechanism for an email-delivery order: {Status}",
                            order.Id, arrivedResponse.StatusCode);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Gift-arrived email threw for order {OrderId} — this is the recipient's ONLY delivery mechanism for an email-delivery order",
                        order.Id);
                }
            }
        }
    }
}
