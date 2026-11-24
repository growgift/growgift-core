using System.Security.Claims;
using GrowGift.Brokerage.Domain;
using GrowGift.Brokerage.Infrastructure.Persistence;
using GrowGift.Brokerage.Infrastructure.ViewTrade;
using GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;
using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Brokerage.Api;

public static class OnboardingEndpoints
{
    /// <summary>
    /// Answers the backend guarantees regardless of what the frontend
    /// collects, for questions where GrowGift's product has exactly one
    /// legitimate answer and no UI to collect anything else meaningfully:
    /// - application.trading-authorization: ViewTrade's own config has no
    ///   registered option_id for "Yes" at all — there is no valid answer
    ///   other than "No" to send even if a UI asked.
    /// - application.trusted-contact.opt-in: answering "Yes" requires
    ///   ViewTrade's name/email follow-up questions, which nothing in the
    ///   onboarding UI collects.
    /// Kept out of AUTO_ANSWERS on the frontend deliberately — these aren't
    /// product defaults a UI could someday expose, they're answers with no
    /// other valid value given ViewTrade's actual contract, so they belong
    /// with the code that talks to ViewTrade rather than the form.
    /// </summary>
    private static readonly Dictionary<string, string> BackendOwnedAnswers = new()
    {
        ["application.trading-authorization"] = "No",
        ["application.trusted-contact.opt-in"] = "No",
    };

    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/onboarding").RequireAuthorization("Applicant");

        v1.MapGet("/", (ClaimsPrincipal user, BrokerageDbContext db, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => GetOrStart(user, db, ct))).WithSummary("Get (or start) the caller's onboarding application");

        v1.MapGet("/countries", (IViewTradeCountriesClient countries, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => GetCountries(countries, ct))).WithSummary("List countries for the form's country-type questions");

        v1.MapPut("/answers", (List<AnswerDto> body, ClaimsPrincipal user, BrokerageDbContext db, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => SaveAnswers(body, user, db, ct))).WithSummary("Save a batch of answers for the current step");

        v1.MapPost("/documents/{systemIdentifier}", (string systemIdentifier, IFormFile file, ClaimsPrincipal user,
                BrokerageDbContext db, IWebHostEnvironment env, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => UploadDocument(systemIdentifier, file, user, db, env, ct)))
            .WithSummary("Upload a required document/photo/signature")
            // Minimal APIs auto-attach antiforgery metadata to any endpoint
            // with an IFormFile parameter (CSRF protection for cookie-based
            // browser sessions). This API is stateless JWT Bearer auth with
            // no cookies involved, so that protection doesn't apply here —
            // and since the antiforgery middleware was never configured,
            // leaving the metadata on threw InvalidOperationException on
            // every upload instead of silently doing nothing.
            .DisableAntiforgery();

        v1.MapPost("/resolve-existing", (ClaimsPrincipal user, ExistingAccountResolver resolver, ILogger<Program> logger, CancellationToken ct) =>
            ResolveExisting(user, resolver, logger, ct))
            .WithSummary("Before onboarding: is the caller already a fully onboarded ViewTrade user? (verified e-mail only)");

        v1.MapPost("/verify", (ClaimsPrincipal user, HttpContext http, BrokerageDbContext db, IViewTradeAnswerSubmitter submitter, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => Reverify(user, http, db, submitter, logger, ct)))
            .WithSummary("Re-run ViewTrade's background check (and document verification when required) for the submitted application");

        v1.MapPost("/submit", (ClaimsPrincipal user, HttpContext http, BrokerageDbContext db, ViewTradeQuestionConfigResolver resolver,
                IViewTradeAnswerSubmitter submitter, IEventPublisher publisher, ILogger<Program> logger, CancellationToken ct) =>
            Guarded(logger, () => Submit(user, http.Request.Headers["X-Forwarded-For"], http.Connection.RemoteIpAddress, db, resolver, submitter, publisher, logger, ct))).WithSummary("Finalize the application and submit it to ViewTrade");

