using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

/// <summary>
/// What gift redemption should do about broker onboarding for a signed-in
/// recipient. Only <see cref="SkipOnboarding"/> lets them bypass it.
/// </summary>
public enum ExistingAccountDecision
{
    /// <summary>No ViewTrade user for this e-mail → current onboarding flow.</summary>
    StartOnboarding,
    /// <summary>ViewTrade user exists but application isn't submitted yet → continue onboarding, reusing that user.</summary>
    ContinueOnboarding,
    /// <summary>ViewTrade user with an APPROVED application and an account number → skip onboarding.</summary>
    SkipOnboarding,
    /// <summary>Application submitted but not approved (any status other than in_progress / approved / rejected).</summary>
    PendingReview,
    /// <summary>User inactive, application rejected, or otherwise not usable. Don't assume eligibility.</summary>
    NotEligible,
    /// <summary>More than one ViewTrade user for the e-mail, or a ViewTrade account already tied to another GrowGift user.</summary>
    Ambiguous,
    /// <summary>GrowGift e-mail not verified → never match against ViewTrade.</summary>
    EmailUnverified,
}

public sealed record ExistingAccountResult(ExistingAccountDecision Decision, string? AccountRef = null);

/// <summary>What GrowGift already stores about this user's onboarding (nothing if they've never started).</summary>
public sealed record LocalOnboardingState(bool IsApproved, string? AccountRef, long? UmaUserId);

/// <summary>Persistence seam so the decision flow is testable without a database.</summary>
public interface IExistingAccountStore
{
    Task<LocalOnboardingState?> GetAsync(Guid userId, CancellationToken ct);
    /// <summary>True if a DIFFERENT GrowGift user is already linked to this ViewTrade user / account.</summary>
    Task<bool> IsLinkedToAnotherUserAsync(Guid userId, long umaUserId, string? accountRef, CancellationToken ct);
    /// <summary>Remember the ViewTrade user (and application) so the existing onboarding Submit reuses it instead of re-registering (which ViewTrade rejects: 4090).</summary>
    Task RememberUserAsync(Guid userId, long umaUserId, long? inProgressApplicationId, CancellationToken ct);
    /// <summary>Record the ViewTrade outcome locally and tell Identity (account-opened.v1) — same effect as finishing onboarding.</summary>
    Task LinkAsync(Guid userId, ExistingViewTradeApplication application, CancellationToken ct);
    /// <summary>Re-announce an already-approved local account (idempotent downstream).</summary>
    Task ReannounceAsync(Guid userId, string accountRef, CancellationToken ct);
}

/// <summary>
/// Pure decision rules. Deliberately conservative about the ViewTrade status
/// vocabulary, because only two values are confirmed in this codebase:
/// "in_progress" (a freshly created application) and "approved". "rejected"
/// is treated as a refusal because its meaning is unambiguous. EVERYTHING
/// else is "submitted but not approved" — never complete.
/// </summary>
public static class ExistingAccountEvaluator
{
    public enum AppKind { Approved, InProgress, Rejected, Pending }

    // The explicit status-mapping layer. ViewTrade reports TWO separate fields (ApplicationGet sample: status
    // "background_check_success" with account_status "in_progress", account_number null until issued), so the
    // single `status` value must not decide alone. Only the values seen in samples/earlier code are named; anything
    // unknown never counts as "approved" and never as "rejected" — it stays Pending (a retryable, non-destructive stop).
    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase)
        { "rejected", "declined", "denied" };
    private static readonly HashSet<string> Closed = new(StringComparer.OrdinalIgnoreCase)
        { "closed", "cancelled", "canceled", "suspended", "restricted" };

    public static AppKind Classify(ExistingViewTradeApplication a)
    {
        var status = (a.Status ?? "").Trim();
        var account = (a.AccountStatus ?? "").Trim();
        var hasAccountNumber = !string.IsNullOrWhiteSpace(a.AccountRef);

        if (Rejected.Contains(status) || Rejected.Contains(account)) return AppKind.Rejected;
        if (Closed.Contains(status) || Closed.Contains(account)) return AppKind.Rejected; // never link a closed/suspended account

        // An issued account number is the evidence that an account exists; a stale or unrelated application
        // `status` (e.g. still "pending") must not override it (Requirement D).
        if (hasAccountNumber) return AppKind.Approved;

        // Not issued yet: "approved" without a number isn't usable; an unsubmitted draft continues onboarding.
        if (status.Equals("approved", StringComparison.OrdinalIgnoreCase)) return AppKind.Pending;
        if (status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
            && !account.Equals("pending", StringComparison.OrdinalIgnoreCase)) return AppKind.InProgress;
        return AppKind.Pending;
    }

    /// <summary>Decision for ONE already-identified ViewTrade user from their applications.</summary>
    public static (ExistingAccountDecision Decision, ExistingViewTradeApplication? Basis) Evaluate(
        IReadOnlyList<ExistingViewTradeApplication> applications)
    {
        var approved = applications.FirstOrDefault(a => Classify(a) == AppKind.Approved);
        if (approved is not null) return (ExistingAccountDecision.SkipOnboarding, approved);

        var pending = applications.FirstOrDefault(a => Classify(a) == AppKind.Pending);
        if (pending is not null) return (ExistingAccountDecision.PendingReview, pending);

        var inProgress = applications.FirstOrDefault(a => Classify(a) == AppKind.InProgress);
        if (inProgress is not null) return (ExistingAccountDecision.ContinueOnboarding, inProgress);

        // Only rejected applications → refuse rather than silently opening a second one.
        if (applications.Count > 0) return (ExistingAccountDecision.NotEligible, applications[0]);

        // A user with no application at all simply hasn't started one.
        return (ExistingAccountDecision.ContinueOnboarding, null);
    }
}

