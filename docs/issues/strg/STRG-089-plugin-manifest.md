---
id: STRG-089
title: Define plugin manifest format and validation
milestone: v0.1
priority: low
status: open
type: implementation
labels: [plugins, infrastructure]
depends_on: [STRG-088]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-089: Define plugin manifest format and validation

## Summary

Define the `strg-plugin.json` manifest format that every plugin package must include. Implement manifest deserialization and validation. This is the metadata contract between the plugin author and the plugin loader.

Plugins are loaded **only when explicitly listed in configuration** — there is no automatic discovery from a plugin directory. The server will not load any plugin not present in the `Plugins` config array.

## Technical Specification

### File: `strg-plugin.json` (in plugin package root):

```json
{
  "id": "com.example.my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "description": "A sample plugin for strg",
  "author": "Example Corp",
  "minStrgVersion": "0.1.0",
  "maxStrgVersion": null,
  "entryPoint": "Strg.Plugin.Example.dll",
  "pluginType": "storage",
  "homepage": "https://example.com/my-plugin",
  "license": "MIT",
  "permissions": ["storage.read", "storage.write"]
}
```

### Plugin types:

| `pluginType` | Maps To |
|---|---|
| `storage` | `IStorageProvider` |
| `auth` | `IAuthConnector` |
| `search` | `ISearchProvider` |
| `endpoint` | `IEndpointModule` |
| `ai-tagger` | `IAITagger` |
| `federation` | `IFederationProvider` |
| `generic` | `IStrgPlugin` (multiple types) |

### File: `src/Strg.Core/Plugins/PluginManifest.cs`

```csharp
public sealed class PluginManifest
{
    [Required]
    public string Id { get; init; } = string.Empty; // reverse-DNS: "com.example.plugin"

    [Required]
    public string Name { get; init; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d+\.\d+\.\d+")]
    public string Version { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;

    [Required]
    public string MinStrgVersion { get; init; } = string.Empty;

    public string? MaxStrgVersion { get; init; }

    [Required]
    public string EntryPoint { get; init; } = string.Empty; // DLL filename

    [Required]
    public string PluginType { get; init; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; init; } = [];
}
```

### Explicit plugin config list:

Plugins are enabled via `appsettings.json`. The server reads this list at startup and loads only listed plugins:

```json
{
  "Plugins": [
    {
      "Id": "com.example.my-plugin",
      "Path": "/opt/strg/plugins/my-plugin/"
    }
  ]
}
```

If `Plugins` is empty or absent, no plugins are loaded. Plugin discovery from arbitrary directories is **not** supported.

### Validation:

```csharp
public static class PluginManifestValidator
{
    public static bool IsCompatible(PluginManifest manifest, string currentStrgVersion)
    {
        return SemVersion.Parse(currentStrgVersion) >= SemVersion.Parse(manifest.MinStrgVersion)
            && (manifest.MaxStrgVersion is null ||
                SemVersion.Parse(currentStrgVersion) <= SemVersion.Parse(manifest.MaxStrgVersion));
    }
}
```

### Permission enforcement at load time:

When the loader (v0.2) instantiates a plugin, it wraps it in `PermissionEnforcingPluginProxy` using the `permissions` list from the manifest. Any attempt by the plugin to register or resolve a service outside its declared permissions throws `PluginPermissionException` and prevents the plugin from loading.

## Acceptance Criteria

- [ ] Valid `strg-plugin.json` → `PluginManifest` deserialized correctly
- [ ] Missing required field → `JsonException` or validation error
- [ ] `minStrgVersion` check: plugin requiring v0.2 not loaded by v0.1 strg
- [ ] `pluginType` must be one of the known values
- [ ] `id` must match reverse-DNS format
- [ ] Only plugins listed in `"Plugins"` config array are loaded (no directory scan)

## Test Cases

- **TC-001**: Valid manifest → `PluginManifest` with all fields populated
- **TC-002**: `minStrgVersion: "0.2.0"` on strg `0.1.0` → `IsCompatible` returns false
- **TC-003**: Missing `entryPoint` → validation error
- **TC-004**: Unknown `pluginType: "magic"` → validation error
- **TC-005**: Plugin not in `Plugins` config list → not loaded even if directory present

## Implementation Tasks

- [ ] Create `PluginManifest.cs` in `Strg.Plugin.Abstractions/`
- [ ] Create `PluginManifestValidator.cs`
- [ ] Add SemVer comparison (use `Semver` NuGet package or simple string parse)
- [ ] Create `PluginConfig` record for config binding (`Id`, `Path`)
- [ ] Bind `"Plugins"` array from `IConfiguration` and validate at startup

## Testing Tasks

- [ ] Unit test: valid manifest deserialized
- [ ] Unit test: compatibility check
- [ ] Unit test: invalid manifest rejected

## Security Review Checklist

- [ ] `id` must be validated (no path characters — prevents path injection in plugin cache)
- [ ] `entryPoint` must be a filename only (no path components)

## Code Review Checklist

- [ ] `PluginManifest` is fully `init`-only (immutable after deserialization)
- [ ] SemVer comparison library used (not string comparison)

## Definition of Done

- [ ] Manifest format defined and validated
- [ ] Compatibility check implemented
