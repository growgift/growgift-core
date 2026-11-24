using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Trading;
using Xunit;

namespace GrowGift.Brokerage.Tests;

internal sealed class FakeDirectory : IViewTradeUserDirectory
{
    public List<ViewTradeUserMatch> Matches { get; } = new();
    public Exception? Throw { get; set; }
    public int Calls;
    public Task<IReadOnlyList<ViewTradeUserMatch>> FindByEmailAsync(string email, CancellationToken ct)
    {
        Calls++;
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<ViewTradeUserMatch>>(Matches);
    }
}

internal sealed class FakeApplications : IViewTradeApplicationLookup
{
    public List<ExistingViewTradeApplication> Apps { get; } = new();
    public Exception? Throw { get; set; }
    public int Calls;
    public Task<IReadOnlyList<ExistingViewTradeApplication>> ListApplicationsForKnownUserAsync(long umaUserId, CancellationToken ct)
    {
        Calls++;
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<ExistingViewTradeApplication>>(Apps);
    }
}

internal sealed class FakeStore : IExistingAccountStore
{
    public LocalOnboardingState? Local { get; set; }
    public bool LinkedElsewhere { get; set; }
    public List<string> Writes { get; } = new();
    public Task<LocalOnboardingState?> GetAsync(Guid userId, CancellationToken ct) => Task.FromResult(Local);
    public Task<bool> IsLinkedToAnotherUserAsync(Guid userId, long uma, string? acct, CancellationToken ct) => Task.FromResult(LinkedElsewhere);
    public Task RememberUserAsync(Guid userId, long uma, long? appId, CancellationToken ct) { Writes.Add($"remember:{uma}:{appId}"); return Task.CompletedTask; }
    public Task LinkAsync(Guid userId, ExistingViewTradeApplication a, CancellationToken ct)
    {
        Writes.Add($"link:{a.Status}:{a.AccountRef}");
        Local = new LocalOnboardingState(a.Status == "approved", a.AccountRef, a.UmaUserId);
        return Task.CompletedTask;
    }
    public Task ReannounceAsync(Guid userId, string accountRef, CancellationToken ct) { Writes.Add($"reannounce:{accountRef}"); return Task.CompletedTask; }
}

public class ExistingAccountResolverTests
{
    private static readonly Guid User = Guid.NewGuid();
    private readonly FakeDirectory _dir = new();
    private readonly FakeApplications _apps = new();
    private readonly FakeStore _store = new();

    private ExistingAccountResolver Svc() =>
        new(_dir, _apps, _store, new ViewTradeFirmOptions("1585", "2641"), new CapturingLogger<ExistingAccountResolver>());

    private void VtUser(long id = 124246, string status = "active", long branch = 2641) =>
        _dir.Matches.Add(new ViewTradeUserMatch(id, status, new long[] { branch }));

    private static ExistingViewTradeApplication App(string status, string? acct = null, long uma = 124246) => new(uma, 9001, status, acct);

