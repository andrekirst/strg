# Plugin System

## Design Goals

- **Stable contracts**: Plugin interfaces in `Strg.Core` are versioned and never changed within a major version
- **Isolation**: Each plugin loads in its own `AssemblyLoadContext` to prevent version conflicts
- **Discoverability**: A plugin marketplace (NuGet-compatible registry) enables browse, install, and update
- **Zero-restart in v0.2+**: Plugins loaded from a shared volume are hot-reloaded without pod restart (v0.2 feature)

---

## Plugin Contract Interfaces

All interfaces live in `Strg.Core.Plugins`. They are the public API surface that must remain stable.

```csharp
// Base interface all plugins implement
public interface IStrgPlugin
{
    string Name { get; }
    string Version { get; }
    string Description { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void ConfigureEndpoints(IEndpointRouteBuilder routes);
    Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;
    Task OnStopAsync(CancellationToken ct) => Task.CompletedTask;
}

// Storage backend (adds a new drive provider type)
public interface IStorageProvider
{
    string ProviderType { get; }
    Task<IStorageFile> GetFileAsync(string path, CancellationToken ct);
    Task<Stream> ReadAsync(string path, long offset, CancellationToken ct);
    Task WriteAsync(string path, Stream content, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<IStorageDirectory> ListAsync(string path, CancellationToken ct);
    Task MoveAsync(string source, string destination, CancellationToken ct);
    Task<bool> ExistsAsync(string path, CancellationToken ct);
}

// Auth connector (adds a new upstream identity provider)
public interface IAuthConnector
{
    string ConnectorType { get; }  // 'ldap', 'saml', 'kerberos', ...
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
    Task<IEnumerable<string>> GetGroupsAsync(string userId, CancellationToken ct);
    void ConfigureOpenIddict(OpenIddictBuilder builder);
}

// Search provider (adds a new search engine backend)
public interface ISearchProvider
{
    string ProviderType { get; }
    Task IndexFileAsync(FileIndexEntry entry, CancellationToken ct);
    Task DeleteIndexAsync(Guid fileId, CancellationToken ct);
    Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct);
}

// API extension (adds new REST routes and/or GraphQL types)
public interface IEndpointModule : IStrgPlugin
{
    // ConfigureEndpoints inherited from IStrgPlugin
    void ConfigureGraphQL(ISchemaBuilder schemaBuilder);
    void ConfigureOpenApi(SwaggerGenOptions options);
}

// AI auto-tagger
public interface IAITagger : IStrgPlugin
{
    Task<IEnumerable<TagSuggestion>> SuggestTagsAsync(FileIndexEntry file, Stream content, CancellationToken ct);
}

// ActivityPub federation provider
public interface IFederationProvider : IStrgPlugin
{
    Task PublishActivityAsync(FederationActivity activity, CancellationToken ct);
    Task<bool> HandleInboxAsync(HttpContext context, CancellationToken ct);
}
```

---

## Plugin Manifest

Each plugin package includes a `strg-plugin.json` manifest:

```json
{
  "id": "strg-plugin-sftp",
  "version": "1.2.0",
  "name": "SFTP Storage Backend",
  "description": "Mount remote SFTP servers as strg drives",
  "author": "Community Contributor",
  "license": "MIT",
  "minStrgVersion": "0.2.0",
  "pluginTypes": ["IStorageProvider"],
  "assembly": "Strg.Plugin.Sftp.dll",
  "entryType": "Strg.Plugin.Sftp.SftpPlugin"
}
```

---

## Plugin Package Format

Plugins are distributed as `.strg-plugin` files — ZIP archives containing:

```
my-plugin.strg-plugin
├── strg-plugin.json    (manifest)
├── My.Plugin.dll       (main assembly)
├── My.Plugin.deps.json (dependency graph)
└── lib/                (additional assemblies)
    └── *.dll
```

The format is compatible with NuGet packaging (plugins can also be published as NuGet packages).

---

## Plugin Loading

```csharp
public class PluginLoader
{
    // Loads a plugin into an isolated AssemblyLoadContext
    public IStrgPlugin Load(string pluginPath);

    // Unloads a plugin and frees its AssemblyLoadContext
    public Task UnloadAsync(string pluginId);

    // Hot-reloads a plugin (unload + reload)
    public Task ReloadAsync(string pluginId);
}
```

Plugins are loaded from the `plugins/` directory adjacent to the binary, or from a configurable path in `appsettings.json`.

---

## Plugin Marketplace

The marketplace is a self-hostable NuGet-compatible feed. The official community registry will be hosted at `strg.dev/plugins`.

### Registry API (compatible with NuGet v3)

```
GET /v3/index.json           Discover feed endpoints
GET /v3/query?q=sftp         Search plugins
GET /v3/registration/{id}    Plugin metadata + versions
GET /v3/content/{id}/{ver}   Download plugin package
```

### Installing a plugin via API

```graphql
mutation {
  installPlugin(id: "strg-plugin-sftp", version: "latest") {
    id version status
  }
}
```

### Plugin Lifecycle States

```
discovered → installing → installed → active
                                   ↘ failed
               active → disabling → disabled
               active → updating → active
```

---

## Security Considerations

- Plugins run in-process. Trust is implicit — only install plugins from trusted sources.
- Plugin signatures (NuGet package signing) are validated before loading when signature verification is enabled.
- Plugin permissions are not sandboxed in v0.1 (same process, full CLR access). A capability model is planned for v2.x.
