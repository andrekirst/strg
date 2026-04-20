---
id: STRG-088
title: Define plugin contract interfaces in Strg.Core
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [plugins, infrastructure]
depends_on: [STRG-003]
blocks: [STRG-089]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-088: Define plugin contract interfaces in Strg.Core

## Summary

Define all plugin contract interfaces in a dedicated `Strg.Plugin.Abstractions` NuGet package (no strg-internal dependencies). These interfaces are the stable public contract that plugin authors reference. The actual plugin loader (`Strg.Infrastructure/Plugins/`) uses `AssemblyLoadContext` for isolation and ships in v0.2; the interfaces are defined in v0.1 to allow plugin authors to start building.

## Technical Specification

### File: `src/Strg.Core/Plugins/IStrgPlugin.cs`

```csharp
public interface IStrgPlugin
{
    string Name { get; }
    string Version { get; }
    string Description { get; }
    string Author { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void ConfigureEndpoints(IEndpointRouteBuilder routes);
    Task InitializeAsync(IServiceProvider services, CancellationToken ct)
        => Task.CompletedTask; // default no-op
}
```

### File: `src/Strg.Core/Plugins/IAuthConnector.cs`

```csharp
public interface IAuthConnector : IStrgPlugin
{
    string ConnectorType { get; } // "ldap", "saml", "oauth2"

    Task<AuthConnectorResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetGroupsAsync(
        string username,
        CancellationToken ct);
}

public record AuthConnectorResult(bool Success, string? ErrorMessage, ClaimsIdentity? Identity);
```

### File: `src/Strg.Core/Plugins/ISearchProvider.cs`

```csharp
public interface ISearchProvider : IStrgPlugin
{
    string ProviderType { get; } // "default-ef", "elasticsearch", "meilisearch"

    Task IndexAsync(Guid fileId, string textContent, CancellationToken ct);
    Task DeleteAsync(Guid fileId, CancellationToken ct);
    Task<SearchResult> SearchAsync(
        Guid tenantId,
        string query,
        SearchOptions options,
        CancellationToken ct);
}

public record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCount);
public record SearchHit(Guid FileId, string Snippet, float Score);
```

### File: `src/Strg.Core/Plugins/IEndpointModule.cs`

```csharp
public interface IEndpointModule : IStrgPlugin
{
    string MountPath { get; } // e.g., "/plugins/my-plugin"
}
```

### File: `src/Strg.Core/Plugins/IAITagger.cs`

```csharp
public interface IAITagger : IStrgPlugin
{
    Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        Guid fileId,
        Stream? content,
        string mimeType,
        CancellationToken ct);
}

public record TagSuggestion(string Key, string Value, float Confidence);
```

### File: `src/Strg.Core/Plugins/IFederationProvider.cs`

```csharp
public interface IFederationProvider : IStrgPlugin
{
    string Protocol { get; } // "activitypub"

    Task PublishActivityAsync(FederationActivity activity, CancellationToken ct);
    Task<FederationActivity?> ReceiveActivityAsync(
        HttpContext context, CancellationToken ct);
}
```

### Plugin isolation (loader, ships in v0.2):

Each plugin assembly is loaded into its own `AssemblyLoadContext` to prevent version conflicts and allow unloading:

```csharp
// Strg.Infrastructure/Plugins/PluginLoader.cs (v0.2)
var context = new AssemblyLoadContext(pluginId, isCollectible: true);
var assembly = context.LoadFromAssemblyPath(manifestPath);
var pluginType = assembly.GetTypes().Single(t => typeof(IStrgPlugin).IsAssignableFrom(t));
```

### Permission enforcement (proxy/decorator pattern):

Plugin permission declarations in the manifest are enforced at runtime via a proxy/decorator wrapping the plugin instance. If a plugin calls a service not declared in its `permissions` list, the proxy throws `PluginPermissionException`:

```csharp
// Strg.Infrastructure/Plugins/PermissionEnforcingPluginProxy.cs (v0.2)
public sealed class PermissionEnforcingPluginProxy(IStrgPlugin inner, IReadOnlySet<string> granted) : IStrgPlugin
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Wrap services collection to only allow registration of permitted interfaces
        inner.ConfigureServices(new RestrictedServiceCollection(services, granted), configuration);
    }
    // ...
}
```

## Acceptance Criteria

- [ ] All six interfaces (`IStrgPlugin`, `IStorageProvider`, `IAuthConnector`, `ISearchProvider`, `IEndpointModule`, `IAITagger`, `IFederationProvider`) defined in `Strg.Plugin.Abstractions`
- [ ] `Strg.Plugin.Abstractions` has no strg-internal dependencies (only Microsoft BCL + `Microsoft.AspNetCore.Http.Abstractions`)
- [ ] Each interface has XML doc comments explaining contract
- [ ] `IStrgPlugin.InitializeAsync` has a default no-op implementation
- [ ] All `CancellationToken` parameters present
- [ ] `AssemblyLoadContext` isolation design documented (loader in v0.2)
- [ ] Permission proxy/decorator design documented (enforcement in v0.2)

## Test Cases

- **TC-001**: `Strg.Plugin.Abstractions` compiles with no strg-internal references
- **TC-002**: `IStorageProvider` is implemented by `LocalFileSystemProvider` (from STRG-024)

## Implementation Tasks

- [ ] Create `Strg.Plugin.Abstractions` project under `src/`
- [ ] Create all interface files in `src/Strg.Plugin.Abstractions/`
- [ ] Add XML doc comments to each interface member
- [ ] Verify `Strg.Plugin.Abstractions` has no strg-internal NuGet references
- [ ] Add `// v0.2: AssemblyLoadContext loader` comment in plugin registration placeholder
- [ ] Add `// v0.2: PermissionEnforcingPluginProxy` comment in plugin DI placeholder

## Security Review Checklist

- [ ] `IAuthConnector` does not accept raw `ClaimsPrincipal` from plugin (plugin provides `ClaimsIdentity` that host validates)
- [ ] `IAITagger` receives content as `Stream?` — null means plugin cannot read content (opt-in)

## Code Review Checklist

- [ ] Interfaces are in `Strg.Core/Plugins/` namespace
- [ ] No `abstract class` — all interfaces (plugins choose their base class)
- [ ] Version in `IStrgPlugin.Version` is a SemVer string

## Definition of Done

- [ ] All plugin interfaces defined and compiled
- [ ] No external dependencies in `Strg.Core`
