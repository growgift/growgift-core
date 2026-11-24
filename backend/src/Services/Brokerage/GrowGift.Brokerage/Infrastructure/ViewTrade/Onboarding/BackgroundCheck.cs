using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

/// <summary>
/// Settings for the ViewTrade application form and the two-step background check.
/// FormId: ViewTrade now requires the form to be named when an application is created (form 175 on this branch;
/// the final production form is expected to differ, so it is configuration, not a constant).
/// VerifyDocumentsPath: the second step. ViewTrade's documentation gives it as POST api/applications/verify-documents;
/// VIEWTRADE_VERIFY_DOCUMENTS_PATH can override it without a code change.
/// </summary>
public sealed record ViewTradeOnboardingOptions(int FormId = 175, string VerifyDocumentsPath = "api/applications/verify-documents")
{
    public const string DefaultVerifyDocumentsPath = "api/applications/verify-documents";

    public static ViewTradeOnboardingOptions FromEnvironment(Func<string, string?> get)
    {
        var form = get("VIEWTRADE_FORM_ID");
        var path = get("VIEWTRADE_VERIFY_DOCUMENTS_PATH");
        return new ViewTradeOnboardingOptions(
            int.TryParse(form, out var id) && id > 0 ? id : 175,
            string.IsNullOrWhiteSpace(path) ? DefaultVerifyDocumentsPath : path.Trim().TrimStart('/'));
    }
}

/// <summary>What the background-check endpoint said, reduced to the two fields that drive the next step.</summary>
public sealed record BackgroundCheckReply(string? Status, string? GlobalIdStatus)
{
    public const string DocumentationRequired = "DOCUMENTATION_REQUIRED";

    /// <summary>
    /// Tolerant parse: the real response shape is unconfirmed, so both fields are searched for anywhere in the JSON.
    /// Unparseable input yields an empty reply (treated as "unknown", never as a pass).
    /// </summary>
    public static BackgroundCheckReply Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, null);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            // The documented reply is an Application with applicants[].background_checks[]. When a background_checks
            // entry is present, ITS status is the check's outcome (the application's own status is a different thing).
            if (LastCheck(doc.RootElement) is { } check)
                return new(Find(check, "status"), Find(check, "global_id_status"));
            return new(Find(doc.RootElement, "status") ?? Find(doc.RootElement, "background_check_status"),
                       Find(doc.RootElement, "global_id_status"));
        }
        catch (JsonException) { return new(null, null); }
    }

    private static JsonElement? LastCheck(JsonElement e)
    {
        JsonElement? last = null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals("background_checks") && p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in p.Value.EnumerateArray()) last = item;
                else if (LastCheck(p.Value) is { } inner) last = inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray())
                if (LastCheck(i) is { } inner) last = inner;
        }
        return last;
    }

    private static string? Find(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            foreach (var p in e.EnumerateObject())
                if (Find(p.Value, name) is { } hit) return hit;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray())
                if (Find(i, name) is { } hit) return hit;
        }
        return null;
    }
}

public enum BackgroundCheckStage
{
    /// <summary>ViewTrade passed the application.</summary>
    Passed,
    /// <summary>Identity documents are needed: the second step (verify-documents) must be triggered.</summary>
    DocumentsRequired,
    /// <summary>Not passed and no documents path offered: needs a human at ViewTrade; never retried automatically.</summary>
    NeedsReview,
    /// <summary>The reply could not be understood; nothing is assumed.</summary>
    Unknown
}

public static class BackgroundCheckDecision
{
    /// <summary>
    /// ViewTrade's two-step process: a failed status with global_id_status DOCUMENTATION_REQUIRED means the
    /// background check itself went fine and documents must now be verified. Anything else that is not a clear
    /// success is NOT treated as approval.
    /// </summary>
    public static BackgroundCheckStage Decide(BackgroundCheckReply r)
    {
        if (string.Equals(r.GlobalIdStatus, BackgroundCheckReply.DocumentationRequired, StringComparison.OrdinalIgnoreCase))
            return BackgroundCheckStage.DocumentsRequired;
        if (r.Status is null) return BackgroundCheckStage.Unknown;
        if (r.Status.Contains("success", StringComparison.OrdinalIgnoreCase)
            || r.Status.Equals("passed", StringComparison.OrdinalIgnoreCase)) return BackgroundCheckStage.Passed;
        return BackgroundCheckStage.NeedsReview;
    }

    /// <summary>The wire value the frontend/API reports.</summary>
    public static string Wire(BackgroundCheckStage s, bool documentsTriggered) => s switch
    {
        BackgroundCheckStage.Passed => "passed",
        BackgroundCheckStage.DocumentsRequired => documentsTriggered ? "documents_under_review" : "documents_required",
        BackgroundCheckStage.NeedsReview => "needs_review",
        _ => "unknown"
    };
}

public interface IBackgroundCheckClient
{
    /// <summary>Step 1 of ViewTrade's background check. Returns the reply reduced to status + global_id_status.</summary>
    Task<BackgroundCheckReply> RunBackgroundCheckAsync(long applicationId, string? ipAddress, CancellationToken ct);

    /// <summary>Step 2, only when step 1 reported DOCUMENTATION_REQUIRED.</summary>
    Task VerifyDocumentsAsync(long applicationId, string? ipAddress, CancellationToken ct);
}

public static class BackgroundCheckRunner
{
    /// <summary>
    /// Step 1: background-check. If ViewTrade answers DOCUMENTATION_REQUIRED, step 2: verify-documents. Returns a
    /// short wire value for the frontend. Never throws for ViewTrade problems and never reports a pass it didn't see.
    /// </summary>
    public static async Task<string> RunAsync(IBackgroundCheckClient client, long applicationId, string? ipAddress, ILogger logger, CancellationToken ct)
    {
        try
        {
            var reply = await client.RunBackgroundCheckAsync(applicationId, ipAddress, ct);
            var stage = BackgroundCheckDecision.Decide(reply);
            var triggered = false;
            if (stage == BackgroundCheckStage.DocumentsRequired)
            {
                await client.VerifyDocumentsAsync(applicationId, ipAddress, ct);
                triggered = true;
            }
            return BackgroundCheckDecision.Wire(stage, triggered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            logger.LogWarning("ViewTrade background check could not be completed for application {ApplicationId}: {Kind} ({Status})",
                applicationId, e.GetType().Name, (e as ViewTradeApiException)?.StatusCode.ToString() ?? "n/a");
            return "check_unavailable";
        }
    }
}

public static class ClientIp
{
    /// <summary>
    /// The applicant's IP as ViewTrade's background-check endpoints want it. Behind the gateway the socket address is
    /// the gateway's, so the first X-Forwarded-For entry is preferred. Only a syntactically valid IP is returned; it is
    /// personal data and is never logged.
    /// </summary>
    public static string? From(string? forwardedFor, System.Net.IPAddress? remote)
    {
        var first = forwardedFor?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null && System.Net.IPAddress.TryParse(first, out var fwd)) return fwd.ToString();
        return remote?.MapToIPv4() is { } ip && ip.ToString() != "0.0.0.0" ? ip.ToString() : remote?.ToString();
    }
}
