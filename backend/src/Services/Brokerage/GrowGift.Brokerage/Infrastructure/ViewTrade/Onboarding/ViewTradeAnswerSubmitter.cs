using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GrowGift.Brokerage.Infrastructure.ViewTrade;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

/// <summary>Details needed to create the ViewTrade b2c user that owns an application (UMA's /api/v1/auth/b2c/register).</summary>
public sealed record ViewTradeUserRegistration(string FirstName, string LastName, string Email, string Phone, string Username);

/// <summary>
/// FIRM/BRANCH headers ViewTrade requires on UserRegistration specifically
/// (confirmed: without them it rejects with "firm or branch header should
/// be specified", code 6000). Every other AMO/UMA call derives firm/branch
/// from the bearer token's own claims instead — this is the one exception.
/// </summary>
public sealed record ViewTradeFirmOptions(string? FirmId, string? BranchId);

/// <summary>
/// A single resolved answer, ready to submit as part of a batched
/// answer-multiple call: the live question_id (never a hardcoded value —
/// always came from ViewTradeQuestionConfigResolver) plus its data payload.
/// Data is either a bare string/int (single-value answers, including
/// resolved option ids) or an array of {data:&lt;id&gt;} objects (multi-select
/// checkbox answers) — see ViewTradeQuestionConfigResolver's docs on how
/// each is resolved, and OnboardingEndpoints.Submit for where these are built.
/// </summary>
public sealed record ResolvedAnswer(int QuestionId, object Data);

public sealed record ViewTradeSubmissionResult(string AccountRef, string Status);

/// <summary>
/// Found via ApplicationList for an already-known ViewTrade user id.
/// Confirmed via a real saved Postman example that this endpoint filters by
/// {user_id, uma_user_id} in the body — it has no email-based lookup, so
/// this can only help once a user id is already known (e.g. resuming after
/// CreateApplicationAsync failed on a prior attempt whose registration
/// succeeded). Status vocabulary is only partially confirmed:
/// ApplicationCreate's captured response shows a freshly created
/// application has status "in_progress" — anything else is treated as
/// already handled by ViewTrade (submitted/approved/rejected/etc.),
/// matching the caller's own stated rule.
/// </summary>
/// <param name="Status">The application workflow status (e.g. "background_check_success").</param>
/// <param name="AccountStatus">The separate account_status field (e.g. "in_progress"); null if not returned.</param>
public sealed record ExistingViewTradeApplication(long UmaUserId, long ApplicationId, string Status, string? AccountRef, string? AccountStatus = null);

/// <summary>
/// Read-only view of a known ViewTrade user's applications (ApplicationList).
/// Narrow on purpose so gift-redemption's "has this person already onboarded?"
/// check can be tested without faking the whole onboarding submitter.
/// </summary>
public interface IViewTradeApplicationLookup
{
    /// <summary>All applications for the user (empty when none). Throws ViewTradeApiException on a non-2xx reply.</summary>
    Task<IReadOnlyList<ExistingViewTradeApplication>> ListApplicationsForKnownUserAsync(long umaUserId, CancellationToken ct);
}

/// <summary>
/// Drives the real ViewTrade AMO/UMA onboarding flow, confirmed against the
/// GrowGift - ViewTrade Postman workspace (Authentication/UserRegistration,
/// ApplicationManagement/ApplicationCreate, ApplicationAnswerMultiple, and
/// the numbered 3002_.._3018_ example requests, which are all just saved
/// answer-multiple/answer payloads split by form section — not separate
/// endpoints). The actual sequence is:
///   1. Register a ViewTrade b2c user for the applicant (once — and its id
///      is persisted the moment it succeeds, before attempting step 2, so a
///      failure there can never cause a duplicate registration on retry;
///      see OnboardingApplication.SetViewTradeUmaUserId).
///   2. Create an application tied to that user (once) — its id is the
///      application_id every subsequent call needs.
///   3. Batch every text/select/date/radio/checkbox answer into ONE
///      answer-multiple call.
///   4. Upload each document/signature via answer (multipart formdata).
///   5. Submit the application.
///
/// *** STILL UNCONFIRMED ***: the exact response shape of ApplicationSubmit
/// — no example response was captured for it in Postman. Parsed
/// defensively below, matching the {status, request_id, ...} envelope
/// every other confirmed AMO/UMA response uses, and the raw body is logged
/// on success so the real shape can be confirmed and this tightened up
/// afterward. (UserRegistration's shape WAS unconfirmed at first but is
/// now locked in below — see ExtractUserId — after seeing a real capture:
/// {"status":201,"register":{"user":{"id":...}}}, one level deeper than
/// any of the original guesses.)
/// </summary>
public interface IViewTradeAnswerSubmitter : IBackgroundCheckClient
{
    Task<long> RegisterUserAsync(ViewTradeUserRegistration registration, CancellationToken ct);
    Task<ExistingViewTradeApplication?> FindApplicationForKnownUserAsync(long umaUserId, CancellationToken ct);
    Task<long> CreateApplicationAsync(long umaUserId, CancellationToken ct);
    Task SubmitAnswersAsync(long applicationId, IReadOnlyList<ResolvedAnswer> answers, CancellationToken ct);
    Task SubmitFileAsync(long applicationId, int questionId, Stream fileContent, string fileName, string contentType, CancellationToken ct);
    Task<ViewTradeSubmissionResult> FinalizeAsync(long applicationId, CancellationToken ct);
}

