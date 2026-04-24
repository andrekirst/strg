---
name: csharp-strg
description: Use when writing, reviewing, or modifying any C# code in the strg project. Enforces the project's C# conventions: full parameter names (no abbreviations), always-braces for control flow, .NET 10 target, `.slnx` solution, BCL/project constants instead of string literals.
---

# C# Coding Conventions for strg

This skill governs all C# code in `src/` and `tests/`. Rules are enforced via `.editorconfig` + `Directory.Build.props` (`EnforceCodeStyleInBuild`) + code review + this skill. Deviating requires a convention-level decision, never a one-off exception.

Microsoft's official C# coding conventions apply as the baseline: https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions

The rules below add project-specific conventions on top of that baseline.

---

## Non-Negotiable Rules

### 1. `CancellationToken` naming — full word, never abbreviated

```csharp
// CORRECT
public Task<User> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
public async Task SaveAsync(CancellationToken cancellationToken) { ... }

// WRONG
public Task<User> GetUserAsync(Guid id, CancellationToken ct = default)
public Task<User> GetUserAsync(Guid id, CancellationToken token)
```

Rationale: matches the .NET runtime/BCL source, matches Microsoft's coding conventions, removes ambiguity when tokens are passed around.

### 2. Braces for every control-flow body

```csharp
// CORRECT — braces even for single statements
if (user is null)
{
    return NotFound();
}

foreach (var item in items)
{
    Process(item);
}

using (var scope = _services.CreateScope())
{
    // ...
}

// WRONG
if (user is null) return NotFound();
if (user is null)
    return NotFound();
foreach (var item in items) Process(item);
```

Applies to `if`/`else if`/`else`/`for`/`foreach`/`while`/`do`/`using`/`lock`.

Enforced by `.editorconfig`: `csharp_prefer_braces = true:error` + `dotnet_diagnostic.IDE0011.severity = error`, build-gated by `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` in `Directory.Build.props`.

### 3. .NET 10 (LTS) is the only target

- `<TargetFramework>net10.0</TargetFramework>` lives centrally in `Directory.Build.props`.
- SDK is pinned via `global.json` (`10.0.x` with `rollForward: latestFeature`).
- Never target `net9.0` or lower, never add multi-targeting (`net8.0`, `netstandard2.1`, etc.).

### 4. Solution file is `.slnx`

The repo has `strg.slnx` only. Never reintroduce `strg.sln`. `dotnet`, Rider, and VS 17.10+ all support `.slnx` natively. If tooling complains, the tooling is out of date — don't regress the file format.

### 5. No magic strings — use BCL or project constants

For well-known strings, always use the library-provided constant:

| Literal | Replace with |
|---|---|
| `"application/octet-stream"`, `"application/json"`, … | `System.Net.Mime.MediaTypeNames.Application.*` |
| `"text/plain"`, `"text/html"`, … | `System.Net.Mime.MediaTypeNames.Text.*` |
| `"Content-Type"`, `"Authorization"`, `"Cache-Control"`, … | `Microsoft.Net.Http.Headers.HeaderNames.*` |
| `"sub"`, `"name"`, `"email"`, … | `System.Security.Claims.ClaimTypes.*` |
| `"GET"`, `"POST"`, … | `Microsoft.AspNetCore.Http.HttpMethods.*` |
| `"Bearer"` | `JwtBearerDefaults.AuthenticationScheme` |
| OpenIddict schemes | `OpenIddictServerAspNetCoreDefaults.AuthenticationScheme` |

```csharp
// CORRECT
using System.Net.Mime;
public string MimeType { get; set; } = MediaTypeNames.Application.Octet;

// WRONG
public string MimeType { get; set; } = "application/octet-stream";
```

For **project-specific** strings that repeat across files (a role name, a subscription topic, an outbox queue, etc.), add a `const string` to `Strg.Core/Constants/` and reference it. Do not duplicate the literal, and do not hoist single-use strings speculatively — the bar is "appears in two or more unrelated places."

---

## Additional Style Rules (enforced by `.editorconfig`)

