using System.Security.Cryptography;
using Strg.Core.Services;

namespace Strg.Infrastructure.Services;

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    // OWASP 2023 PBKDF2-HMAC-SHA256 guidance: 600,000 iterations.
    // Matches ASP.NET Core Identity V3 default. Raising later requires a rehash-on-next-login shim.
    private const int Iterations = 600_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    // Materialized exactly once per process via the singleton DI registration. UserManager etc.
    // pass this to Verify() on missing-user / locked-account paths so the wall-clock cost matches
    // the existing-user wrong-password path. Initializing per-request (e.g. via a Lazy<string>
    // inside a scoped service) would cost a Hash() round on first miss per request, doubling
    // wall-clock relative to the verify-only path and re-introducing the very timing oracle the
    // canary exists to defeat.
    public string CanaryHash { get; } =
        ComputeCanary();

    private static string ComputeCanary()
    {
        var seed = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(seed, salt, Iterations, Algorithm, KeySize);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        byte[] salt, storedKey;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            storedKey = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);
        return CryptographicOperations.FixedTimeEquals(key, storedKey);
    }
}