        return app;
    }

    /// <summary>
    /// Identity inputs come ONLY from the caller's verified JWT — the request has no body, so there is
    /// no way to ask about someone else's e-mail. Any ViewTrade failure is reported as 503 "couldn't
    /// check" (retryable), never as "no account", so a flaky API can't start a duplicate application.
    /// </summary>
    internal static async Task<IResult> ResolveExisting(
        ClaimsPrincipal user, ExistingAccountResolver resolver, ILogger logger, CancellationToken ct)
    {
        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email) || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var userId))
            return Results.Json(new { error = new { code = "onboarding.no_identity", message = "We couldn't read your account details. Please sign in again." } }, statusCode: 401);

        var verified = string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            var r = await resolver.ResolveAsync(userId, email, verified, ct);
            return Results.Ok(new { decision = ToWire(r.Decision), accountRef = r.AccountRef });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // Safe diagnostics only: type + status, never response bodies / tokens / e-mail.
            logger.LogWarning("ViewTrade onboarding-status check failed for user {UserId}: {Kind} (status {Status}).",
                userId, e.GetType().Name, (e as ViewTradeApiException)?.StatusCode.ToString() ?? (e as GrowGift.Brokerage.Infrastructure.ViewTrade.Trading.ViewTradeSessionException)?.StatusCode?.ToString() ?? "n/a");
            return Results.Json(new { error = new { code = "onboarding.status_unavailable",
                message = "We couldn't check your brokerage status right now. Your gift is saved — please try again." } }, statusCode: 503);
        }
    }

    internal static string ToWire(ExistingAccountDecision d) => d switch
    {
        ExistingAccountDecision.StartOnboarding => "start_onboarding",
        ExistingAccountDecision.ContinueOnboarding => "continue_onboarding",
        ExistingAccountDecision.SkipOnboarding => "skip_onboarding",
        ExistingAccountDecision.PendingReview => "pending_review",
        ExistingAccountDecision.NotEligible => "not_eligible",
        ExistingAccountDecision.Ambiguous => "ambiguous",
        ExistingAccountDecision.EmailUnverified => "email_unverified",
        _ => "not_eligible"
    };

    /// <summary>Re-runs the two-step check for the caller's own submitted application (e.g. after Veriff results change in UAT).</summary>
    internal static async Task<IResult> Reverify(
        ClaimsPrincipal user, HttpContext http, BrokerageDbContext db, IViewTradeAnswerSubmitter submitter, ILogger logger, CancellationToken ct)
    {
        var userId = UserId(user);
        var application = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (application?.ViewTradeApplicationId is null || application.Status != OnboardingStatus.Pending)
            return Results.Json(new { error = new { code = "onboarding.nothing_to_verify", message = "There is no submitted application waiting for verification." } }, statusCode: 409);
        var verification = await BackgroundCheckRunner.RunAsync(submitter, application.ViewTradeApplicationId.Value, ClientIp.From(http.Request.Headers["X-Forwarded-For"], http.Connection.RemoteIpAddress), logger, ct);
        return Results.Ok(new { backgroundCheck = verification });
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<OnboardingApplication> GetOrCreateAsync(BrokerageDbContext db, Guid userId, CancellationToken ct)
    {
        var app = await db.OnboardingApplications.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (app is not null) return app;
        app = OnboardingApplication.Start(userId);
        db.OnboardingApplications.Add(app);
        await db.SaveChangesAsync(ct);
        return app;
    }

    /// <summary>
    /// Every handler below is wrapped with this so a DB or unexpected error
    /// always comes back as JSON the frontend can read ({error:{code,message}})
    /// instead of a bare 500 with no body — which the browser reports simply
    /// as "Internal Server Error" with nothing to go on.
    /// </summary>
    private static async Task<IResult> Guarded(ILogger logger, Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Onboarding endpoint failed");
            return Results.Json(new { error = new { code = "onboarding.unexpected_error", message = e.Message } }, statusCode: 500);
        }
    }

    private static async Task<IResult> GetOrStart(ClaimsPrincipal user, BrokerageDbContext db, CancellationToken ct)
    {
        var application = await GetOrCreateAsync(db, UserId(user), ct);
        return Results.Ok(new
        {
            status = application.Status.ToString(),
            answers = application.Answers,
            multiAnswers = application.MultiAnswers,
            documentsProvided = application.DocumentRefs.Keys,
            rejectionReason = application.RejectionReason,
            accountRef = application.ViewTradeAccountRef,
            viewTradeApplicationId = application.ViewTradeApplicationId,
        });
    }

    // id kept alongside code/name so the frontend has a stable React key even
    // though the value it actually submits back (in AnswerDto.Value) is the
    // "code" field — see ViewTradeCountriesClient's class-level doc for why.
    private static async Task<IResult> GetCountries(IViewTradeCountriesClient countries, CancellationToken ct)
    {
        var list = await countries.GetCountriesAsync(ct);
        return Results.Ok(list.Select(c => new { c.Id, c.Code, c.Name }));
    }

    public sealed record AnswerDto(string SystemIdentifier, string? Value, List<string>? Values);

    private static async Task<IResult> SaveAnswers(
        [FromBody] List<AnswerDto> body, ClaimsPrincipal user, BrokerageDbContext db, CancellationToken ct)
    {
        var application = await GetOrCreateAsync(db, UserId(user), ct);

        foreach (var a in body)
        {
            var result = a.Values is { Count: > 0 }
                ? application.SetMultiAnswer(a.SystemIdentifier, a.Values)
                : application.SetAnswer(a.SystemIdentifier, a.Value ?? "");
            if (result.IsFailure) return result.Error.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { message = "Saved.", saved = body.Count });
    }

    private static async Task<IResult> UploadDocument(
        string systemIdentifier, IFormFile file, ClaimsPrincipal user,
        BrokerageDbContext db, IWebHostEnvironment env, CancellationToken ct)
    {
        var application = await GetOrCreateAsync(db, UserId(user), ct);

        // Dev-simple storage on local disk keyed by application+identifier;
        // production should write to object storage (S3/Azure Blob) instead
        // and store the resulting key/URL here in place of a local path.
        var dir = Path.Combine(env.ContentRootPath, "App_Data", "onboarding-documents", application.Id.ToString());
        Directory.CreateDirectory(dir);
        var safeName = $"{systemIdentifier.Replace('.', '_')}{Path.GetExtension(file.FileName)}";
        var fullPath = Path.Combine(dir, safeName);
        await using (var stream = File.Create(fullPath))
            await file.CopyToAsync(stream, ct);

        var result = application.SetDocument(systemIdentifier, fullPath);
        if (result.IsFailure) return result.Error.ToProblem();

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { message = "Uploaded.", systemIdentifier });
    }

    /// <summary>
    /// Caller's own rule: any status other than "in_progress" means
    /// ViewTrade already has this application past the point GrowGift needs
    /// to drive it further — sync locally and return an IResult to send
    /// immediately, rather than attempting to resubmit an already-submitted
    /// application. Returns null when the application is still in_progress,
    /// meaning the caller should proceed with the normal submission flow.
    /// </summary>
    private static async Task<IResult?> TryResumeExistingAsync(
        ExistingViewTradeApplication existing, OnboardingApplication application, Guid userId,
        IEventPublisher publisher, BrokerageDbContext db, CancellationToken ct)
    {
        if (existing.Status == "in_progress") return null;

        application.SetViewTradeUmaUserId(existing.UmaUserId);
        application.SetViewTradeApplicationId(existing.ApplicationId);
        application.CompleteWithResult(existing.AccountRef ?? "", existing.Status);
        await publisher.EnqueueAsync(new AccountOpenedV1(userId, existing.AccountRef ?? "", existing.Status),
            partitionKey: userId.ToString(), ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = application.Status.ToString(), accountRef = existing.AccountRef ?? "" });
    }

    private static async Task<IResult> Submit(
        ClaimsPrincipal user, string? forwardedFor, System.Net.IPAddress? remoteIp, BrokerageDbContext db, ViewTradeQuestionConfigResolver resolver,
        IViewTradeAnswerSubmitter submitter, IEventPublisher publisher,
        ILogger<Program> logger, CancellationToken ct)
    {
        var userId = UserId(user);
        var clientIp = ClientIp.From(forwardedFor, remoteIp);
        var application = await GetOrCreateAsync(db, userId, ct);

        var begun = application.BeginSubmission();
        if (begun.IsFailure) return begun.Error.ToProblem();
        await db.SaveChangesAsync(ct);

        try
        {
            // Register the ViewTrade user (once — persisted immediately on
            // success, see SetViewTradeUmaUserId's doc) then create the
            // application (once). Reusing both on retry is essential: a
            // failure on CreateApplicationAsync or anything after it must
            // never cause a duplicate registration — and per
            // ViewTradeAnswerSubmitter's docs, there is no API-based way to
            // recover a user id from just an email after the fact, so not
            // losing it here is the only correct fix, not a nice-to-have.
            if (application.ViewTradeApplicationId is null)
            {
                long umaUserId;
                if (application.ViewTradeUmaUserId is not null)
                {
                    // A previous attempt already registered this applicant
                    // in ViewTrade; only CreateApplicationAsync (or
                    // something after it) failed. Reuse, don't re-register.
                    umaUserId = application.ViewTradeUmaUserId.Value;

                    // That previous attempt may also have gotten as far as
                    // creating an application before failing later still —
                    // check before creating a second one for the same user.
                    var existing = await submitter.FindApplicationForKnownUserAsync(umaUserId, ct);
                    if (existing is not null)
                    {
                        var earlyExit = await TryResumeExistingAsync(existing, application, userId, publisher, db, ct);
                        if (earlyExit is not null) return earlyExit;
                        application.SetViewTradeApplicationId(existing.ApplicationId);
                        await db.SaveChangesAsync(ct);
                    }
                }
                else
                {
                    var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email")
                        ?? throw new InvalidOperationException("Caller's token has no email claim.");
                    var registration = new ViewTradeUserRegistration(
                        FirstName: application.Answers.GetValueOrDefault("applicant.primary.name.first", ""),
                        LastName: application.Answers.GetValueOrDefault("applicant.primary.name.last", ""),
                        Email: email,
                        Phone: application.Answers.GetValueOrDefault("applicant.primary.phone.mobile", ""),
                        Username: email); // email is already guaranteed-unique in GrowGift; simplest stable ViewTrade username

                    umaUserId = await submitter.RegisterUserAsync(registration, ct);
                    application.SetViewTradeUmaUserId(umaUserId);
                    await db.SaveChangesAsync(ct); // persisted before CreateApplicationAsync is even attempted — see class doc
                }

                if (application.ViewTradeApplicationId is null)
                {
                    var applicationId = await submitter.CreateApplicationAsync(umaUserId, ct);
                    application.SetViewTradeApplicationId(applicationId);
                    await db.SaveChangesAsync(ct);
                }
            }

            var viewTradeApplicationId = application.ViewTradeApplicationId!.Value;

            // Every text/date/email/country/state/select/radio/checkbox
            // answer batches into ONE answer-multiple call — see
            // ViewTradeAnswerSubmitter's class doc for why this replaced
            // the old one-call-per-answer design. Merged with
            // BackendOwnedAnswers so those questions are always answered
            // even though nothing in the UI collects them; BackendOwnedAnswers
            // wins on the (currently impossible, since the UI no longer asks
            // either) chance both sources somehow disagree.
            var allAnswers = new Dictionary<string, string>(application.Answers);
            foreach (var (systemIdentifier, value) in BackendOwnedAnswers)
                allAnswers[systemIdentifier] = value;

            // applicant.primary.w8.treaty-country's question_label IS the
            // beneficial-owner residency certification ("I certify that
            // the beneficial owner is a resident of the country selected
            // below...") — there's no separate UI control for it, and no
            // separate answer to collect: the applicant's residency is
            // exactly what their home address country already represents.
            // Deriving it here (rather than asking a second time in the
            // form) guarantees the two can never disagree, which matters
            // since a mismatch would misstate the actual certification.
            if (allAnswers.GetValueOrDefault("applicant.primary.w8.special-rate.applicable") == "Yes"
                && allAnswers.TryGetValue("applicant.primary.address.home.country", out var homeCountryCode))
            {
                allAnswers["applicant.primary.w8.treaty-country"] = homeCountryCode;
            }

            var resolved = new List<ResolvedAnswer>();
            foreach (var (systemIdentifier, value) in allAnswers)
            {
                var question = resolver.Resolve(systemIdentifier, preferredOptionLabel: value);

                object data;
                if (question.TypeName == "date")
                {
                    // Confirmed via a real captured Postman example
                    // (question_id 27134, applicant.primary.dob):
                    // ViewTrade expects MM/DD/YYYY, not the ISO
                    // "YYYY-MM-DD" our HTML date input naturally produces.
                    // This was silently accepted either way (no validation
                    // error), which is exactly why DOB never actually
                    // updated on ViewTrade's dashboard despite submissions
                    // "succeeding" — a wrong-but-well-formed value isn't
                    // the same as a rejected one.
                    // Explicit format/culture — the frontend's HTML date
                    // input always emits ISO "yyyy-MM-dd" regardless of
                    // browser locale, but DateOnly.Parse without an exact
                    // format depends on the server's current culture, which
                    // will differ between this dev machine and wherever
                    // this eventually runs in AWS. ParseExact avoids that
                    // becoming a second, much harder to notice, silent
                    // wrong-format bug on top of the first one.
                    var parsedDate = DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    data = parsedDate.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    logger.LogInformation(
                        "Sending date answer for {SystemIdentifier} (question_id {QuestionId}): raw={Raw} converted={Converted}",
                        systemIdentifier, question.QuestionId, value, data);
                }
                else if (question.Options is { Count: > 0 })
                {
                    var optionId = resolver.ResolveOptionId(systemIdentifier, value);
                    // Confirmed via real captured examples: "checkbox" type
                    // questions use the multi-select wrapped array shape
                    // ([{"data": "id"}]) even when only one option is being
                    // sent — e.g. application.acknowledgment.tax-form-information
                    // (question_id 27128) is type_name "checkbox" with only
                    // one valid option, yet still needs [{"data":"27053"}],
                    // not a bare "27053" string. "radio" type questions use
                    // the bare string. Sending a checkbox question's answer
                    // as a bare string is accepted with no error but never
                    // actually registers — this was the root cause of
                    // tax-form-information staying unchecked on the
                    // dashboard despite no error ever surfacing.
                    data = question.TypeName == "checkbox"
                        ? new object[] { new { data = optionId.ToString() } }
                        : optionId.ToString();
                }
                else
                {
                    data = value;
                }
                resolved.Add(new ResolvedAnswer(question.QuestionId, data));
            }
            foreach (var (systemIdentifier, labels) in application.MultiAnswers)
            {
                var question = resolver.Resolve(systemIdentifier);
                // ViewTrade's multi-select shape wraps each option id (also
                // a string): [{"data": "27056"}, ...].
                var wrapped = labels
                    .Select(l => resolver.ResolveOptionId(systemIdentifier, l).ToString())
                    .Select(optionId => new { data = optionId })
                    .ToArray();
                resolved.Add(new ResolvedAnswer(question.QuestionId, wrapped));
            }
            // Diagnostic: phone.home was rejected as "required" even after
            // confirming it was filled in and (per the conditional
            // showIf) should have been visible/saved — logging exactly
            // what ended up in the batch to see whether it's a save-time
            // issue (missing from allAnswers entirely) or something else
            // (present here but still rejected downstream).
            logger.LogInformation(
                "Submitting {Count} answers for application {ApplicationId}: {Identifiers}. phone.home present: {HasPhoneHome} (value: {PhoneHomeValue})",
                resolved.Count, viewTradeApplicationId, string.Join(", ", allAnswers.Keys),
                allAnswers.ContainsKey("applicant.primary.phone.home"),
                allAnswers.GetValueOrDefault("applicant.primary.phone.home", "(not present)"));

            await submitter.SubmitAnswersAsync(viewTradeApplicationId, resolved, ct);

            // Documents / photos / signatures — one file-upload call each,
            // skipping anything ViewTrade already confirmed on a prior
            // attempt (see MarkDocumentUploadedToViewTrade's doc for why
            // this is required, not just an optimization).
            foreach (var (systemIdentifier, path) in application.DocumentRefs)
            {
                var contentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".pdf" => "application/pdf",
                    _ => "application/octet-stream"
                };

                if (!application.ViewTradeUploadedDocuments.Contains(systemIdentifier))
                {
                    var question = resolver.Resolve(systemIdentifier);
                    await using (var stream = File.OpenRead(path))
                    {
                        await submitter.SubmitFileAsync(viewTradeApplicationId, question.QuestionId,
                            stream, Path.GetFileName(path), contentType, ct);
                    }
                    application.MarkDocumentUploadedToViewTrade(systemIdentifier);
                    await db.SaveChangesAsync(ct); // persist immediately — a later document's failure must not re-trigger this one
                }

                // applicant.primary.w8.signature is its own question
                // (type_name "file" — confirmed via a 406 rejecting it when
                // sent as a text answer through answer-multiple: "File
                // upload for the multiple answer endpoint is not allowed").
                // There's no separate UI step asking for a second signature
                // upload just for the W-8 form — the same signature photo
                // already collected for identity verification is submitted
                // again under this question's id, rather than making the
                // applicant upload their signature twice for what is
                // physically the same signature.
                //
                // Deliberately NOT nested inside the "if not already
                // uploaded" block above: this check needs to run every
                // iteration regardless of whether document.signature.image
                // itself was skipped as already-done, since that's a
                // SEPARATE identifier with its own independent tracking.
                // The original version had this nested inside a `continue`
                // for the outer identifier, which meant once
                // document.signature.image got marked uploaded, this block
                // could never run at all — confirmed bug: the reuse upload
                // to section 3009 (W8Certification) never happened even
                // though section 3018 (Signatures) succeeded.
                if (systemIdentifier == "applicant.primary.document.signature.image"
                    && !application.ViewTradeUploadedDocuments.Contains("applicant.primary.w8.signature"))
                {
                    var w8Question = resolver.Resolve("applicant.primary.w8.signature");
                    await using (var w8Stream = File.OpenRead(path))
                    {
                        await submitter.SubmitFileAsync(viewTradeApplicationId, w8Question.QuestionId,
                            w8Stream, Path.GetFileName(path), contentType, ct);
                    }
                    application.MarkDocumentUploadedToViewTrade("applicant.primary.w8.signature");
                    await db.SaveChangesAsync(ct);
                }
            }

            var result = await submitter.FinalizeAsync(viewTradeApplicationId, ct);
            application.CompleteWithResult(result.AccountRef, result.Status);

            await publisher.EnqueueAsync(new AccountOpenedV1(userId, result.AccountRef, result.Status),
                partitionKey: userId.ToString(), ct);
            await db.SaveChangesAsync(ct); // application status + outbox message commit together

            // ViewTrade's two-step background check. The application is already submitted and saved, so a problem
            // here is reported (and can be re-run with POST /onboarding/verify), never allowed to undo the submission.
            var verification = await BackgroundCheckRunner.RunAsync(submitter, viewTradeApplicationId, clientIp, logger, ct);
            return Results.Ok(new { status = application.Status.ToString(), accountRef = result.AccountRef, backgroundCheck = verification });
        }
        // ViewTrade error 4090 ("User with such username or email already
        // exists") from the user-registration call. The generic handler below
        // tells the applicant their answers are saved and to retry — which is
        // actively misleading here: nothing is saved on the ViewTrade side
        // until registration succeeds, so every retry fails identically until
        // the account's email changes. Common in a sandbox, where ViewTrade
        // keeps every user ever registered even after our own database was
        // reset and forgot the mapping; and in production, for anyone whose
        // email is already known to ViewTrade through another route.
        catch (ViewTradeApiException e) when (
            e.StatusCode == System.Net.HttpStatusCode.Conflict
            && e.ResponseBody.Contains("4090", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "ViewTrade already has a user registered under the applicant's email — user {UserId}. Retrying cannot succeed with this account's email.",
                userId);
            application.Fail(e.Message);
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                error = new
                {
                    code = "onboarding.viewtrade_user_exists",
                    message = "ViewTrade already has an account registered with this email address, so we can't open a new application under it. Sign out and sign up with a different email address — or contact support if this is your own existing ViewTrade account.",
                    detail = e.Message
                }
            }, statusCode: 409);
        }
        catch (Exception e)
        {
            logger.LogError(e, "ViewTrade onboarding submission failed for user {UserId}", userId);
            application.Fail(e.Message);
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                error = new
                {
                    code = "onboarding.submission_failed",
                    message = "ViewTrade couldn't accept the application. Your answers are saved — this can be retried.",
                    detail = e.Message
                }
            }, statusCode: 502);
        }
    }

    private static IResult ToProblem(this Error error) => error.Type switch
    {
        ErrorType.Validation => Results.BadRequest(new { error = new { code = error.Code, message = error.Message } }),
        ErrorType.Conflict => Results.Conflict(new { error = new { code = error.Code, message = error.Message } }),
        _ => Results.Json(new { error = new { code = error.Code, message = error.Message } }, statusCode: 500)
    };
}
