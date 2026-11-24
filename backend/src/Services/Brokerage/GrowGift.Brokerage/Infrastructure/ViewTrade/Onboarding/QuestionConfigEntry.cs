namespace GrowGift.Brokerage.Infrastructure.ViewTrade.Onboarding;

public sealed record QuestionOption(int OptionId, string Label);

public sealed record QuestionConfigEntry(
    int QuestionId,
    string SystemIdentifier,
    int TypeId,
    string TypeName,
    int? SectionId,
    string? SectionName,
    string QuestionLabel,
    bool IsOptional,
    bool MultipleAnswers,
    IReadOnlyList<QuestionOption>? Options
);
