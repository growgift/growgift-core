using System.Text.Json;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Brokerage.Domain;

public enum OnboardingStatus { InProgress, Submitting, Pending, Approved, Rejected, Failed }

/// <summary>
/// One in-progress or completed ViewTrade brokerage application for a
/// GrowGift user. Answers are stored per System Identifier (never per raw
/// ViewTrade question_id — those are resolved only at submission time via
/// ViewTradeQuestionConfigResolver) so the same saved application survives
/// ViewTrade renumbering questions between environments.
/// </summary>
public sealed class OnboardingApplication
{
    private OnboardingApplication() { } // EF

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OnboardingStatus Status { get; private set; }
    public string? ViewTradeFormSessionRef { get; private set; } // legacy — superseded by the fields below, kept so the existing DB column isn't orphaned
    public long? ViewTradeUmaUserId { get; private set; }
    public long? ViewTradeApplicationId { get; private set; }
    public string? ViewTradeAccountRef { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // Persisted as JSON columns (see OnboardingApplicationConfiguration) —
    // a flexible key-value bag is the right shape for ~113 possible fields
    // that vary by conditional branch; a fully normalized child table would
    // just be a slower way to store the same thing.
    private Dictionary<string, string> _answers = new();
    private Dictionary<string, List<string>> _multiAnswers = new();
    private Dictionary<string, string> _documentRefs = new(); // systemIdentifier -> stored file key
    private HashSet<string> _viewTradeUploadedDocuments = new(); // systemIdentifiers ViewTrade has already confirmed received

    public IReadOnlyDictionary<string, string> Answers => _answers;
    public IReadOnlyDictionary<string, List<string>> MultiAnswers => _multiAnswers;
    public IReadOnlyDictionary<string, string> DocumentRefs => _documentRefs;
    public IReadOnlySet<string> ViewTradeUploadedDocuments => _viewTradeUploadedDocuments;

    public static OnboardingApplication Start(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Status = OnboardingStatus.InProgress,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    public Result SetAnswer(string systemIdentifier, string value)
    {
        if (Status is OnboardingStatus.Approved or OnboardingStatus.Pending)
            return Result.Failure(Error.Conflict("onboarding.locked", "This application has already been submitted."));
        _answers[systemIdentifier] = value;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result SetMultiAnswer(string systemIdentifier, List<string> values)
    {
        if (Status is OnboardingStatus.Approved or OnboardingStatus.Pending)
            return Result.Failure(Error.Conflict("onboarding.locked", "This application has already been submitted."));
        _multiAnswers[systemIdentifier] = values;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result SetDocument(string systemIdentifier, string storedFileKey)
    {
        if (Status is OnboardingStatus.Approved or OnboardingStatus.Pending)
            return Result.Failure(Error.Conflict("onboarding.locked", "This application has already been submitted."));
        _documentRefs[systemIdentifier] = storedFileKey;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result BeginSubmission()
    {
        // Failed is retryable by design — the error message shown to the
        // user explicitly promises this ("your answers are saved — this
        // can be retried"), and never losing a persisted ViewTrade user id
        // (see SetViewTradeUmaUserId) means a
        // retry safely resumes rather than re-registering.
        if (Status is not (OnboardingStatus.InProgress or OnboardingStatus.Failed))
            return Result.Failure(Error.Conflict("onboarding.invalid_state", $"Cannot submit from state {Status}."));
        Status = OnboardingStatus.Submitting;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Persisted immediately after ViewTrade registration succeeds — kept
    /// separate from SetViewTradeApplicationId below so a retry that fails
    /// on CreateApplicationAsync (or anything after it) never re-registers.
    /// Re-registering isn't just wasteful: ViewTrade's ApplicationList has
    /// no email-based lookup (it requires a known user_id/uma_user_id to
    /// filter by — see ViewTradeAnswerSubmitter's docs), so once a
    /// registration succeeds without this being saved, there is no API-based
    /// way to recover that user id from the email alone.
    /// </summary>
    public void SetViewTradeUmaUserId(long umaUserId)
    {
        ViewTradeUmaUserId = umaUserId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetViewTradeApplicationId(long applicationId)
    {
        ViewTradeApplicationId = applicationId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Persisted immediately after each successful file upload to ViewTrade
    /// — required because retries reuse the same application_id (see
    /// SetViewTradeApplicationId's doc for why that's correct), but
    /// ViewTrade rejects re-uploading a document that was already accepted
    /// for a given question ("Document has already been uploaded", code
    /// document_can_be_uploaded=1081). Without tracking this, a retry after
    /// a LATER step failed (e.g. a different document, or the final submit)
    /// would re-send every earlier-succeeded upload and hit this on every
    /// attempt. Keyed by systemIdentifier, including the synthetic
    /// "applicant.primary.w8.signature" reuse of the signature photo — see
    /// OnboardingEndpoints.Submit's file-upload loop.
    /// </summary>
    public void MarkDocumentUploadedToViewTrade(string systemIdentifier)
    {
        _viewTradeUploadedDocuments.Add(systemIdentifier);
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void CompleteWithResult(string accountRef, string viewTradeStatus)
    {
        ViewTradeAccountRef = accountRef;
        Status = viewTradeStatus.Equals("approved", StringComparison.OrdinalIgnoreCase)
            ? OnboardingStatus.Approved
            : OnboardingStatus.Pending;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Fail(string reason)
    {
        Status = OnboardingStatus.Failed;
        RejectionReason = reason;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    // ---- EF persistence helpers (JSON column (de)serialization) ----
    internal string AnswersJson { get => JsonSerializer.Serialize(_answers); set => _answers = Deserialize<Dictionary<string, string>>(value) ?? new(); }
    internal string MultiAnswersJson { get => JsonSerializer.Serialize(_multiAnswers); set => _multiAnswers = Deserialize<Dictionary<string, List<string>>>(value) ?? new(); }
    internal string DocumentRefsJson { get => JsonSerializer.Serialize(_documentRefs); set => _documentRefs = Deserialize<Dictionary<string, string>>(value) ?? new(); }
    internal string ViewTradeUploadedDocumentsJson { get => JsonSerializer.Serialize(_viewTradeUploadedDocuments); set => _viewTradeUploadedDocuments = Deserialize<HashSet<string>>(value) ?? new(); }
    private static T? Deserialize<T>(string json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);
}
