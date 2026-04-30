using System.Runtime.CompilerServices;
using Strg.Integration.Tests.Upload;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// Sets <c>STRG_SECURITY__ENCRYPTIONKEY</c> at module load so the production
/// <c>EnvVarKeyProvider</c> (resolved as a singleton inside <c>StrgWebApplicationFactory</c>'s
/// host) finds a 32-byte base64 KEK when first instantiated. Without this, every COPY/MOVE in
/// <see cref="WebDavMutationTests"/> 500s at handler construction even though the test drive
/// doesn't enable encryption — the Mediator handlers' DI graph constructor-injects
/// <c>IEncryptingFileWriterFactory</c> → <c>IKeyProvider</c> regardless.
///
/// <para><b>Why a module initializer.</b> Setting the env var inside the test class's static
/// ctor would run too late: xunit constructs <c>IClassFixture&lt;StrgWebApplicationFactory&gt;</c>
/// (and therefore the host) BEFORE the test class itself, and the host wires DI on first
/// request. <c>[ModuleInitializer]</c> runs at assembly load — before any xunit machinery.</para>
/// </summary>
internal static class WebDavMutationModuleInit
{
    [ModuleInitializer]
    internal static void EnsureTestKekSet()
    {
        // Idempotent: only set if absent. Reuse StrgTusUploadFixture's KEK so a future test
        // that exercises both fixtures in one assembly run can't drift between them.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STRG_SECURITY__ENCRYPTIONKEY")))
        {
            Environment.SetEnvironmentVariable("STRG_SECURITY__ENCRYPTIONKEY", StrgTusUploadFixture.TestKekBase64);
        }
    }
}