public sealed class ViewTradeAnswerSubmitter(
    HttpClient http, // AMO, typed client — see Program.cs
    IHttpClientFactory httpClientFactory, // used to reach the "ViewTradeUmaAuthenticated" client for RegisterUserAsync
    ViewTradeFirmOptions firmOptions,
    ViewTradeOnboardingOptions onboarding,
    ILogger<ViewTradeAnswerSubmitter> logger) : IViewTradeAnswerSubmitter, IViewTradeApplicationLookup
{
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ViewTradeApiException(response.StatusCode, body);
    }

    public async Task<long> RegisterUserAsync(ViewTradeUserRegistration registration, CancellationToken ct)
    {
        // UserRegistration hits the UMA host (not AMO) but still needs the
        // same UMA-issued bearer token as everything else — the raw
        // "ViewTradeUma" client (used by the token provider itself) doesn't
        // carry that handler, so this uses a separate named client that does.
        var uma = httpClientFactory.CreateClient("ViewTradeUmaAuthenticated");
        if (firmOptions.FirmId is not null) uma.DefaultRequestHeaders.Add("FIRM", firmOptions.FirmId);
        if (firmOptions.BranchId is not null) uma.DefaultRequestHeaders.Add("BRANCH", firmOptions.BranchId);

        // ViewTrade requires a password for b2c registration even though
        // GrowGift never uses it — the applicant authenticates through
        // GrowGift, never directly against ViewTrade. Generated once here
        // and discarded; nothing downstream needs to know it.
        var systemPassword = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

        var response = await uma.PostAsJsonAsync("api/v1/auth/b2c/register", new
        {
            first_name = registration.FirstName,
            last_name = registration.LastName,
            password = systemPassword,
            captcha_response = "",
            email = registration.Email,
            phone = registration.Phone,
            username = registration.Username
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("ViewTrade user registration failed: {Status} {Body}", response.StatusCode, body);
            throw new ViewTradeApiException(response.StatusCode, body);
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("ViewTrade UserRegistration response (shape unconfirmed, logging raw): {Body}", raw);

        var userId = ExtractUserId(raw)
            ?? throw new InvalidOperationException(
                "Couldn't find a user id in ViewTrade's registration response — check the logged raw body above and update ExtractUserId.");
        return userId;
    }

    // Confirmed shape: {"status":201,"register":{"user":{"id":..., ...}}}
    private static long? ExtractUserId(string raw)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("register", out var register)
            && register.TryGetProperty("user", out var user)
            && user.TryGetProperty("id", out var nestedId))
            return nestedId.GetInt64();
        // Fallbacks in case a different ViewTrade environment shapes this differently.
        if (root.TryGetProperty("user", out var flatUser) && flatUser.TryGetProperty("id", out var flatId))
            return flatId.GetInt64();
        if (root.TryGetProperty("id", out var topId))
            return topId.GetInt64();
        if (root.TryGetProperty("user_id", out var userId))
            return userId.GetInt64();
        return null;
    }

    public async Task<ExistingViewTradeApplication?> FindApplicationForKnownUserAsync(long umaUserId, CancellationToken ct) =>
        (await ListApplicationsForKnownUserAsync(umaUserId, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<ExistingViewTradeApplication>> ListApplicationsForKnownUserAsync(long umaUserId, CancellationToken ct)
    {
        // Confirmed via a real saved Postman example: ApplicationList
        // filters by a known user_id/uma_user_id in the body — it has NO
        // email-based lookup. That means this can only ever help once we
        // already know a ViewTrade user id; it can't answer "what's this
        // email's user id" for the 409-on-registration case. See
        // OnboardingApplication.SetViewTradeUmaUserId's doc for why that
        // case is instead solved by never losing a known user id in the
        // first place, rather than by looking it up after the fact.
        var response = await http.PostAsJsonAsync("api/applications/list", new
        {
            user_id = umaUserId,
            uma_user_id = umaUserId
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("ViewTrade application list failed: {Status} {Body}", response.StatusCode, errBody);
            throw new ViewTradeApiException(response.StatusCode, errBody);
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var total = root.TryGetProperty("meta", out var meta)
            && meta.TryGetProperty("pagination", out var pagination)
            && pagination.TryGetProperty("total", out var totalEl)
            ? totalEl.GetInt32() : (int?)null;
        if (total == 0) return Array.Empty<ExistingViewTradeApplication>();
        var found = new List<ExistingViewTradeApplication>();
        var unreadable = 0;

        foreach (var app in FindApplicationArray(root))
        {
            var applicationId = app.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : (long?)null;
            var status = app.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
            var accountRef = app.TryGetProperty("account_number", out var acctEl) && acctEl.ValueKind != System.Text.Json.JsonValueKind.Null
                ? acctEl.GetString() : null;
            var accountStatus = app.TryGetProperty("account_status", out var asEl) && asEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? asEl.GetString() : null;
            if (applicationId is null || status is null)
            {
                logger.LogWarning("Found an ApplicationList entry for uma_user_id {UmaUserId} but couldn't read id/status from it — raw body: {Body}", umaUserId, raw);
                unreadable++;
                continue;
            }
            found.Add(new ExistingViewTradeApplication(umaUserId, applicationId.Value, status, accountRef, accountStatus));
        }
        // Entries exist but none could be read: that is "unknown", NOT "no applications" — returning
        // empty here would make the caller start a duplicate application.
        if (found.Count == 0 && unreadable > 0)
            throw new ViewTradeApiException(System.Net.HttpStatusCode.BadGateway, "ApplicationList entries were unreadable.");
        return found;
    }

    // Tries the shapes we'd expect for a list envelope: a bare array, or
    // one nested under a conventionally-named property.
    private static IEnumerable<System.Text.Json.JsonElement> FindApplicationArray(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            return root.EnumerateArray();
        foreach (var key in new[] { "applications", "data", "list", "results" })
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                return arr.EnumerateArray();
        }
        return [];
    }

    public async Task<long> CreateApplicationAsync(long umaUserId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/applications/create", new
        {
            user_id = umaUserId,
            uma_user_id = umaUserId,
            form_id = onboarding.FormId   // required by ViewTrade now (form 175 on this branch); configurable
        }, ct);
        await ThrowIfFailedAsync(response, "application create", ct);

        var payload = await response.Content.ReadFromJsonAsync<ApplicationCreateResponse>(ct)
            ?? throw new InvalidOperationException("Empty ViewTrade application-create response.");
        return payload.Application.Id;
    }

    public async Task SubmitAnswersAsync(long applicationId, IReadOnlyList<ResolvedAnswer> answers, CancellationToken ct)
    {
        if (answers.Count == 0) return;

        var response = await http.PostAsJsonAsync("api/applications/answer-multiple", new
        {
            application_id = applicationId,
            answers = answers.Select(a => new { question_id = a.QuestionId.ToString(), data = a.Data })
        }, ct);

        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("ViewTrade rejected batched answers for application {ApplicationId}: {Status} {Body}",
                applicationId, response.StatusCode, raw);
            throw new ViewTradeApiException(response.StatusCode, raw);
        }

        // A 2xx status alone doesn't mean every answer in the batch actually
        // landed — the same "messages" per-question-id structure seen on
        // outright 406 rejections can also come back inside an overall
        // {"status":true,...} envelope for individual answers that didn't
        // take, with everything else in the batch still accepted. Treating
        // any HTTP success as complete success was a real blind spot: a few
        // dashboard fields (DOB, phone, tax-form-information) staying
        // unfilled despite no error ever surfacing is consistent with
        // exactly this — silently-partial answer-multiple calls.
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("messages", out var messages)
            && messages.ValueKind == System.Text.Json.JsonValueKind.Object
            && messages.EnumerateObject().Any())
        {
            logger.LogError("ViewTrade returned 2xx for batched answers but flagged per-question issues for application {ApplicationId}: {Body}",
                applicationId, raw);
            throw new ViewTradeApiException(response.StatusCode, raw);
        }
    }

    public async Task SubmitFileAsync(long applicationId, int questionId, Stream fileContent, string fileName, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileContent);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(new StringContent(applicationId.ToString()), "application_id");
        form.Add(new StringContent(questionId.ToString()), "question_id");
        form.Add(streamContent, "data", fileName);

        var response = await http.PostAsync("api/applications/answer", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // ViewTrade's error_codes.question_id.document_can_be_uploaded=1081
            // ("Document has already been uploaded") means ViewTrade itself
            // already has this document — which is the actual goal of this
            // call, just satisfied by a different attempt. Treating this as
            // a hard failure kept recreating the exact same problem it was
            // meant to prevent: our local upload-tracking only works while
            // the local application row survives, but dev resets (and, more
            // generally, any environment where local state can diverge from
            // ViewTrade's) mean local state alone can't be trusted as the
            // sole guard against re-uploading. ViewTrade's own confirmation
            // that the document already exists is a MORE authoritative
            // signal than our local tracking ever was — so treat it as
            // success rather than aborting the whole submission over it.
            if (body.Contains("\"document_can_be_uploaded\":1081"))
            {
                logger.LogWarning(
                    "ViewTrade already has a document for question {QuestionId} (from a prior attempt) — treating as already satisfied, not a failure.",
                    questionId);
                return;
            }

            logger.LogError("ViewTrade rejected file upload for question {QuestionId}: {Status} {Body}",
                questionId, response.StatusCode, body);
            throw new ViewTradeApiException(response.StatusCode, body);
        }
    }

    public async Task<ViewTradeSubmissionResult> FinalizeAsync(long applicationId, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/applications/submit", new { application_id = applicationId }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("ViewTrade rejected application submission for {ApplicationId}: {Status} {Body}",
                applicationId, response.StatusCode, body);
            throw new ViewTradeApiException(response.StatusCode, body);
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("ViewTrade ApplicationSubmit response (shape unconfirmed, logging raw): {Body}", raw);

        // account_number appears (as null pre-submission) on ApplicationCreate's
        // confirmed response shape — assuming submit populates it, alongside
        // the same top-level "status" field ApplicationCreate's nested
        // application object carries. Unconfirmed; see class-level doc.
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var app = doc.RootElement.TryGetProperty("application", out var nested) ? nested : doc.RootElement;
        var accountRef = app.TryGetProperty("account_number", out var acct) && acct.ValueKind != System.Text.Json.JsonValueKind.Null
            ? acct.GetString() ?? ""
            : "";
        var status = app.TryGetProperty("status", out var st) ? st.GetString() ?? "pending" : "pending";
        return new ViewTradeSubmissionResult(accountRef, status);
    }

    public async Task<BackgroundCheckReply> RunBackgroundCheckAsync(long applicationId, string? ipAddress, CancellationToken ct)
    {
        // success_redirect_url / failure_redirect_url are only for hosted-redirect providers; omitted on purpose.
        var response = await http.PostAsJsonAsync("api/applications/background-check", new { application_id = applicationId, ip_address = ipAddress }, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        // A failed background check is reported by ViewTrade as a failed status, possibly with a non-2xx HTTP code;
        // the body (status / global_id_status) is what matters, so parse it before deciding the call itself failed.
        var reply = BackgroundCheckReply.Parse(raw);
        if (!response.IsSuccessStatusCode && reply.Status is null && reply.GlobalIdStatus is null)
        {
            logger.LogWarning("ViewTrade background-check for application {ApplicationId} returned {Status} with no readable body", applicationId, (int)response.StatusCode);
            throw new ViewTradeApiException(response.StatusCode, raw);
        }
        // Only the two non-sensitive status fields are logged, never the body.
        logger.LogInformation("ViewTrade background-check for application {ApplicationId}: http {Http}, status {Status}, global_id_status {GlobalId}",
            applicationId, (int)response.StatusCode, reply.Status ?? "(none)", reply.GlobalIdStatus ?? "(none)");
        return reply;
    }

    public async Task VerifyDocumentsAsync(long applicationId, string? ipAddress, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(onboarding.VerifyDocumentsPath, new { application_id = applicationId, applicant_type = "primary", ip_address = ipAddress }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("ViewTrade verify-documents ({Path}) for application {ApplicationId} returned {Status}", onboarding.VerifyDocumentsPath, applicationId, (int)response.StatusCode);
            throw new ViewTradeApiException(response.StatusCode, body);
        }
        logger.LogInformation("ViewTrade verify-documents triggered for application {ApplicationId}", applicationId);
    }

    private sealed record ApplicationCreateResponse(bool Status, [property: JsonPropertyName("application")] ApplicationCreatePayload Application);
    private sealed record ApplicationCreatePayload(long Id);
}