    [Fact]
    public async Task Existing_user_with_approved_application_skips_onboarding_and_links_it()
    {
        VtUser(); _apps.Apps.Add(App("approved", "ORBS-01-004099"));
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.SkipOnboarding, r.Decision);
        Assert.Equal("ORBS-01-004099", r.AccountRef);
        Assert.Equal(new[] { "link:approved:ORBS-01-004099" }, _store.Writes);
    }

    [Fact]
    public async Task No_matching_user_starts_the_existing_onboarding_and_writes_nothing()
    {
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.StartOnboarding, r.Decision);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _apps.Calls);
    }

    [Fact]
    public async Task Existing_user_with_in_progress_application_continues_and_reuses_that_user()
    {
        VtUser(); _apps.Apps.Add(App("in_progress"));
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.ContinueOnboarding, r.Decision);
        // Remembering the ViewTrade user id is what stops Submit re-registering (ViewTrade 4090).
        Assert.Equal(new[] { "remember:124246:9001" }, _store.Writes);
    }

    [Fact]
    public async Task Existing_user_with_no_application_continues_onboarding()
    {
        VtUser();
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.ContinueOnboarding, r.Decision);
        Assert.Equal(new[] { "remember:124246:" }, _store.Writes);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("pending")]
    [InlineData("under_review")]
    [InlineData("some_status_nobody_documented")]
    public async Task Submitted_but_not_approved_is_pending_review_never_complete(string status)
    {
        VtUser(); _apps.Apps.Add(App(status));
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.PendingReview, r.Decision);
        Assert.Null(r.AccountRef);
    }

    [Fact]
    public async Task Approved_without_an_account_number_is_not_treated_as_complete()
    {
        VtUser(); _apps.Apps.Add(App("approved", null));
        Assert.Equal(ExistingAccountDecision.PendingReview, (await Svc().ResolveAsync(User, "a@x.com", true, default)).Decision);
        _apps.Apps.Clear(); _apps.Apps.Add(App("approved", "  "));
        Assert.Equal(ExistingAccountDecision.PendingReview, (await Svc().ResolveAsync(User, "a@x.com", true, default)).Decision);
    }

    [Fact]
    public async Task Rejected_application_is_not_eligible_and_does_not_start_a_second_one()
    {
        VtUser(); _apps.Apps.Add(App("rejected"));
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.NotEligible, r.Decision);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Inactive_or_out_of_branch_users_are_not_eligible()
    {
        VtUser(status: "suspended");
        Assert.Equal(ExistingAccountDecision.NotEligible, (await Svc().ResolveAsync(User, "a@x.com", true, default)).Decision);
        _dir.Matches.Clear(); VtUser(branch: 777);
        Assert.Equal(ExistingAccountDecision.NotEligible, (await Svc().ResolveAsync(User, "a@x.com", true, default)).Decision);
    }

    [Fact]
    public async Task Several_matching_users_are_ambiguous_and_nothing_is_chosen()
    {
        VtUser(1); VtUser(2);
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.Ambiguous, r.Decision);
        Assert.Equal(0, _apps.Calls);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task A_viewtrade_user_already_linked_to_another_growgift_user_is_refused()
    {
        VtUser(); _apps.Apps.Add(App("approved", "ORBS-01-004099")); _store.LinkedElsewhere = true;
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.Ambiguous, r.Decision);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Unverified_email_is_never_matched_against_viewtrade()
    {
        VtUser(); _apps.Apps.Add(App("approved", "ORBS-01-004099"));
        var r = await Svc().ResolveAsync(User, "a@x.com", false, default);
        Assert.Equal(ExistingAccountDecision.EmailUnverified, r.Decision);
        Assert.Equal(0, _dir.Calls);
        Assert.Equal(0, _apps.Calls);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Timeout_or_error_in_user_lookup_surfaces_as_an_exception_not_as_no_user()
    {
        _dir.Throw = new ViewTradeSessionException(TradingStage.UserLookup, "timed out");
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Svc().ResolveAsync(User, "a@x.com", true, default));
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Authentication_failure_in_application_lookup_surfaces_and_writes_nothing()
    {
        VtUser(); _apps.Throw = new ViewTradeApiException(System.Net.HttpStatusCode.Unauthorized, "nope");
        await Assert.ThrowsAsync<ViewTradeApiException>(() => Svc().ResolveAsync(User, "a@x.com", true, default));
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Retry_after_success_does_not_hit_viewtrade_and_does_not_relink()
    {
        VtUser(); _apps.Apps.Add(App("approved", "ORBS-01-004099"));
        var svc = Svc();
        await svc.ResolveAsync(User, "a@x.com", true, default);
        var dirCalls = _dir.Calls;
        var r = await svc.ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.SkipOnboarding, r.Decision);
        Assert.Equal(dirCalls, _dir.Calls);
        Assert.Equal(1, _store.Writes.Count(w => w.StartsWith("link:")));   // linked once; second call only re-announces
        Assert.Contains("reannounce:ORBS-01-004099", _store.Writes);
    }

    [Fact]
    public async Task A_stored_viewtrade_user_id_is_used_instead_of_an_email_search()
    {
        _store.Local = new LocalOnboardingState(false, null, 124246);
        _apps.Apps.Add(App("approved", "ORBS-01-004099"));
        var r = await Svc().ResolveAsync(User, "a@x.com", true, default);
        Assert.Equal(ExistingAccountDecision.SkipOnboarding, r.Decision);
        Assert.Equal(0, _dir.Calls);
    }

    [Fact]
    public async Task Pending_then_approved_later_upgrades_the_link()
    {
        VtUser(); _apps.Apps.Add(App("submitted"));
        var svc = Svc();
        Assert.Equal(ExistingAccountDecision.PendingReview, (await svc.ResolveAsync(User, "a@x.com", true, default)).Decision);
        _apps.Apps.Clear(); _apps.Apps.Add(App("approved", "ORBS-01-004099"));
        Assert.Equal(ExistingAccountDecision.SkipOnboarding, (await svc.ResolveAsync(User, "a@x.com", true, default)).Decision);
    }
}

public class ViewTradeUserDirectoryTests
{
    private readonly FakeViewTrade _vt = new();
    private ViewTradeUserDirectory Dir(FakeInitialTokens? t = null) =>
        new(new FakeFactory(_vt), t ?? new FakeInitialTokens(), new ViewTradeFirmOptions("1585", "2641"), new CapturingLogger<ViewTradeUserDirectory>());

