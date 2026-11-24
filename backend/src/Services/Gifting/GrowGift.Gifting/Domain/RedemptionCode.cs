using System.Security.Cryptography;
using System.Text;
using GrowGift.SharedKernel.Domain;

namespace GrowGift.Gifting.Domain;

/// <summary>
/// Redemption codes are generated from a CSPRNG over a Crockford-style
/// alphabet (no 0/O/1/I ambiguity), formatted GG-XXXX-XXXX-XXXX, and stored
/// only as a salted SHA-256 hash. The plaintext exists exactly twice: printed
/// in the gift box and (optionally) sent in the gift email — never in our
/// database or logs. ~5.9e17 combinations at 12 characters over a 30-symbol
/// alphabet, combined with a 10-attempt lockout, makes online guessing
/// infeasible.
/// </summary>
public sealed class RedemptionCode : ValueObject
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int GroupCount = 3;
    private const int GroupLength = 4;

    public string HashHex { get; }

    private RedemptionCode(string hashHex) => HashHex = hashHex;

    public static (RedemptionCode Code, string Plaintext) Generate(string salt)
    {
        var chars = new char[GroupCount * GroupLength];
        Span<byte> buffer = stackalloc byte[chars.Length];
        RandomNumberGenerator.Fill(buffer);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[buffer[i] % Alphabet.Length];

        var groups = Enumerable.Range(0, GroupCount)
            .Select(g => new string(chars, g * GroupLength, GroupLength));
        var plaintext = "GG-" + string.Join('-', groups);

        return (FromPlaintext(plaintext, salt), plaintext);
    }

    public static RedemptionCode FromPlaintext(string plaintext, string salt)
    {
        var normalized = Normalize(plaintext);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + normalized));
        return new RedemptionCode(Convert.ToHexString(bytes));
    }

    /// <summary>Forgiving input: strip separators, fold lookalike glyphs.</summary>
    private static string Normalize(string input) =>
        input.Trim().ToUpperInvariant()
            .Replace(" ", "").Replace("-", "")
            .Replace("O", "0").Replace("I", "1").Replace("L", "1");

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return HashHex;
    }
}
