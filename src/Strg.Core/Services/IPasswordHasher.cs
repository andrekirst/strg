namespace Strg.Core.Services;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);

    /// <summary>
    /// A pre-computed hash of throwaway material, materialized exactly once per process.
    /// Callers that need to defeat timing oracles on missing-user / locked-account login paths
    /// pass this to <see cref="Verify"/> so the wall-clock cost matches the existing-user
    /// wrong-password path. Treat as opaque: do not parse, log, or persist.
    /// </summary>
    string CanaryHash { get; }
}
