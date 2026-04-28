namespace Strg.Core.Constants;

/// <summary>
/// Algorithm names persisted on <see cref="Domain.FileKey.Algorithm"/> and round-tripped through
/// the <see cref="Storage.IEncryptingFileWriter"/> port. Centralised so consumers across layers
/// (TUS upload pipeline in Strg.Infrastructure, cross-drive move handler in Strg.Application)
/// share one canonical literal — drift between the writer's <c>AesGcmFileWriter.AlgorithmName</c>
/// and a separately-typed FileKey row would silently route reads to <c>NotSupportedException</c>
/// at decrypt time, masking the actual misconfiguration.
///
/// <para>Per <c>feedback_shared_constants_layering.md</c>: drift-defense constants live at the
/// lowest common dependency. <see cref="AesGcm256"/> mirrors
/// <c>Strg.Infrastructure.Storage.Encryption.AesGcmFileWriter.AlgorithmName</c> — Strg.Core
/// cannot reference the writer (Core has no infrastructure deps), so the Application-layer
/// MoveFileHandler imports this constant for the P→E target-write algorithm pick. The writer
/// keeps its own <c>const string</c> so its body remains self-contained; an architecture test
/// (or unit assertion) can pin them to the same value.</para>
/// </summary>
public static class EncryptionAlgorithms
{
    /// <summary>
    /// AES-256-GCM with chunked streaming (the v0.1 default — see <see cref="Domain.FileKey.Algorithm"/>'s
    /// default initializer and the Infrastructure-layer <c>AesGcmFileWriter</c>'s envelope shape).
    /// </summary>
    public const string AesGcm256 = "AES-256-GCM";
}
