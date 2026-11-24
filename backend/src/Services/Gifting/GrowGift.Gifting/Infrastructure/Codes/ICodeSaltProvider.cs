namespace GrowGift.Gifting.Infrastructure.Codes;

/// <summary>
/// The code-hash salt (a "pepper") lives in AWS Secrets Manager, injected via
/// environment at deploy time and rotated with dual-read support: hashes are
/// verified against current and previous salts during a rotation window.
/// </summary>
public interface ICodeSaltProvider
{
    string CurrentSalt { get; }
    IReadOnlyList<string> AcceptedSalts { get; }
}

public sealed class EnvironmentCodeSaltProvider : ICodeSaltProvider
{
    public EnvironmentCodeSaltProvider()
    {
        CurrentSalt = Environment.GetEnvironmentVariable("GG_CODE_PEPPER")
            ?? throw new InvalidOperationException("GG_CODE_PEPPER is not configured.");
        var previous = Environment.GetEnvironmentVariable("GG_CODE_PEPPER_PREVIOUS");
        AcceptedSalts = previous is null ? [CurrentSalt] : [CurrentSalt, previous];
    }

    public string CurrentSalt { get; }
    public IReadOnlyList<string> AcceptedSalts { get; }
}
