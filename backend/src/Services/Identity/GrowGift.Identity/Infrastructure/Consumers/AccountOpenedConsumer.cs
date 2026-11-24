using System.Text.Json;
using Confluent.Kafka;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Identity.Domain;
using GrowGift.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrowGift.Identity.Infrastructure.Consumers;

/// <summary>
/// account-opened.v1 (from Brokerage) → advance the user's KYC state.
/// "approved" completes KYC with the real ViewTrade account ref; anything
/// else (e.g. "pending") just ensures the user is marked Pending so the
/// frontend can show "still under review" rather than "not started".
///
/// Idempotency: no Redis in Identity, so this checks current state instead
/// of a separate claim store — if the user's ViewTradeAccountRef already
/// matches the incoming one and Kyc is already Approved, the message is a
/// harmless redelivery and is skipped.
/// </summary>
public sealed class AccountOpenedConsumer(
    IServiceScopeFactory scopeFactory,
    Func<IConsumer<string, string>> consumerFactory,
    ILogger<AccountOpenedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);

    private async Task ConsumeLoop(CancellationToken ct)
    {
        using var consumer = consumerFactory();
        consumer.Subscribe("growgift.brokerage.account-opened.v1");
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
                logger.LogError(ex, "Failed to process account-opened message; will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleAsync(ConsumeResult<string, string> message, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize<AccountOpenedV1>(message.Message.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == evt.UserId, ct);
        if (user is null)
        {
            logger.LogWarning("account-opened.v1 for unknown user {UserId} — ignoring", evt.UserId);
            return;
        }

        if (user.Kyc == KycStatus.Approved && user.ViewTradeAccountRef == evt.AccountRef)
            return; // already applied — redelivery

        // A user who used the dev "simulate verification" shortcut holds a fake VT-DEV-… account. If a REAL
        // approved account now arrives (existing ViewTrade user linked, or onboarding finished), replace it —
        // otherwise they'd stay stuck on the fake one and no shares could ever be bought.
        if (evt.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
            && user.Kyc == KycStatus.Approved && User.IsSimulatedAccount(user.ViewTradeAccountRef))
        {
            var replaced = user.ReplaceSimulatedAccount(evt.AccountRef);
            if (replaced.IsFailure)
                logger.LogWarning("Could not replace simulated account for {UserId}: {Reason}", evt.UserId, replaced.Error.Message);
            else
                await db.SaveChangesAsync(ct);
            return;
        }

        if (user.Kyc == KycStatus.NotStarted)
            user.StartKyc($"viewtrade:{evt.AccountRef}");

        if (evt.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
        {
            var completed = user.CompleteKyc("approved", evt.AccountRef);
            if (completed.IsFailure)
                logger.LogWarning("Could not complete KYC for {UserId}: {Reason}", evt.UserId, completed.Error.Message);
        }
        // "pending" (or anything else): leave Kyc = Pending — StartKyc above
        // already set that if this is the first event for this user.

        await db.SaveChangesAsync(ct);
    }
}