- **File-scoped namespaces**: `namespace Strg.Core.Domain;` — never the block form.
- **`var` for built-in types and when the type is apparent**: `var user = new User();`
- **4-space indent, LF line endings, trailing newline** on every file.
- **`sealed` by default** for concrete classes — see the main `CLAUDE.md` `Class Structure` section.
- **No unused `using` directives.** Enforced at build via `IDE0005` (warning → error under `TreatWarningsAsErrors`). Rider/ReSharper's "Remove unused usings" is the canonical cleanup.
- **`await using` for `IAsyncDisposable` in async methods.** When the target implements `IAsyncDisposable` and the containing method is `async`, use `await using`. Example: `Utf8JsonWriter` exposes an async flush path via its `IAsyncDisposable` implementation, so `await using var writer = new Utf8JsonWriter(buffer, opts);` inside an async method — never plain `using`.
- **Null-conditional assignment** (C# 14) instead of `is not null` guards around a single property assignment:
  ```csharp
  // CORRECT (C# 14)
  drive?.DeletedAt = DateTimeOffset.UtcNow;

  // WRONG
  if (drive is not null)
  {
      drive.DeletedAt = DateTimeOffset.UtcNow;
  }
  ```
- **`const` over `static readonly`** when every component is already `const`. Example: `private const string JsonPath = $"/openapi/{DocumentName}.json";` when `DocumentName` is `const`.

### Primary constructors

Use a primary constructor when the constructor body is *any combination* of pure `_field = param` assignments, `: base(…)` delegation, or trivial field initializers derived from parameters — regardless of parameter count. Remove the `_field_` prefix at call sites and reference the parameter name directly. Applies to DI-heavy services, exception classes, stream wrappers that capture ctor args, test helper stubs, and xUnit test classes that receive `ITestOutputHelper`/fixtures.

```csharp
// CORRECT — DI service with primary ctor
public sealed class StrgErrorFilter(IHostEnvironment env, ILogger<StrgErrorFilter> logger) : IErrorFilter
{
    public IError OnError(IError error) => /* uses env, logger directly */;
}

// CORRECT — exception with parameter-backed property
public sealed class ValidationException(string message, string? propertyName = null)
    : Exception(message)
{
    public string? PropertyName { get; } = propertyName;
}

// CORRECT — DataLoader with base delegation
public sealed class DriveByIdDataLoader(
    IDbContextFactory<StrgDbContext> dbFactory,
    IBatchScheduler batchScheduler,
    DataLoaderOptions? options = null)
    : BatchDataLoader<Guid, Drive>(batchScheduler, options ?? new DataLoaderOptions())
{
    protected override async Task<IReadOnlyDictionary<Guid, Drive>> LoadBatchAsync(/*…*/)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        /* … */
    }
}

// CORRECT — xUnit class receiving helpers
public sealed class AuditLogConsumerTests(ITestOutputHelper output) : IAsyncLifetime { /* … */ }
```

Classic constructors are only kept when the body contains logic beyond trivial assignment/delegation (e.g., validation, branching, parameter transformation).

### Extension methods: C# 14 `extension(T)` block

When two or more extension methods target the same receiver type, group them into a single `extension(T x)` block rather than a series of `public static Foo(this T x, …)` methods. The block form is the idiomatic .NET 10 / C# 14 syntax, single-sources the receiver parameter, and lets you promote to extension properties/operators later without breaking call sites.

```csharp
// CORRECT
public static class ClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal user)
    {
        public Guid GetUserId() =>
            Guid.Parse(user.FindFirst(StrgClaimNames.Subject)?.Value
                       ?? throw new InvalidOperationException("…"));

        public bool HasScope(string scope) =>
            user.FindAll(StrgClaimNames.Scope)
                .Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(scope, StringComparer.Ordinal));
    }
}

// WRONG — unrelated top-level static methods when they all share a receiver
public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) => /*…*/;
    public static bool HasScope(this ClaimsPrincipal user, string scope) => /*…*/;
}
```

A single standalone extension method may still use the classic `this T` form; switch to a block as soon as a second member is added for the same receiver.

---

## Naming Conventions

### Filename must match typename

After any type rename, rename the containing file too. `git status` must show a `R` (renamed) entry for the file, not a content-only `M`. Rider does not rename files by default — enable "Rename file when renaming type" in settings, or use `git mv` manually. A mismatch (file `IFooMarker.cs` containing `interface IBarMarker`) should not land in a commit.

### Acronyms: PascalCase for 3+ letters, everywhere

Per Microsoft Framework Design Guidelines, acronyms of 3 or more letters are PascalCase — first letter uppercase, rest lowercase: `GraphQl`, `Html`, `Json`, `Xml`, `Http`. Two-letter acronyms stay fully uppercase: `IO`, `ID`, `DB`. Applies uniformly to types, members, namespaces, folder names, and filenames — no half-application (a type named `GraphQlFoo` inside namespace `Strg.GraphQl` is wrong; both must match).

```csharp
// CORRECT
namespace Strg.GraphQl.Consumers;
public sealed class GraphQlSubscriptionPublisher { }

// WRONG
namespace Strg.GraphQl.Consumers;
public sealed class GraphQLSubscriptionPublisher { }
```

### Local `const`: camelCase (project-specific)

Deviation from Microsoft's convention: local `const` identifiers inside method bodies are **camelCase**. Non-local constants (field-scope) remain PascalCase as per Microsoft.

```csharp
// CORRECT
public void Test()
{
    const string oldPath = "/docs/draft.md";
    const string newPath = "/docs/archive/draft-2026.md";
    /* … */
}

// WRONG
public void Test()
{
    const string OldPath = "/docs/draft.md";
}
```

Rationale: matches Rider/ReSharper's cleanup output; eliminates a recurring churn source where test data is extracted to a local `const` for readability and then re-cased on the next cleanup pass. Enforced by a naming rule in `.editorconfig`.

---

## When Writing Code

Before finishing any edit, verify:

- [ ] Every `CancellationToken` parameter is named `cancellationToken`.
- [ ] Every `if`/`for`/`foreach`/`while`/`do`/`using`/`lock` body has braces.
- [ ] No MIME-type, header-name, claim-type, HTTP-method, or auth-scheme **string literal** — use the BCL constant.
- [ ] Any project-specific string repeating across files lives in `Strg.Core/Constants/`.
- [ ] Target framework references (`.csproj` if overridden) are `net10.0` or absent (inherited from `Directory.Build.props`).
- [ ] No unused `using` directives.
- [ ] Primary constructor used where the body would be trivial assignment/delegation only.
- [ ] Extension methods grouped under `extension(T)` block when two or more share a receiver.
- [ ] Any renamed type has its file renamed to match (and `git status` shows `R`).
- [ ] Acronyms of 3+ letters are PascalCase (`GraphQl`, not `GraphQL`) in types, members, namespaces, folders, filenames.
- [ ] Local `const` identifiers are camelCase; field-level constants remain PascalCase.
- [ ] `await using` used for `IAsyncDisposable` targets inside async methods.
- [ ] `x?.Prop = v` used instead of `if (x is not null) x.Prop = v` when the body is a single assignment.

---

## Precedence

If this skill contradicts `CLAUDE.md`, `CLAUDE.md` wins. This skill complements, never overrides, the project-wide guide.
