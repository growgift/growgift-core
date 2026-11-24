using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowGift.Brokerage.Tests;

public class BackgroundCheckTests
{
    private sealed class FakeClient(BackgroundCheckReply? reply = null, Exception? failCheck = null, Exception? failDocs = null) : IBackgroundCheckClient
    {
        public int Checks, DocCalls;
        public Task<BackgroundCheckReply> RunBackgroundCheckAsync(long id, string? ip, CancellationToken ct)
        { Checks++; if (failCheck is not null) throw failCheck; return Task.FromResult(reply!); }
        public Task VerifyDocumentsAsync(long id, string? ip, CancellationToken ct)
        { DocCalls++; if (failDocs is not null) throw failDocs; return Task.CompletedTask; }
    }

    private static Task<string> Run(FakeClient c) => BackgroundCheckRunner.RunAsync(c, 1, "203.0.113.5", NullLogger.Instance, CancellationToken.None);

    [Fact] public async Task Failed_status_with_documentation_required_triggers_the_second_step()
    {
        var c = new FakeClient(new("failed", "DOCUMENTATION_REQUIRED"));
        Assert.Equal("documents_under_review", await Run(c));
        Assert.Equal(1, c.Checks); Assert.Equal(1, c.DocCalls);
    }

    [Fact] public async Task A_clean_pass_never_triggers_document_verification()
    {
        var c = new FakeClient(new("background_check_success", null));
        Assert.Equal("passed", await Run(c));
        Assert.Equal(0, c.DocCalls);
    }

    [Fact] public async Task A_failure_without_the_documents_signal_is_not_approval_and_not_retried()
    {
        var c = new FakeClient(new("failed", null));
        Assert.Equal("needs_review", await Run(c));
        Assert.Equal(0, c.DocCalls); Assert.Equal(1, c.Checks);
    }

    [Fact] public async Task An_unreadable_reply_is_unknown_never_a_pass()
    {
        var c = new FakeClient(new(null, null));
        Assert.Equal("unknown", await Run(c));
        Assert.Equal(0, c.DocCalls);
    }

    [Fact] public async Task ViewTrade_errors_are_reported_not_thrown()
    {
        var down = new ViewTradeApiException(System.Net.HttpStatusCode.BadGateway, "x");
        Assert.Equal("check_unavailable", await Run(new FakeClient(failCheck: down)));
        var docsDown = new FakeClient(new("failed", "DOCUMENTATION_REQUIRED"), failDocs: down);
        Assert.Equal("check_unavailable", await Run(docsDown));
    }

    [Fact] public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var c = new FakeClient(failCheck: new OperationCanceledException(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => BackgroundCheckRunner.RunAsync(c, 1, null, NullLogger.Instance, cts.Token));
    }

    [Theory]
    [InlineData("{\"status\":\"failed\",\"global_id_status\":\"DOCUMENTATION_REQUIRED\"}", "failed", "DOCUMENTATION_REQUIRED")]
    [InlineData("{\"status\":true,\"application\":{\"status\":\"background_check_success\"}}", "background_check_success", null)]
    [InlineData("{\"data\":{\"global_id_status\":\"DOCUMENTATION_REQUIRED\"},\"status\":\"failed\"}", "failed", "DOCUMENTATION_REQUIRED")]
    [InlineData("not json", null, null)]
    [InlineData("", null, null)]
    public void Parse_finds_the_fields_wherever_they_sit(string raw, string? status, string? global)
    {
        var r = BackgroundCheckReply.Parse(raw);
        Assert.Equal(status, r.Status); Assert.Equal(global, r.GlobalIdStatus);
    }

    [Fact] public void Documentation_required_is_matched_case_insensitively()
        => Assert.Equal(BackgroundCheckStage.DocumentsRequired, BackgroundCheckDecision.Decide(new("failed", "documentation_required")));

    [Fact] public void Options_default_to_form_175_and_accept_overrides()
    {
        var d = ViewTradeOnboardingOptions.FromEnvironment(_ => null);
        Assert.Equal(175, d.FormId); Assert.Equal("api/applications/verify-documents", d.VerifyDocumentsPath);
        var o = ViewTradeOnboardingOptions.FromEnvironment(k => k == "VIEWTRADE_FORM_ID" ? "190" : "/api/x/verify");
        Assert.Equal(190, o.FormId); Assert.Equal("api/x/verify", o.VerifyDocumentsPath);
        Assert.Equal(175, ViewTradeOnboardingOptions.FromEnvironment(_ => "abc").FormId);
    }

    [Fact] public void The_background_checks_entry_wins_over_the_applications_own_status()
    {
        const string raw = "{\"status\":true,\"application\":{\"status\":\"in_progress\",\"applicants\":[{\"background_checks\":[{\"status\":\"failed\",\"global_id_status\":\"DOCUMENTATION_REQUIRED\"}]}]}}";
        var r = BackgroundCheckReply.Parse(raw);
        Assert.Equal("failed", r.Status); Assert.Equal("DOCUMENTATION_REQUIRED", r.GlobalIdStatus);
        Assert.Equal(BackgroundCheckStage.DocumentsRequired, BackgroundCheckDecision.Decide(r));
    }

    [Fact] public void The_latest_background_check_is_the_one_that_counts()
    {
        const string raw = "{\"application\":{\"applicants\":[{\"background_checks\":[{\"status\":\"failed\",\"global_id_status\":\"DOCUMENTATION_REQUIRED\"},{\"status\":\"success\"}]}]}}";
        Assert.Equal(BackgroundCheckStage.Passed, BackgroundCheckDecision.Decide(BackgroundCheckReply.Parse(raw)));
    }

    [Theory]
    [InlineData("198.51.100.7, 10.0.0.1", "198.51.100.7")]
    [InlineData("not-an-ip", null)]
    [InlineData(null, null)]
    public void Client_ip_prefers_the_first_valid_forwarded_address(string? xff, string? expected)
        => Assert.Equal(expected, ClientIp.From(xff, null));

    [Fact] public void Client_ip_falls_back_to_the_socket_address()
        => Assert.Equal("192.0.2.9", ClientIp.From(null, System.Net.IPAddress.Parse("192.0.2.9")));

    // The real reply ViewTrade sent for a background check without applicants.background_checks requested.
    private const string RealReply = "{\"status\":true,\"application\":{\"id\":158,\"form_id\":253,\"status\":\"background_check_success\",\"account_status\":\"in_progress\",\"account_number\":null}}";

    [Fact] public void The_real_reply_is_read_as_a_pass_and_carries_no_documents_signal()
    {
        var r = BackgroundCheckReply.Parse(RealReply);
        Assert.Equal("background_check_success", r.Status);
        Assert.Null(r.GlobalIdStatus);
        Assert.Equal(BackgroundCheckStage.Passed, BackgroundCheckDecision.Decide(r));
    }
}
