using System.Text.Json;

namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

/// <summary>
/// Resolves GrowGift's stable System Identifiers (e.g.
/// "applicant.primary.name.first") to ViewTrade's environment-specific
/// question_id / option_id values, using the exported form config as the
/// source of truth. Never hardcode a question_id or option_id in calling
/// code — always go through this resolver.
///
/// DISAMBIGUATION — IMPORTANT DEVIATION FROM THE ORIGINAL SPEC:
/// The integration doc recommends disambiguating duplicate identifiers by
/// (system_identifier, section_id). Inspecting the actual exported config
/// shows this doesn't work: both duplicate pairs
/// (applicant.primary.document.id.social-security-card.number, and
/// application.trading-authorization) share the SAME section_id between
/// their two entries — there is no field in this static export that tells
/// us which branch applies. See the two TODOs below for how each is
/// currently handled, and confirm both against a live sandbox submission
/// before this goes anywhere near production:
///
///   - application.trading-authorization: the two entries differ in their
///     OPTION SETS (one offers Yes/No, the other only No), so resolution
///     can pick the correct entry by checking which one actually has the
///     option label being submitted. Verified safe for both Yes and No.
///
///   - applicant.primary.document.id.social-security-card.number: a text
///     field with no options to disambiguate by. There is genuinely no
///     signal in this file to tell the two question_ids apart. We default
///     to the lower question_id (27169) and log a warning every time this
///     path is hit. TODO: confirm with ViewTrade (or via a live
///     SectionBySectionID call, which reflects current conditional state)
///     whether 27169 or 27171 is the branch that actually applies once a
///     "Do you have a US Tax ID Number?" answer has been given.
/// </summary>
public sealed class ViewTradeQuestionConfigResolver
{
    private readonly ILogger<ViewTradeQuestionConfigResolver> _logger;
    private readonly Dictionary<string, List<QuestionConfigEntry>> _byIdentifier;

    public ViewTradeQuestionConfigResolver(ILogger<ViewTradeQuestionConfigResolver> logger, string? configPath = null)
    {
        _logger = logger;
        var path = configPath ?? Path.Combine(AppContext.BaseDirectory,
            "Infrastructure", "ViewTrade", "Onboarding", "viewtrade_question_config.json");
        // Fall back to the source-relative path in dev (AppContext.BaseDirectory
        // is the build output dir, which only has this file if CopyToOutputDirectory
        // is set on the csproj — see the accompanying .csproj change).
        if (!File.Exists(path))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, "viewtrade_question_config.json");
            if (File.Exists(alt)) path = alt;
        }

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<QuestionConfigEntry>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }) ?? [];

        _byIdentifier = entries
            .GroupBy(e => e.SystemIdentifier)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.QuestionId).ToList());

        var ambiguous = _byIdentifier.Where(kv => kv.Value.Count > 1).ToList();
        if (ambiguous.Count > 0)
        {
            _logger.LogWarning(
                "ViewTrade question config has {Count} ambiguous system identifiers (multiple question_ids, " +
                "no disambiguating field): {Identifiers}. See ViewTradeQuestionConfigResolver's class-level docs.",
                ambiguous.Count, string.Join(", ", ambiguous.Select(a => a.Key)));
        }
    }

    /// <summary>Total questions loaded — useful for a startup sanity check/log line.</summary>
    public int QuestionCount => _byIdentifier.Values.Sum(v => v.Count);

    /// <summary>
    /// Resolves a system identifier to its live question. When an identifier
    /// has multiple candidates and <paramref name="preferredOptionLabel"/> is
    /// supplied, prefers the candidate whose options actually contain that
    /// label; otherwise (or if no candidate matches) falls back to the
    /// lowest question_id and logs a warning, since that's a guess.
    /// </summary>
    public QuestionConfigEntry Resolve(string systemIdentifier, string? preferredOptionLabel = null)
    {
        if (!_byIdentifier.TryGetValue(systemIdentifier, out var candidates) || candidates.Count == 0)
            throw new ViewTradeQuestionNotFoundException(systemIdentifier);

        if (candidates.Count == 1) return candidates[0];

        if (preferredOptionLabel is not null)
        {
            var match = candidates.FirstOrDefault(c =>
                c.Options?.Any(o => string.Equals(o.Label, preferredOptionLabel, StringComparison.OrdinalIgnoreCase)) == true);
            if (match is not null) return match;
        }

        _logger.LogWarning(
            "Resolving ambiguous system identifier {Identifier} with no disambiguating signal — " +
            "defaulting to question_id {QuestionId} of {Count} candidates. Verify against a live sandbox submission.",
            systemIdentifier, candidates[0].QuestionId, candidates.Count);
        return candidates[0];
    }

    /// <summary>Resolves a choice-type answer's option label to its live option_id.</summary>
    public int ResolveOptionId(string systemIdentifier, string optionLabel)
    {
        var question = Resolve(systemIdentifier, optionLabel);
        var option = question.Options?.FirstOrDefault(o =>
            string.Equals(o.Label, optionLabel, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            throw new ViewTradeOptionNotFoundException(systemIdentifier, optionLabel,
                question.Options?.Select(o => o.Label) ?? []);
        return option.OptionId;
    }

    public bool TryGetDefinition(string systemIdentifier, out QuestionConfigEntry? entry)
    {
        entry = _byIdentifier.TryGetValue(systemIdentifier, out var candidates) ? candidates[0] : null;
        return entry is not null;
    }
}

public sealed class ViewTradeQuestionNotFoundException(string systemIdentifier)
    : Exception($"No ViewTrade question found for system identifier '{systemIdentifier}'. " +
                 "Check viewtrade_question_config.json is current for this form version.");

public sealed class ViewTradeOptionNotFoundException(string systemIdentifier, string requestedLabel, IEnumerable<string> available)
    : Exception($"No option labeled '{requestedLabel}' for '{systemIdentifier}'. " +
                 $"Available: {string.Join(", ", available)}");
