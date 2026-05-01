using Strg.Plugin.Abstractions.Storage;
using Strg.Plugin.Abstractions.Internal.Encryption;

namespace Strg.Infrastructure.Storage.Encryption;

/// <summary>
/// Default <see cref="IEncryptingFileWriterFactory"/> binding the only v0.1 algorithm
/// (<see cref="AesGcmFileWriter"/>). The <see cref="IKeyProvider"/> is resolved once at
/// factory construction (singleton lifetime in DI) and passed to every writer instance —
/// only the per-drive <see cref="IStorageProvider"/> varies per call.
///
/// <para>When a second algorithm lands, this is the seam where dispatch belongs: take an
/// algorithm hint on <see cref="Create"/> (or accept a discriminator on the drive) and
/// route to the matching writer impl. Until then, the AES-GCM writer is the only choice.</para>
/// </summary>
public sealed class AesGcmFileWriterFactory(IKeyProvider keyProvider) : IEncryptingFileWriterFactory
{
    public IEncryptingFileWriter Create(IStorageProvider provider)
        => new AesGcmFileWriter(provider, keyProvider);
}
