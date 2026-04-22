using System.Runtime.CompilerServices;
using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Architecture.Tests.Domain;

/// <summary>
/// Pins <see cref="TenantedEntity.TenantId"/> as init-only via reflection (STRG-066 audit
/// INFO-3). The forbidden-patterns block in CLAUDE.md calls out
/// <c>file.TenantId = differentTenant</c> as a tenant-isolation violation; init-only is the
/// type-system enforcement that makes that line a compile error rather than a runtime
/// surprise. A refactor that relaxes <c>{ init; }</c> to <c>{ set; }</c> silently turns off
/// the guard without touching any business code — this test fails loudly when that happens.
/// </summary>
public sealed class TenantedEntityShapeTests
{
    [Fact]
    public void TenantId_is_init_only()
    {
        var tenantIdProperty = typeof(TenantedEntity).GetProperty(nameof(TenantedEntity.TenantId));
        tenantIdProperty.Should().NotBeNull(
            "TenantedEntity.TenantId is load-bearing for tenant isolation and must exist");

        var setter = tenantIdProperty!.SetMethod;
        setter.Should().NotBeNull(
            "TenantId needs a setter — init-only setters are still emitted; the check " +
            "below distinguishes { init; } from { set; }");

        // Init-only setters are marked by the compiler with a modreq on System.Runtime.CompilerServices.IsExternalInit
        // on the return parameter. This is the load-bearing reflection check — the C# keyword
        // `init` surfaces as that modifier in metadata.
        var requiredModifiers = setter!.ReturnParameter.GetRequiredCustomModifiers();
        requiredModifiers.Should().Contain(
            typeof(IsExternalInit),
            because: "TenantId must be { init; } not { set; } — see CLAUDE.md multi-tenancy " +
                     "rules. A regression that drops the init-only modifier allows any code " +
                     "path to reassign TenantId after construction, defeating the tenant " +
                     "isolation global query filter.");
    }
}
