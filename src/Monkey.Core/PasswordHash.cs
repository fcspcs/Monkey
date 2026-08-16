using System.Security.Cryptography;

namespace Monkey.Core;

/// <summary>
/// PBKDF2-SHA256. Das Klartextpasswort wird nie gespeichert, und die Pruefung
/// findet ausschliesslich im Dienst statt - nicht im Agent, der sonst gepatcht
/// werden koennte.
/// </summary>
public static class PasswordHash
{
    public const int MinimumLength = 10;
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt, int Iterations) Create(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string? hash, string? salt, int iterations)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt) || iterations <= 0)
            return false;

        byte[] expected, saltBytes;
        try
        {
            expected = Convert.FromBase64String(hash);
            saltBytes = Convert.FromBase64String(salt);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
