using Strg.Plugin.Abstractions.Storage;

namespace Strg.Plugin.Abstractions.Internal.Encryption;

/// <summary>
/// Builds an <see cref="IEncryptingFileWriter"/> bound to a specific
/// <see cref="IStorageProvider"/>. Storage providers are resolved per-drive at request time
/// (each drive carries its own <c>ProviderType</c> + <c>ProviderConfig</c>), so the
/// writer cannot constructor-inject <see cref="IStorageProvider"/> via DI — there is no sensible
/// singleton or scoped instance. The factory closes that gap: callers resolve the provider
/// from <see cref="IStorageProviderRegistry"/> using the drive's config, then ask the
/// factory for a writer wrapping it.
///
/// <para><b>Why this is a port, not inline construction.</b> <c>AesGcmFileWriter</c>
/// (the only implementation today) lives in <c>Strg.Infrastructure</c>; <c>Strg.Application</c>
/// callers like <c>FileDownloadResolver</c> cannot reference it without violating the
/// dependency layering. The factory keeps the construction step on the infrastructure side
/// of the seam while application code stays portable.</para>
/// </summary>
public interface IEncryptingFileWriterFactory
{
    /// <summary>
    /// Returns a writer bound to <paramref name="provider"/>. The returned instance is
    /// short-lived — meant to be used for one read or write call and then discarded.
    /// </summary>
    IEncryptingFileWriter Create(IStorageProvider provider);
}
