using System.Security.Cryptography;
using System.Text;

namespace FamilyDnsLock.Core;

public sealed record PinVerificationResult(bool IsSuccess, bool IsLockedOut, TimeSpan? RetryAfter, string Message);

public sealed class PinSecurityManager
{
    private const int SaltSizeBytes = 32;
    private const int HashSizeBytes = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    public static (string HashBase64, string SaltBase64) HashPin(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        if (pin.Length < 4 || pin.Length > 64)
        {
            throw new ArgumentException("Secret must be between 4 and 64 characters.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            Iterations,
            HashAlgorithm,
            HashSizeBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPin(string pin, string hashBase64, string saltBase64)
    {
        if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(hashBase64) || string.IsNullOrWhiteSpace(saltBase64))
        {
            return false;
        }

        try
        {
            var expectedHash = Convert.FromBase64String(hashBase64);
            var salt = Convert.FromBase64String(saltBase64);

            var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin),
                salt,
                Iterations,
                HashAlgorithm,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    public static string GenerateRecoveryKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Base32 without ambiguous 0,O,1,I
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder("FDNS-", 21);
        for (var i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append('-');
            }
            sb.Append(alphabet[bytes[i] % alphabet.Length]);
        }
        return sb.ToString();
    }

    public static TimeSpan GetLockoutDelay(int failedAttempts) => failedAttempts switch
    {
        < 3 => TimeSpan.Zero,
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(30),
        5 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10)
    };
}
