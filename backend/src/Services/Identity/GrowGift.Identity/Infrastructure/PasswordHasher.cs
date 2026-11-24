using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace GrowGift.Identity.Infrastructure;

/// <summary>
/// Argon2id with OWASP-recommended parameters (19 MiB, 2 iterations, parallelism 1).
/// Format: argon2id$iterations$memoryKb$saltB64$hashB64
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 2;
    private const int MemoryKb = 19_456;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt);
        return $"argon2id${Iterations}${MemoryKb}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5) return false;
        var salt = Convert.FromBase64String(parts[3]);
        var expected = Convert.FromBase64String(parts[4]);
        var actual = Compute(password, salt);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = Iterations,
            MemorySize = MemoryKb,
            DegreeOfParallelism = 1
        };
        return argon2.GetBytes(HashBytes);
    }
}
