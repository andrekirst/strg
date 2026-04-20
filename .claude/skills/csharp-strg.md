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
- **Primary constructors** where they replace a one-line `_field = param` ctor, e.g. `public sealed class FooService(IFooRepo repo) : IFooService`.

---

## When Writing Code

Before finishing any edit, verify:

- [ ] Every `CancellationToken` parameter is named `cancellationToken`.
- [ ] Every `if`/`for`/`foreach`/`while`/`do`/`using`/`lock` body has braces.
- [ ] No MIME-type, header-name, claim-type, HTTP-method, or auth-scheme **string literal** — use the BCL constant.
- [ ] Any project-specific string repeating across files lives in `Strg.Core/Constants/`.
- [ ] Target framework references (`.csproj` if overridden) are `net10.0` or absent (inherited from `Directory.Build.props`).

---

## Precedence

If this skill contradicts `CLAUDE.md`, `CLAUDE.md` wins. This skill complements, never overrides, the project-wide guide.
