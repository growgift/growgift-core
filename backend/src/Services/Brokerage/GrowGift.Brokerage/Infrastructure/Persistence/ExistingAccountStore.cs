using GrowGift.Brokerage.Domain;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Infrastructure.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Brokerage.Infrastructure.Persistence;

/// <summary>EF-backed <see cref="IExistingAccountStore"/>: same tables and outbox the onboarding Submit already uses.</summary>
public sealed class ExistingAccountStore(BrokerageDbContext db, IEventPublisher publisher) : IExistingAccountStore
{
    public async Task<LocalOnboardingState?> GetAsync(Guid userId, CancellationToken ct)
    {
        var a = await db.OnboardingApplications.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return a is null ? null : new LocalOnboardingState(a.Status == OnboardingStatus.Approved, a.ViewTradeAccountRef, a.ViewTradeUmaUserId);
    }

    public async Task<bool> IsLinkedToAnotherUserAsync(Guid userId, long umaUserId, string? accountRef, CancellationToken ct) =>
        await db.OnboardingApplications.AnyAsync(x => x.UserId != userId &&
            (x.ViewTradeUmaUserId == umaUserId || (accountRef != null && x.ViewTradeAccountRef == accountRef)), ct);

    private async Task<OnboardingApplication> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var app = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (app is not null) return app;
        app = OnboardingApplication.Start(userId);
        db.OnboardingApplications.Add(app);
        return app;
    }

    public async Task RememberUserAsync(Guid userId, long umaUserId, long? inProgressApplicationId, CancellationToken ct)
    {
        var app = await GetOrCreateAsync(userId, ct);
        app.SetViewTradeUmaUserId(umaUserId);
        if (inProgressApplicationId is { } id) app.SetViewTradeApplicationId(id);
        await db.SaveChangesAsync(ct);
    }

    public async Task LinkAsync(Guid userId, ExistingViewTradeApplication existing, CancellationToken ct)
    {
        var app = await GetOrCreateAsync(userId, ct);
        app.SetViewTradeUmaUserId(existing.UmaUserId);
        app.SetViewTradeApplicationId(existing.ApplicationId);
        // The evaluator decides "has an issued account" from account_number + both status fields; the local record and
        // event must say so in the vocabulary the rest of the platform uses ("approved"), not ViewTrade's raw workflow
        // status (e.g. "background_check_success"), or the link is stored as Pending and never counts as approved.
        var status = ExistingAccountEvaluator.Classify(existing) == ExistingAccountEvaluator.AppKind.Approved ? "approved" : existing.Status;
        app.CompleteWithResult(existing.AccountRef ?? "", status);
        // Same event, same outbox as a normally-completed onboarding → Identity sets kyc_status / viewtrade_account.
        await publisher.EnqueueAsync(new AccountOpenedV1(userId, existing.AccountRef ?? "", status),
            partitionKey: userId.ToString(), ct);
        await db.SaveChangesAsync(ct); // application + outbox message commit together
    }

    public async Task ReannounceAsync(Guid userId, string accountRef, CancellationToken ct)
    {
        await publisher.EnqueueAsync(new AccountOpenedV1(userId, accountRef, "approved"),
            partitionKey: userId.ToString(), ct);
        await db.SaveChangesAsync(ct);
    }
}