/// <summary>
/// Decides, for a signed-in recipient, whether ViewTrade onboarding is needed
/// — BEFORE the app redirects them into it. The only identity input is the
/// caller's own verified e-mail (from their JWT, never the request body), and
/// e-mail alone is not treated as proof of ownership: it must be verified, map
/// to exactly one ViewTrade user, and that user must not already be tied to a
/// different GrowGift user.
///
/// A lookup failure THROWS (ViewTradeSessionException / ViewTradeApiException /
/// HttpRequestException…): the caller must treat that as "unknown" and offer a
/// retry — never as "no user" (which would start a duplicate application).
/// Re-running after success is harmless: the stored local approval short-circuits.
/// </summary>
public sealed class ExistingAccountResolver(
    IViewTradeUserDirectory directory,
    IViewTradeApplicationLookup applications,
    IExistingAccountStore store,
    ViewTradeFirmOptions firm,
    ILogger<ExistingAccountResolver> logger)
{
    public async Task<ExistingAccountResult> ResolveAsync(Guid userId, string email, bool emailVerified, CancellationToken ct)
    {
        if (!emailVerified) return new(ExistingAccountDecision.EmailUnverified);

        var local = await store.GetAsync(userId, ct);
        if (local is { IsApproved: true } && !string.IsNullOrWhiteSpace(local.AccountRef))
        {
            // Already onboarded on our side; the caller only asks if its token still lacks the claims.
            await store.ReannounceAsync(userId, local.AccountRef!, ct);
            return new(ExistingAccountDecision.SkipOnboarding, local.AccountRef);
        }

        // Which ViewTrade user? A mapping we stored ourselves beats an e-mail search.
        long umaUserId;
        if (local?.UmaUserId is { } known)
        {
            umaUserId = known;
        }
        else
        {
            var matches = await directory.FindByEmailAsync(email, ct);
            if (matches.Count == 0) return new(ExistingAccountDecision.StartOnboarding);

            var ids = matches.Select(m => m.Id).Distinct().ToList();
            if (ids.Count > 1)
            {
                logger.LogWarning("Multiple ViewTrade users ({Count}) match one verified e-mail for GrowGift user {UserId}.", ids.Count, userId);
                return new(ExistingAccountDecision.Ambiguous);
            }

            var user = matches[0];
            var inBranch = !long.TryParse(firm.BranchId, out var branch) || user.BranchIds.Count == 0 || user.BranchIds.Contains(branch);
            if (!user.IsActive || !inBranch) return new(ExistingAccountDecision.NotEligible);
            umaUserId = user.Id;
        }

        // One ViewTrade user ↔ one GrowGift user.
        if (await store.IsLinkedToAnotherUserAsync(userId, umaUserId, null, ct))
        {
            logger.LogWarning("ViewTrade user is already linked to another GrowGift account (requested by {UserId}).", userId);
            return new(ExistingAccountDecision.Ambiguous);
        }

        var apps = await applications.ListApplicationsForKnownUserAsync(umaUserId, ct);
        var (decision, basis) = ExistingAccountEvaluator.Evaluate(apps);

        if (decision == ExistingAccountDecision.SkipOnboarding &&
            await store.IsLinkedToAnotherUserAsync(userId, umaUserId, basis!.AccountRef, ct))
            return new(ExistingAccountDecision.Ambiguous);

        switch (decision)
        {
            case ExistingAccountDecision.SkipOnboarding:
            case ExistingAccountDecision.PendingReview:
                await store.LinkAsync(userId, basis!, ct); // local record + account-opened.v1
                break;
            case ExistingAccountDecision.ContinueOnboarding:
                await store.RememberUserAsync(userId, umaUserId, basis?.ApplicationId, ct);
                break;
        }

        return new(decision, decision == ExistingAccountDecision.SkipOnboarding ? basis!.AccountRef : null);
    }
}