    [Fact]
    public async Task Returns_only_exact_email_matches_case_insensitively()
    {
        _vt.UsersJson = """{"users_list":[{"id":1,"email":"A@x.com","status":"active"},{"id":2,"email":"zz-a@x.com","status":"active"}]}""";
        var m = await Dir().FindByEmailAsync("a@x.com", default);
        Assert.Equal(1, m.Count);
        Assert.Equal(1L, m[0].Id);
    }

    [Fact]
    public async Task Server_error_is_an_exception_not_an_empty_result()
    {
        _vt.Override = p => FakeViewTrade.Json("{}", System.Net.HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Dir().FindByEmailAsync("a@x.com", default));
    }

    [Fact]
    public async Task Unreadable_reply_is_an_exception_not_an_empty_result()
    {
        _vt.Override = p => FakeViewTrade.Json("<html>", System.Net.HttpStatusCode.OK);
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Dir().FindByEmailAsync("a@x.com", default));
    }

    [Fact]
    public async Task Auth_failure_renews_the_token_once_then_fails_without_looping()
    {
        var t = new FakeInitialTokens();
        _vt.Override = p => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<ViewTradeSessionException>(() => Dir(t).FindByEmailAsync("a@x.com", default));
        Assert.Equal(1, t.Invalidations);
        Assert.Equal(2, _vt.Calls.Count);
    }

    [Fact]
    public async Task Search_is_scoped_to_the_configured_firm_and_branch()
    {
        await Dir().FindByEmailAsync("a@x.com", default);
        Assert.Contains("\"branch_ids\":[2641]", _vt.Calls[0].Body);
        Assert.Contains("\"firm_ids\":[1585]", _vt.Calls[0].Body);
    }
}

public class ExistingAccountEvaluatorTests
{
    private static ExistingViewTradeApplication A(string s, string? acct = null) => new(1, 1, s, acct);

    [Fact]
    public void Approved_beats_other_applications_for_the_same_user() =>
        Assert.Equal(ExistingAccountDecision.SkipOnboarding,
            ExistingAccountEvaluator.Evaluate(new[] { A("rejected"), A("in_progress"), A("approved", "X-1") }).Decision);

    [Fact]
    public void Status_matching_is_case_and_whitespace_insensitive() =>
        Assert.Equal(ExistingAccountDecision.SkipOnboarding,
            ExistingAccountEvaluator.Evaluate(new[] { A(" Approved ", "X-1") }).Decision);

    [Fact]
    public void Pending_beats_in_progress() =>
        Assert.Equal(ExistingAccountDecision.PendingReview,
            ExistingAccountEvaluator.Evaluate(new[] { A("in_progress"), A("submitted") }).Decision);

    private static ExistingViewTradeApplication B(string s, string? acct, string? accountStatus) => new(1, 1, s, acct, accountStatus);

    [Fact] // the saved ApplicationGet sample: submitted, background check passed, account not issued yet
    public void Sample_application_with_no_account_number_is_pending_not_skipped() =>
        Assert.Equal(ExistingAccountDecision.PendingReview,
            ExistingAccountEvaluator.Evaluate(new[] { B("background_check_success", null, "in_progress") }).Decision);

    [Fact] // stale/unrelated application status must not override an issued account
    public void Issued_account_number_wins_over_a_stale_pending_status() =>
        Assert.Equal(ExistingAccountDecision.SkipOnboarding,
            ExistingAccountEvaluator.Evaluate(new[] { B("pending_review", "ORBS-01-004099", "active") }).Decision);

    [Fact]
    public void Stale_pending_application_does_not_beat_an_approved_one() =>
        Assert.Equal(ExistingAccountDecision.SkipOnboarding,
            ExistingAccountEvaluator.Evaluate(new[] { B("submitted", null, null), B("whatever", "ORBS-01-004099", "active") }).Decision);

    [Theory]
    [InlineData("closed")]
    [InlineData("suspended")]
    [InlineData("rejected")]
    public void Closed_or_rejected_account_is_never_linked(string accountStatus) =>
        Assert.Equal(ExistingAccountDecision.NotEligible,
            ExistingAccountEvaluator.Evaluate(new[] { B("approved", "ORBS-01-004099", accountStatus) }).Decision);

    [Fact]
    public void Unknown_status_without_account_number_is_pending_never_approved() =>
        Assert.Equal(ExistingAccountDecision.PendingReview,
            ExistingAccountEvaluator.Evaluate(new[] { B("brand_new_status", null, null) }).Decision);

    [Fact]
    public void Draft_in_progress_application_continues_onboarding() =>
        Assert.Equal(ExistingAccountDecision.ContinueOnboarding,
            ExistingAccountEvaluator.Evaluate(new[] { A("in_progress") }).Decision);
}
