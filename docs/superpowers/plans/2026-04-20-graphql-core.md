# GraphQL Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the full core GraphQL API for strg — foundation, types, namespaced queries and mutations, and file event subscriptions — covering STRG-049, STRG-050–058, STRG-065, and STRG-066.

**Architecture:** Code-first Hot Chocolate 15.x schema with assembly scanning (OCP), namespaced Query/Mutation roots via resolver objects, Relay cursor pagination everywhere, and Relay-style mutation payloads. Subscription backplane is Redis in production / in-memory in development and tests.

**Tech Stack:** Hot Chocolate 15.x, EF Core 9, xUnit, SQLite in-memory (tests), StackExchange.Redis

---

## Prerequisites

This plan executes **after** the following issues are complete:
- STRG-001 (solution scaffold — `Strg.Api`, `Strg.Core`, `Strg.Infrastructure` projects exist)
- STRG-004 (EF Core `StrgDbContext` exists)
- STRG-011 (User entity)
- STRG-025 (Drive entity)
- STRG-031 (FileItem entity)
- STRG-046 (Tag entity)
- STRG-013 (JWT bearer auth wired)

Confirm these compile before starting: `dotnet build src/`

---

## File Map

### New project: `src/Strg.GraphQL/`

```
src/Strg.GraphQL/
├── Strg.GraphQL.csproj
├── IGraphQLMarker.cs
├── Topics.cs
├── Errors/
│   └── StrgErrorFilter.cs
├── Payloads/
│   ├── UserError.cs
│   ├── Drive/      CreateDrivePayload.cs  UpdateDrivePayload.cs  DeleteDrivePayload.cs
│   ├── File/       CreateFolderPayload.cs  DeleteFilePayload.cs  MoveFilePayload.cs  CopyFilePayload.cs  RenameFilePayload.cs
│   ├── Tag/        AddTagPayload.cs  UpdateTagPayload.cs  RemoveTagPayload.cs  RemoveAllTagsPayload.cs
│   └── User/       UpdateProfilePayload.cs  ChangePasswordPayload.cs  UpdateUserQuotaPayload.cs  LockUserPayload.cs  UnlockUserPayload.cs
├── Inputs/
│   ├── File/       FileFilterInput.cs  FileSortInput.cs  CreateFolderInput.cs  DeleteFileInput.cs  MoveFileInput.cs  CopyFileInput.cs  RenameFileInput.cs
│   ├── Drive/      CreateDriveInput.cs  UpdateDriveInput.cs  DeleteDriveInput.cs
│   ├── Tag/        AddTagInput.cs  UpdateTagInput.cs  RemoveTagInput.cs  RemoveAllTagsInput.cs
│   ├── User/       UpdateProfileInput.cs  ChangePasswordInput.cs
│   ├── Admin/      UpdateUserQuotaInput.cs  LockUserInput.cs  UnlockUserInput.cs  AuditFilterInput.cs
│   └── Enums/      ConflictResolution.cs  FileSortField.cs  SortDirection.cs  FileEventType.cs
├── Types/
│   ├── UserType.cs
│   ├── DriveType.cs
│   ├── FileItemType.cs
│   ├── TagType.cs
│   ├── FileVersionType.cs
│   ├── AuditEntryType.cs
│   └── FileEventOutputType.cs
├── DataLoaders/
│   ├── FileItemByIdDataLoader.cs
│   ├── DriveByIdDataLoader.cs
│   ├── UserByIdDataLoader.cs
│   └── InboxRuleByIdDataLoader.cs
├── Queries/
│   ├── RootQueryExtension.cs    (me + namespace fields)
│   ├── Storage/
│   │   ├── StorageQueries.cs    (marker record + DriveQueries + FileQueries)
│   │   └── (DriveQueries and FileQueries are inner extensions of StorageQueries marker)
│   └── Admin/
│       └── AdminQueries.cs      (marker record + AuditLogQueries)
├── Mutations/
│   ├── RootMutationExtension.cs (namespace fields)
│   ├── Storage/
│   │   ├── StorageMutations.cs  (marker record)
│   │   ├── DriveMutations.cs
│   │   ├── FileMutations.cs
│   │   └── TagMutations.cs
│   ├── User/
│   │   └── UserMutations.cs     (marker record + handlers)
│   └── Admin/
│       └── AdminMutations.cs    (marker record + handlers)
└── Subscriptions/
    ├── FileSubscriptions.cs
    └── Payloads/
        └── FileEventPayload.cs
```

### Modified: `src/Strg.Api/Program.cs`
Add HC server registration.

### New: `tests/Strg.GraphQL.Tests/`
```
tests/Strg.GraphQL.Tests/
├── Strg.GraphQL.Tests.csproj
├── Helpers/
│   └── GraphQLTestFixture.cs
├── Queries/
│   ├── DriveQueriesTests.cs
│   ├── FileQueriesTests.cs
│   └── AuditQueriesTests.cs
├── Mutations/
│   ├── DriveMutationsTests.cs
│   ├── FileMutationsTests.cs
│   ├── TagMutationsTests.cs
│   └── UserMutationsTests.cs
└── Subscriptions/
    └── FileSubscriptionsTests.cs
```

---

## Task 1: Create Strg.GraphQL project + install packages (STRG-049)

**Files:**
- Create: `src/Strg.GraphQL/Strg.GraphQL.csproj`
- Create: `src/Strg.GraphQL/IGraphQLMarker.cs`
- Create: `src/Strg.GraphQL/Topics.cs`
- Modify: `strg.sln` (add project reference)

- [ ] **Step 1: Create the class library project**

```bash
dotnet new classlib -n Strg.GraphQL -o src/Strg.GraphQL --framework net9.0
dotnet sln add src/Strg.GraphQL/Strg.GraphQL.csproj
```

- [ ] **Step 2: Add project references**

```bash
dotnet add src/Strg.GraphQL reference src/Strg.Core
dotnet add src/Strg.GraphQL reference src/Strg.Infrastructure
dotnet add src/Strg.Api reference src/Strg.GraphQL
```

- [ ] **Step 3: Add NuGet packages**

```bash
dotnet add src/Strg.GraphQL package HotChocolate.AspNetCore --version 15.*
dotnet add src/Strg.GraphQL package HotChocolate.Data.EntityFramework --version 15.*
dotnet add src/Strg.GraphQL package HotChocolate.AspNetCore.Authorization --version 15.*
dotnet add src/Strg.GraphQL package StackExchange.Redis
```

- [ ] **Step 4: Create directory structure**

```bash
mkdir -p src/Strg.GraphQL/{Errors,Payloads/{Drive,File,Tag,User},Inputs/{File,Drive,Tag,User,Admin,Enums},Types,DataLoaders,Queries/{Storage,Admin},Mutations/{Storage,User,Admin},Subscriptions/Payloads}
```

- [ ] **Step 5: Create `IGraphQLMarker.cs`**

```csharp
// src/Strg.GraphQL/IGraphQLMarker.cs
namespace Strg.GraphQL;

// Empty interface — assembly anchor for AddTypes() discovery only
internal interface IGraphQLMarker { }
```

- [ ] **Step 6: Create `Topics.cs`**

```csharp
// src/Strg.GraphQL/Topics.cs
namespace Strg.GraphQL;

public static class Topics
{
    public static string FileEvents(Guid driveId) => $"file-events:{driveId}";
    public static string InboxFileProcessed(Guid tenantId) => $"inbox-file-processed:{tenantId}";
}
```

- [ ] **Step 7: Verify build**

```bash
dotnet build src/Strg.GraphQL
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Strg.GraphQL/ strg.sln
git commit -m "feat(graphql): create Strg.GraphQL project with HC 15.x packages"
```

---

## Task 2: UserError type + StrgErrorFilter (STRG-056)

**Files:**
- Create: `src/Strg.GraphQL/Payloads/UserError.cs`
- Create: `src/Strg.GraphQL/Errors/StrgErrorFilter.cs`
- Create: `tests/Strg.GraphQL.Tests/Strg.GraphQL.Tests.csproj`
- Create: `tests/Strg.GraphQL.Tests/Helpers/GraphQLTestFixture.cs`

- [ ] **Step 1: Create test project**

```bash
dotnet new xunit -n Strg.GraphQL.Tests -o tests/Strg.GraphQL.Tests --framework net9.0
dotnet sln add tests/Strg.GraphQL.Tests/Strg.GraphQL.Tests.csproj
dotnet add tests/Strg.GraphQL.Tests reference src/Strg.GraphQL
dotnet add tests/Strg.GraphQL.Tests package HotChocolate.AspNetCore --version 15.*
dotnet add tests/Strg.GraphQL.Tests package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Create `GraphQLTestFixture.cs`**

```csharp
// tests/Strg.GraphQL.Tests/Helpers/GraphQLTestFixture.cs
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Exceptions;
using Strg.GraphQL.Errors;

namespace Strg.GraphQL.Tests.Helpers;

public static class GraphQLTestFixture
{
    public static async Task<IRequestExecutor> CreateExecutorAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<IRequestExecutorBuilder>? configureSchema = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);

        var builder = services
            .AddGraphQLServer()
            .AddQueryType(q => q.Name("Query"))
            .AddMutationType(m => m.Name("Mutation"))
            .AddTypes(typeof(IGraphQLMarker).Assembly)
            .AddGlobalObjectIdentification()
            .AddInMemorySubscriptions()
            .AddErrorFilter<StrgErrorFilter>();

        configureSchema?.Invoke(builder);

        return await services.BuildServiceProvider()
            .GetRequiredService<IRequestExecutorResolver>()
            .GetRequestExecutorAsync();
    }
}
```

- [ ] **Step 3: Write failing test for StrgErrorFilter**

```csharp
// tests/Strg.GraphQL.Tests/ErrorFilterTests.cs
using HotChocolate.Execution;
using Strg.Core.Exceptions;
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests;

public class ErrorFilterTests
{
    [Fact]
    public async Task StoragePathException_MapsTo_InvalidPathCode()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b.AddTypeExtension<ThrowingQuery>());

        var result = await executor.ExecuteAsync("{ throwPath }");

        var errors = result.ExpectQueryResult().Errors!;
        Assert.Single(errors);
        Assert.Equal("INVALID_PATH", errors[0].Code);
        Assert.DoesNotContain("StoragePath", errors[0].Message); // no stack trace
    }

    [Fact]
    public async Task UnhandledException_MapsTo_InternalErrorCode_NoStackTrace()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b.AddTypeExtension<ThrowingQuery>());

        var result = await executor.ExecuteAsync("{ throwUnknown }");

        var errors = result.ExpectQueryResult().Errors!;
        Assert.Single(errors);
        Assert.Equal("INTERNAL_ERROR", errors[0].Code);
        Assert.Null(errors[0].Exception); // no exception exposed in prod mode
    }

    [ExtendObjectType("Query")]
    private sealed class ThrowingQuery
    {
        public string ThrowPath() => throw new StoragePathException("traversal attempt");
        public string ThrowUnknown() => throw new InvalidOperationException("internal bug");
    }
}
```

- [ ] **Step 4: Run — expect FAIL (StrgErrorFilter not created yet)**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter ErrorFilterTests -v
```
Expected: FAIL — `StrgErrorFilter` not found.

- [ ] **Step 5: Create `UserError.cs`**

```csharp
// src/Strg.GraphQL/Payloads/UserError.cs
namespace Strg.GraphQL.Payloads;

public sealed record UserError(string Code, string Message, string? Field);
```

- [ ] **Step 6: Create `StrgErrorFilter.cs`**

```csharp
// src/Strg.GraphQL/Errors/StrgErrorFilter.cs
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Strg.Core.Exceptions;

namespace Strg.GraphQL.Errors;

public sealed class StrgErrorFilter : IErrorFilter
{
    private readonly IHostEnvironment _env;
    private readonly ILogger<StrgErrorFilter> _logger;

    public StrgErrorFilter(IHostEnvironment env, ILogger<StrgErrorFilter> logger)
    {
        _env = env;
        _logger = logger;
    }

    public IError OnError(IError error)
    {
        var exception = error.Exception;

        return exception switch
        {
            StoragePathException => error
                .WithCode("INVALID_PATH")
                .RemoveException()
                .WithMessage(exception.Message),

            ValidationException ve => error
                .WithCode("VALIDATION_ERROR")
                .RemoveException()
                .WithMessage(ve.Message),

            UnauthorizedAccessException => error
                .WithCode("FORBIDDEN")
                .RemoveException()
                .WithMessage("Access denied."),

            NotFoundException => error
                .WithCode("NOT_FOUND")
                .RemoveException()
                .WithMessage(exception.Message),

            QuotaExceededException => error
                .WithCode("QUOTA_EXCEEDED")
                .RemoveException()
                .WithMessage("Storage quota exceeded."),

            DuplicateDriveNameException => error
                .WithCode("DUPLICATE_DRIVE_NAME")
                .RemoveException()
                .WithMessage(exception.Message),

            _ => HandleUnexpected(error, exception)
        };
    }

    private IError HandleUnexpected(IError error, Exception? ex)
    {
        if (ex is not null)
            _logger.LogError(ex, "Unhandled GraphQL error");

        return _env.IsDevelopment()
            ? error.WithCode("INTERNAL_ERROR")
            : error
                .WithCode("INTERNAL_ERROR")
                .RemoveException()
                .WithMessage("An internal error occurred.");
    }
}
```

- [ ] **Step 7: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter ErrorFilterTests -v
```
Expected: PASS — 2 tests.

- [ ] **Step 8: Commit**

```bash
git add src/Strg.GraphQL/Payloads/UserError.cs src/Strg.GraphQL/Errors/StrgErrorFilter.cs tests/Strg.GraphQL.Tests/
git commit -m "feat(graphql): add UserError record and StrgErrorFilter with domain exception mapping"
```

---

## Task 3: Enum types and input records

**Files:**
- Create: `src/Strg.GraphQL/Inputs/Enums/ConflictResolution.cs`
- Create: `src/Strg.GraphQL/Inputs/Enums/FileSortField.cs`
- Create: `src/Strg.GraphQL/Inputs/Enums/SortDirection.cs`
- Create: `src/Strg.GraphQL/Inputs/Enums/FileEventType.cs`
- Create: `src/Strg.GraphQL/Inputs/File/FileFilterInput.cs`
- Create: `src/Strg.GraphQL/Inputs/File/FileSortInput.cs`
- Create: `src/Strg.GraphQL/Inputs/File/*.cs` (all file inputs)
- Create: `src/Strg.GraphQL/Inputs/Drive/*.cs`
- Create: `src/Strg.GraphQL/Inputs/Tag/*.cs`
- Create: `src/Strg.GraphQL/Inputs/User/*.cs`
- Create: `src/Strg.GraphQL/Inputs/Admin/*.cs`

- [ ] **Step 1: Create enum types**

```csharp
// src/Strg.GraphQL/Inputs/Enums/ConflictResolution.cs
namespace Strg.GraphQL.Inputs.Enums;
public enum ConflictResolution { AutoRename, Overwrite, Fail }
```

```csharp
// src/Strg.GraphQL/Inputs/Enums/FileSortField.cs
namespace Strg.GraphQL.Inputs.Enums;
public enum FileSortField { Name, Size, CreatedAt, UpdatedAt, MimeType }
```

```csharp
// src/Strg.GraphQL/Inputs/Enums/SortDirection.cs
namespace Strg.GraphQL.Inputs.Enums;
public enum SortDirection { Asc, Desc }
```

```csharp
// src/Strg.GraphQL/Inputs/Enums/FileEventType.cs
namespace Strg.GraphQL.Inputs.Enums;
public enum FileEventType { Uploaded, Deleted, Moved, Copied, Renamed }
```

- [ ] **Step 2: Create file inputs**

```csharp
// src/Strg.GraphQL/Inputs/File/FileFilterInput.cs
namespace Strg.GraphQL.Inputs.File;

public sealed record FileFilterInput(
    string? NameContains,
    string? MimeType,           // supports wildcard "image/*"
    bool? IsFolder,
    long? MinSize,
    long? MaxSize,
    DateTimeOffset? CreatedAfter,
    DateTimeOffset? CreatedBefore,
    string? TagKey,
    bool? IsInInbox
);
```

```csharp
// src/Strg.GraphQL/Inputs/File/FileSortInput.cs
using Strg.GraphQL.Inputs.Enums;
namespace Strg.GraphQL.Inputs.File;
public sealed record FileSortInput(FileSortField Field, SortDirection Direction);
```

```csharp
// src/Strg.GraphQL/Inputs/File/CreateFolderInput.cs
namespace Strg.GraphQL.Inputs.File;
public sealed record CreateFolderInput(Guid DriveId, string Path);
```

```csharp
// src/Strg.GraphQL/Inputs/File/DeleteFileInput.cs
namespace Strg.GraphQL.Inputs.File;
public sealed record DeleteFileInput(Guid Id);
```

```csharp
// src/Strg.GraphQL/Inputs/File/MoveFileInput.cs
using Strg.GraphQL.Inputs.Enums;
namespace Strg.GraphQL.Inputs.File;
public sealed record MoveFileInput(Guid Id, string TargetPath, Guid? TargetDriveId, ConflictResolution? ConflictResolution);
```

```csharp
// src/Strg.GraphQL/Inputs/File/CopyFileInput.cs
using Strg.GraphQL.Inputs.Enums;
namespace Strg.GraphQL.Inputs.File;
public sealed record CopyFileInput(Guid Id, string TargetPath, Guid? TargetDriveId, ConflictResolution? ConflictResolution);
```

```csharp
// src/Strg.GraphQL/Inputs/File/RenameFileInput.cs
namespace Strg.GraphQL.Inputs.File;
public sealed record RenameFileInput(Guid Id, string NewName);
```

- [ ] **Step 3: Create drive inputs**

```csharp
// src/Strg.GraphQL/Inputs/Drive/CreateDriveInput.cs
namespace Strg.GraphQL.Inputs.Drive;
public sealed record CreateDriveInput(string Name, string ProviderType, string ProviderConfig, bool? IsDefault, bool? IsEncrypted);
```

```csharp
// src/Strg.GraphQL/Inputs/Drive/UpdateDriveInput.cs
namespace Strg.GraphQL.Inputs.Drive;
public sealed record UpdateDriveInput(Guid Id, string? Name, bool? IsDefault);
```

```csharp
// src/Strg.GraphQL/Inputs/Drive/DeleteDriveInput.cs
namespace Strg.GraphQL.Inputs.Drive;
public sealed record DeleteDriveInput(Guid Id);
```

- [ ] **Step 4: Create tag inputs**

```csharp
// src/Strg.GraphQL/Inputs/Tag/AddTagInput.cs
using Strg.Core.Domain; // TagValueType enum from domain
namespace Strg.GraphQL.Inputs.Tag;
public sealed record AddTagInput(Guid FileId, string Key, string Value, TagValueType ValueType);
```

```csharp
// src/Strg.GraphQL/Inputs/Tag/UpdateTagInput.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Inputs.Tag;
public sealed record UpdateTagInput(Guid Id, string Value, TagValueType ValueType);
```

```csharp
// src/Strg.GraphQL/Inputs/Tag/RemoveTagInput.cs
namespace Strg.GraphQL.Inputs.Tag;
public sealed record RemoveTagInput(Guid Id);
```

```csharp
// src/Strg.GraphQL/Inputs/Tag/RemoveAllTagsInput.cs
namespace Strg.GraphQL.Inputs.Tag;
public sealed record RemoveAllTagsInput(Guid FileId);
```

- [ ] **Step 5: Create user and admin inputs**

```csharp
// src/Strg.GraphQL/Inputs/User/UpdateProfileInput.cs
namespace Strg.GraphQL.Inputs.User;
public sealed record UpdateProfileInput(string? DisplayName, string? Email);
```

```csharp
// src/Strg.GraphQL/Inputs/User/ChangePasswordInput.cs
namespace Strg.GraphQL.Inputs.User;
public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
```

```csharp
// src/Strg.GraphQL/Inputs/Admin/UpdateUserQuotaInput.cs
namespace Strg.GraphQL.Inputs.Admin;
public sealed record UpdateUserQuotaInput(Guid UserId, long QuotaBytes);
```

```csharp
// src/Strg.GraphQL/Inputs/Admin/LockUserInput.cs
namespace Strg.GraphQL.Inputs.Admin;
public sealed record LockUserInput(Guid UserId, string? Reason);
```

```csharp
// src/Strg.GraphQL/Inputs/Admin/UnlockUserInput.cs
namespace Strg.GraphQL.Inputs.Admin;
public sealed record UnlockUserInput(Guid UserId);
```

```csharp
// src/Strg.GraphQL/Inputs/Admin/AuditFilterInput.cs
namespace Strg.GraphQL.Inputs.Admin;
public sealed record AuditFilterInput(Guid? UserId, string? Action, string? ResourceType, DateTimeOffset? From, DateTimeOffset? To);
```

- [ ] **Step 6: Build to verify all records compile**

```bash
dotnet build src/Strg.GraphQL
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Strg.GraphQL/Inputs/
git commit -m "feat(graphql): add all input records and enum types"
```

---

## Task 4: Payload records

**Files:**
- Create: `src/Strg.GraphQL/Payloads/Drive/*.cs`
- Create: `src/Strg.GraphQL/Payloads/File/*.cs`
- Create: `src/Strg.GraphQL/Payloads/Tag/*.cs`
- Create: `src/Strg.GraphQL/Payloads/User/*.cs`

- [ ] **Step 1: Create drive payloads**

```csharp
// src/Strg.GraphQL/Payloads/Drive/CreateDrivePayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.Drive;
public sealed record CreateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/Drive/UpdateDrivePayload.cs
namespace Strg.GraphQL.Payloads.Drive;
public sealed record UpdateDrivePayload(Core.Domain.Drive? Drive, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/Drive/DeleteDrivePayload.cs
namespace Strg.GraphQL.Payloads.Drive;
public sealed record DeleteDrivePayload(Guid? DriveId, IReadOnlyList<UserError>? Errors);
```

- [ ] **Step 2: Create file payloads**

```csharp
// src/Strg.GraphQL/Payloads/File/CreateFolderPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record CreateFolderPayload(FileItem? File, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/File/DeleteFilePayload.cs
namespace Strg.GraphQL.Payloads.File;
public sealed record DeleteFilePayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/File/MoveFilePayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record MoveFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/File/CopyFilePayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record CopyFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/File/RenameFilePayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.File;
public sealed record RenameFilePayload(FileItem? File, IReadOnlyList<UserError>? Errors);
```

- [ ] **Step 3: Create tag payloads**

```csharp
// src/Strg.GraphQL/Payloads/Tag/AddTagPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.Tag;
public sealed record AddTagPayload(Core.Domain.Tag? Tag, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/Tag/UpdateTagPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.Tag;
public sealed record UpdateTagPayload(Core.Domain.Tag? Tag, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/Tag/RemoveTagPayload.cs
namespace Strg.GraphQL.Payloads.Tag;
public sealed record RemoveTagPayload(Guid? TagId, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/Tag/RemoveAllTagsPayload.cs
namespace Strg.GraphQL.Payloads.Tag;
public sealed record RemoveAllTagsPayload(Guid? FileId, IReadOnlyList<UserError>? Errors);
```

- [ ] **Step 4: Create user payloads**

```csharp
// src/Strg.GraphQL/Payloads/User/UpdateProfilePayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record UpdateProfilePayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/User/ChangePasswordPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record ChangePasswordPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/User/UpdateUserQuotaPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record UpdateUserQuotaPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/User/LockUserPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record LockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
```

```csharp
// src/Strg.GraphQL/Payloads/User/UnlockUserPayload.cs
using Strg.Core.Domain;
namespace Strg.GraphQL.Payloads.User;
public sealed record UnlockUserPayload(Core.Domain.User? User, IReadOnlyList<UserError>? Errors);
```

- [ ] **Step 5: Build**

```bash
dotnet build src/Strg.GraphQL
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Strg.GraphQL/Payloads/
git commit -m "feat(graphql): add all mutation payload records"
```

---

## Task 5: ObjectType descriptors (STRG-050)

**Files:**
- Create: `src/Strg.GraphQL/Types/UserType.cs`
- Create: `src/Strg.GraphQL/Types/DriveType.cs`
- Create: `src/Strg.GraphQL/Types/FileItemType.cs`
- Create: `src/Strg.GraphQL/Types/TagType.cs`
- Create: `src/Strg.GraphQL/Types/FileVersionType.cs`
- Create: `src/Strg.GraphQL/Types/AuditEntryType.cs`

- [ ] **Step 1: Write failing schema test**

```csharp
// tests/Strg.GraphQL.Tests/SchemaTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests;

public class SchemaTests
{
    [Fact]
    public async Task DriveType_ProviderConfig_NotInSchema()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync();
        var result = await executor.ExecuteAsync("""
            {
              __type(name: "Drive") {
                fields { name }
              }
            }
            """);

        var fields = result.ExpectQueryResult()
            .Data!["__type"]!["fields"]!
            .AsArray()
            .Select(f => f!["name"]!.GetValue<string>())
            .ToList();

        Assert.DoesNotContain("providerConfig", fields);
        Assert.DoesNotContain("tenantId", fields);
    }

    [Fact]
    public async Task FileItemType_HasIsFolder_NotIsDirectory()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync();
        var result = await executor.ExecuteAsync("""
            {
              __type(name: "FileItem") {
                fields { name }
              }
            }
            """);

        var fields = result.ExpectQueryResult()
            .Data!["__type"]!["fields"]!
            .AsArray()
            .Select(f => f!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("isFolder", fields);
        Assert.DoesNotContain("isDirectory", fields);
        Assert.DoesNotContain("tenantId", fields);
    }
}
```

- [ ] **Step 2: Run — expect FAIL (types not created yet)**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter SchemaTests -v
```
Expected: FAIL.

- [ ] **Step 3: Create `UserType.cs`**

```csharp
// src/Strg.GraphQL/Types/UserType.cs
using HotChocolate.Types;
using Strg.Core.Domain;

namespace Strg.GraphQL.Types;

public sealed class UserType : ObjectType<User>
{
    protected override void Configure(IObjectTypeDescriptor<User> descriptor)
    {
        descriptor.ImplementsNode().IdField(u => u.Id);
        descriptor.Field(u => u.TenantId).Ignore();
        descriptor.Field(u => u.PasswordHash).Ignore();
        descriptor.Field(u => u.LockedUntil).Ignore();
    }
}
```

- [ ] **Step 4: Create `DriveType.cs`**

```csharp
// src/Strg.GraphQL/Types/DriveType.cs
using HotChocolate.Types;
using Strg.Core.Domain;

namespace Strg.GraphQL.Types;

public sealed class DriveType : ObjectType<Drive>
{
    protected override void Configure(IObjectTypeDescriptor<Drive> descriptor)
    {
        descriptor.ImplementsNode().IdField(d => d.Id);
        descriptor.Field(d => d.ProviderConfig).Ignore();  // contains credentials — NEVER expose
        descriptor.Field(d => d.TenantId).Ignore();
    }
}
```

- [ ] **Step 5: Create `FileItemType.cs`**

```csharp
// src/Strg.GraphQL/Types/FileItemType.cs
using HotChocolate.Types;
using Strg.Core.Domain;

namespace Strg.GraphQL.Types;

public sealed class FileItemType : ObjectType<FileItem>
{
    protected override void Configure(IObjectTypeDescriptor<FileItem> descriptor)
    {
        descriptor.ImplementsNode().IdField(f => f.Id);
        descriptor.Field(f => f.TenantId).Ignore();
        descriptor.Field(f => f.StorageKey).Ignore();  // internal encryption key reference
        // isFolder: Boolean! (matches domain property name)
        // mimeType, size are nullable — correct for folders
        descriptor.Field("children")
            .UsePaging<ObjectType<FileItem>>(options: new() { DefaultPageSize = 50, MaxPageSize = 200 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<Infrastructure.Data.StrgDbContext>();
                return db.Files.Where(f => f.ParentId == file.Id);
            });
        descriptor.Field("tags")
            .UsePaging<ObjectType<Tag>>(options: new() { DefaultPageSize = 100, MaxPageSize = 500 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<Infrastructure.Data.StrgDbContext>();
                return db.Tags.Where(t => t.FileId == file.Id);
            });
        descriptor.Field("versions")
            .UsePaging<ObjectType<FileVersion>>(options: new() { DefaultPageSize = 20, MaxPageSize = 100 })
            .Resolve(ctx =>
            {
                var file = ctx.Parent<FileItem>();
                var db = ctx.Service<Infrastructure.Data.StrgDbContext>();
                return db.FileVersions.Where(v => v.FileId == file.Id).OrderByDescending(v => v.VersionNumber);
            });
    }
}
```

- [ ] **Step 6: Create `TagType.cs`**

```csharp
// src/Strg.GraphQL/Types/TagType.cs
using HotChocolate.Types;
using Strg.Core.Domain;

namespace Strg.GraphQL.Types;

public sealed class TagType : ObjectType<Tag>
{
    protected override void Configure(IObjectTypeDescriptor<Tag> descriptor)
    {
        descriptor.ImplementsNode().IdField(t => t.Id);
        descriptor.Field(t => t.TenantId).Ignore();
        descriptor.Field(t => t.UserId).Ignore();  // user isolation — not a public field
        descriptor.Field(t => t.FileId).Ignore();  // navigation only; accessed via FileItem.tags
    }
}
```

- [ ] **Step 7: Create `FileVersionType.cs`**

```csharp
// src/Strg.GraphQL/Types/FileVersionType.cs
using HotChocolate.Types;
using Strg.Core.Domain;

namespace Strg.GraphQL.Types;

public sealed class FileVersionType : ObjectType<FileVersion>
{
    protected override void Configure(IObjectTypeDescriptor<FileVersion> descriptor)
    {
        descriptor.ImplementsNode().IdField(v => v.Id);
        descriptor.Field(v => v.TenantId).Ignore();
        descriptor.Field(v => v.StorageKey).Ignore();  // internal encryption reference
        descriptor.Field(v => v.FileId).Ignore();
    }
}
```

- [ ] **Step 8: Create `AuditEntryType.cs`**

```csharp
// src/Strg.GraphQL/Types/AuditEntryType.cs
using HotChocolate.Types;
using Strg.Core.Domain;
using Strg.GraphQL.DataLoaders;

namespace Strg.GraphQL.Types;

public sealed class AuditEntryType : ObjectType<AuditEntry>
{
    protected override void Configure(IObjectTypeDescriptor<AuditEntry> descriptor)
    {
        descriptor.ImplementsNode().IdField(e => e.Id);
        descriptor.Field(e => e.TenantId).Ignore();
        descriptor.Field(e => e.UserId).Ignore();  // exposed via performedBy field below

        descriptor.Field("performedBy")
            .Resolve(async ctx =>
            {
                var entry = ctx.Parent<AuditEntry>();
                var loader = ctx.Service<UserByIdDataLoader>();
                return await loader.LoadAsync(entry.UserId, ctx.RequestAborted);
            });
    }
}
```

- [ ] **Step 9: Run schema tests — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter SchemaTests -v
```
Expected: PASS — 2 tests.

- [ ] **Step 10: Commit**

```bash
git add src/Strg.GraphQL/Types/
git commit -m "feat(graphql): add ObjectType descriptors for User, Drive, FileItem, Tag, FileVersion, AuditEntry"
```

---

## Task 6: DataLoaders

**Files:**
- Create: `src/Strg.GraphQL/DataLoaders/FileItemByIdDataLoader.cs`
- Create: `src/Strg.GraphQL/DataLoaders/DriveByIdDataLoader.cs`
- Create: `src/Strg.GraphQL/DataLoaders/UserByIdDataLoader.cs`
- Create: `src/Strg.GraphQL/DataLoaders/InboxRuleByIdDataLoader.cs`

- [ ] **Step 1: Create `FileItemByIdDataLoader.cs`**

```csharp
// src/Strg.GraphQL/DataLoaders/FileItemByIdDataLoader.cs
using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class FileItemByIdDataLoader : BatchDataLoader<Guid, FileItem>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public FileItemByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, FileItem>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Files
            .Where(f => keys.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
    }
}
```

- [ ] **Step 2: Create `DriveByIdDataLoader.cs`**

```csharp
// src/Strg.GraphQL/DataLoaders/DriveByIdDataLoader.cs
using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class DriveByIdDataLoader : BatchDataLoader<Guid, Drive>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public DriveByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, Drive>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Drives
            .Where(d => keys.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);
    }
}
```

- [ ] **Step 3: Create `UserByIdDataLoader.cs`**

```csharp
// src/Strg.GraphQL/DataLoaders/UserByIdDataLoader.cs
using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class UserByIdDataLoader : BatchDataLoader<Guid, User>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public UserByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, User>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => keys.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
    }
}
```

- [ ] **Step 4: Create `InboxRuleByIdDataLoader.cs`**

```csharp
// src/Strg.GraphQL/DataLoaders/InboxRuleByIdDataLoader.cs
using GreenDonut;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.DataLoaders;

public sealed class InboxRuleByIdDataLoader : BatchDataLoader<Guid, InboxRule>
{
    private readonly IDbContextFactory<StrgDbContext> _dbFactory;

    public InboxRuleByIdDataLoader(
        IDbContextFactory<StrgDbContext> dbFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, InboxRule>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.InboxRules
            .Where(r => keys.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);
    }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build src/Strg.GraphQL
```
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/Strg.GraphQL/DataLoaders/
git commit -m "feat(graphql): add FileItem, Drive, User, InboxRule BatchDataLoaders"
```

---

## Task 7: Namespace wiring + HC server registration (STRG-049)

**Files:**
- Create: `src/Strg.GraphQL/Queries/RootQueryExtension.cs`
- Create: `src/Strg.GraphQL/Queries/Storage/StorageQueries.cs`
- Create: `src/Strg.GraphQL/Queries/Admin/AdminQueries.cs`
- Create: `src/Strg.GraphQL/Mutations/RootMutationExtension.cs`
- Create: `src/Strg.GraphQL/Mutations/Storage/StorageMutations.cs`
- Create: `src/Strg.GraphQL/Mutations/User/UserMutations.cs`
- Create: `src/Strg.GraphQL/Mutations/Admin/AdminMutations.cs`
- Modify: `src/Strg.Api/Program.cs`

- [ ] **Step 1: Create namespace marker records**

```csharp
// src/Strg.GraphQL/Queries/Storage/StorageQueries.cs
namespace Strg.GraphQL.Queries.Storage;
public sealed record StorageQueries;
```

```csharp
// src/Strg.GraphQL/Queries/Admin/AdminQueries.cs
namespace Strg.GraphQL.Queries.Admin;
public sealed record AdminQueries;
```

```csharp
// src/Strg.GraphQL/Mutations/Storage/StorageMutations.cs
namespace Strg.GraphQL.Mutations.Storage;
public sealed record StorageMutations;
```

```csharp
// src/Strg.GraphQL/Mutations/User/UserMutations.cs
namespace Strg.GraphQL.Mutations.User;
public sealed record UserMutations;
```

```csharp
// src/Strg.GraphQL/Mutations/Admin/AdminMutations.cs
namespace Strg.GraphQL.Mutations.Admin;
public sealed record AdminMutations;
```

- [ ] **Step 2: Create `RootQueryExtension.cs`**

```csharp
// src/Strg.GraphQL/Queries/RootQueryExtension.cs
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Queries.Admin;
using Strg.GraphQL.Queries.Storage;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries;

[ExtendObjectType("Query")]
public sealed class RootQueryExtension
{
    [Authorize]
    public async Task<User> Me(
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
           ?? throw new UnauthorizedAccessException();

    public StorageQueries Storage() => new();

    public InboxQueries Inbox() => new();

    [Authorize(Policy = "Admin")]
    public AdminQueries Admin() => new();
}

// InboxQueries marker — defined here for now; Inbox plan will extend it
public sealed record InboxQueries;
```

- [ ] **Step 3: Create `RootMutationExtension.cs`**

```csharp
// src/Strg.GraphQL/Mutations/RootMutationExtension.cs
using HotChocolate.Types;
using Strg.GraphQL.Mutations.Admin;
using Strg.GraphQL.Mutations.Storage;
using Strg.GraphQL.Mutations.User;

namespace Strg.GraphQL.Mutations;

[ExtendObjectType("Mutation")]
public sealed class RootMutationExtension
{
    public StorageMutations Storage() => new();
    public UserMutations User() => new();
    public AdminMutations Admin() => new();
    public InboxMutations Inbox() => new();
}

public sealed record InboxMutations;
```

- [ ] **Step 4: Write failing root query test**

```csharp
// tests/Strg.GraphQL.Tests/Queries/RootQueryTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Queries;

public class RootQueryTests
{
    [Fact]
    public async Task Query_HasStorageField()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync();
        var result = await executor.ExecuteAsync("""
            { __type(name: "Query") { fields { name } } }
            """);

        var fields = result.ExpectQueryResult()
            .Data!["__type"]!["fields"]!
            .AsArray()
            .Select(f => f!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("storage", fields);
        Assert.Contains("inbox", fields);
        Assert.Contains("admin", fields);
        Assert.Contains("me", fields);
    }

    [Fact]
    public async Task Mutation_HasStorageField()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync();
        var result = await executor.ExecuteAsync("""
            { __type(name: "Mutation") { fields { name } } }
            """);

        var fields = result.ExpectQueryResult()
            .Data!["__type"]!["fields"]!
            .AsArray()
            .Select(f => f!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("storage", fields);
        Assert.Contains("user", fields);
        Assert.Contains("admin", fields);
        Assert.Contains("inbox", fields);
    }
}
```

- [ ] **Step 5: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter RootQueryTests -v
```
Expected: FAIL.

- [ ] **Step 6: Register HC in `Program.cs`**

Add to `src/Strg.Api/Program.cs` (after existing service registrations):

```csharp
// GraphQL — Hot Chocolate 15.x
builder.Services
    .AddGraphQLServer()
    .AddQueryType(q => q.Name("Query"))
    .AddMutationType(m => m.Name("Mutation"))
    .AddSubscriptionType(s => s.Name("Subscription"))
    .AddTypes(typeof(Strg.GraphQL.IGraphQLMarker).Assembly)
    .AddGlobalObjectIdentification()
    .AddFiltering()
    .AddSorting()
    .AddAuthorization()
    .RegisterDbContext<StrgDbContext>(DbContextKind.Pooled)
    .AddErrorFilter<StrgErrorFilter>()  // BEFORE types
    .AddMaxExecutionDepthRule(10)
    .ModifyRequestOptions(o => o.Complexity.MaximumAllowed = 100);

if (builder.Environment.IsDevelopment())
    builder.Services.AddGraphQLServer().AddInMemorySubscriptions();
else
    builder.Services.AddGraphQLServer()
        .AddRedisSubscriptions(sp =>
            ConnectionMultiplexer.Connect(
                sp.GetRequiredService<IConfiguration>()["Redis:ConnectionString"]!));

if (!builder.Environment.IsDevelopment())
    builder.Services.AddGraphQLServer()
        .ModifyOptions(o => o.EnableSchemaIntrospection = false);

// Middleware
app.UseWebSockets();   // before MapGraphQL
app.MapGraphQL("/graphql");
```

- [ ] **Step 7: Run tests — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter RootQueryTests -v
```
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Strg.GraphQL/Queries/ src/Strg.GraphQL/Mutations/ src/Strg.Api/Program.cs
git commit -m "feat(graphql): wire HC server with namespace roots, assembly scanning, and OCP registration"
```

---

## Task 8: Drive queries (STRG-057)

**Files:**
- Create: `src/Strg.GraphQL/Queries/Storage/DriveQueries.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Queries/DriveQueriesTests.cs
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Queries;

public class DriveQueriesTests
{
    [Fact]
    public async Task GetDrives_ReturnsOnlyCurrentTenantDrives()
    {
        var tenantId = Guid.NewGuid();
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                // In-memory SQLite with seed data
                services.AddDbContext<StrgDbContext>(o =>
                    o.UseSqlite("DataSource=:memory:"));
                // Seed: two drives for tenantId, one for another tenant
            },
            globalState: new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["userId"] = Guid.NewGuid()
            });

        var result = await executor.ExecuteAsync("""
            { storage { drives(first: 10) { nodes { id name } totalCount } } }
            """);

        var data = result.ExpectQueryResult().Data!;
        var totalCount = data["storage"]!["drives"]!["totalCount"]!.GetValue<int>();

        Assert.Equal(2, totalCount);  // only current tenant's drives
    }

    [Fact]
    public async Task GetDrive_OtherTenant_ReturnsNull()
    {
        var tenantId = Guid.NewGuid();
        var otherDriveId = Guid.NewGuid();

        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = Guid.NewGuid() });

        var result = await executor.ExecuteAsync(
            $"{{ storage {{ drive(id: \"{otherDriveId}\") {{ id }} }} }}");

        var driveData = result.ExpectQueryResult().Data!["storage"]!["drive"];
        Assert.Null(driveData);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter DriveQueriesTests -v
```

- [ ] **Step 3: Create `DriveQueries.cs`**

```csharp
// src/Strg.GraphQL/Queries/Storage/DriveQueries.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Data;
using HotChocolate.Types;
using HotChocolate.Types.Pagination;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries.Storage;

[ExtendObjectType<StorageQueries>]
public sealed class DriveQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<Drive> GetDrives(
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId)
        => db.Drives
             .Where(d => d.TenantId == tenantId)
             .OrderBy(d => d.Name);

    [Authorize(Policy = "FilesRead")]
    public Task<Drive?> GetDrive(
        Guid id,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
        => db.Drives.FirstOrDefaultAsync(
               d => d.Id == id && d.TenantId == tenantId, ct);
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter DriveQueriesTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Queries/Storage/DriveQueries.cs tests/Strg.GraphQL.Tests/Queries/DriveQueriesTests.cs
git commit -m "feat(graphql): add DriveQueries with cursor pagination and tenant isolation"
```

---

## Task 9: File queries (STRG-050)

**Files:**
- Create: `src/Strg.GraphQL/Queries/Storage/FileQueries.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Queries/FileQueriesTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Queries;

public class FileQueriesTests
{
    [Fact]
    public async Task GetFiles_FilterByNameContains_ReturnsMatching()
    {
        var driveId = Guid.NewGuid();
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            // ... seed DB with files, one matching "report"
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var result = await executor.ExecuteAsync($$"""
            { storage { files(driveId: "{{driveId}}", filter: { nameContains: "report" }) { nodes { id name } totalCount } } }
            """);

        var totalCount = result.ExpectQueryResult()
            .Data!["storage"]!["files"]!["totalCount"]!.GetValue<int>();
        Assert.Equal(1, totalCount);
    }

    [Fact]
    public async Task GetFile_ReturnsNullForInaccessibleFile()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var result = await executor.ExecuteAsync(
            $"{{ storage {{ file(id: \"{Guid.NewGuid()}\") {{ id }} }} }}");

        Assert.Null(result.ExpectQueryResult().Data!["storage"]!["file"]);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileQueriesTests -v
```

- [ ] **Step 3: Create `FileQueries.cs`**

```csharp
// src/Strg.GraphQL/Queries/Storage/FileQueries.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Inputs.File;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries.Storage;

[ExtendObjectType<StorageQueries>]
public sealed class FileQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    [Authorize(Policy = "FilesRead")]
    public IQueryable<FileItem> GetFiles(
        Guid driveId,
        string? path,
        FileFilterInput? filter,
        [Service] StrgDbContext db)
    {
        var query = db.Files.Where(f => f.DriveId == driveId);

        if (path is not null)
            query = query.Where(f => f.Path.StartsWith(path));
        if (filter?.NameContains is not null)
            query = query.Where(f => f.Name.Contains(filter.NameContains));
        if (filter?.IsFolder.HasValue == true)
            query = query.Where(f => f.IsFolder == filter.IsFolder.Value);
        if (filter?.MinSize.HasValue == true)
            query = query.Where(f => f.Size >= filter.MinSize.Value);
        if (filter?.MaxSize.HasValue == true)
            query = query.Where(f => f.Size <= filter.MaxSize.Value);
        if (filter?.CreatedAfter.HasValue == true)
            query = query.Where(f => f.CreatedAt >= filter.CreatedAfter.Value);
        if (filter?.CreatedBefore.HasValue == true)
            query = query.Where(f => f.CreatedAt <= filter.CreatedBefore.Value);
        if (filter?.IsInInbox.HasValue == true)
            query = query.Where(f => f.IsInInbox == filter.IsInInbox.Value);
        if (filter?.MimeType is not null)
            query = filter.MimeType.EndsWith("/*")
                ? query.Where(f => f.MimeType != null && f.MimeType.StartsWith(filter.MimeType[..^2]))
                : query.Where(f => f.MimeType == filter.MimeType);

        return query;
    }

    [Authorize(Policy = "FilesRead")]
    public Task<FileItem?> GetFile(
        Guid id,
        [Service] StrgDbContext db,
        CancellationToken ct)
        => db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileQueriesTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Queries/Storage/FileQueries.cs tests/Strg.GraphQL.Tests/Queries/FileQueriesTests.cs
git commit -m "feat(graphql): add FileQueries with handcrafted filter, cursor pagination"
```

---

## Task 10: Audit log query (STRG-055)

**Files:**
- Create: `src/Strg.GraphQL/Queries/Admin/AuditLogQueries.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Queries/AuditQueriesTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Queries;

public class AuditQueriesTests
{
    [Fact]
    public async Task AuditLog_RequiresAdminPolicy()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        // Query without admin role
        var result = await executor.ExecuteAsync(
            "{ admin { auditLog(first: 5) { nodes { id action } } } }");

        // Should have authorization error
        Assert.NotNull(result.ExpectQueryResult().Errors);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter AuditQueriesTests -v
```

- [ ] **Step 3: Create `AuditLogQueries.cs`**

```csharp
// src/Strg.GraphQL/Queries/Admin/AuditLogQueries.cs
using HotChocolate;
using HotChocolate.Types;
using Strg.Core.Domain;
using Strg.GraphQL.Inputs.Admin;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Queries.Admin;

[ExtendObjectType<AdminQueries>]
public sealed class AuditLogQueries
{
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 500)]
    [GraphQLComplexity(5)]
    public IQueryable<AuditEntry> GetAuditLog(
        AuditFilterInput? filter,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId)
    {
        var query = db.AuditEntries
            .Where(e => e.TenantId == tenantId)  // explicit — AuditEntry has no global tenant filter
            .OrderByDescending(e => e.PerformedAt);

        if (filter?.UserId.HasValue == true)
            query = query.Where(e => e.UserId == filter.UserId.Value);
        if (filter?.Action is not null)
            query = query.Where(e => e.Action == filter.Action);
        if (filter?.ResourceType is not null)
            query = query.Where(e => e.ResourceType == filter.ResourceType);
        if (filter?.From.HasValue == true)
            query = query.Where(e => e.PerformedAt >= filter.From.Value);
        if (filter?.To.HasValue == true)
            query = query.Where(e => e.PerformedAt <= filter.To.Value);

        return query;
    }

    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200)]
    [GraphQLComplexity(5)]
    public IQueryable<User> GetUsers([Service] StrgDbContext db)
        => db.Users.OrderBy(u => u.Email);

    public Task<User?> GetUser(
        Guid id,
        [Service] StrgDbContext db,
        CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter AuditQueriesTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Queries/Admin/ tests/Strg.GraphQL.Tests/Queries/AuditQueriesTests.cs
git commit -m "feat(graphql): add AuditLogQueries and AdminQueries extensions"
```

---

## Task 11: Drive mutations (STRG-053)

**Files:**
- Create: `src/Strg.GraphQL/Mutations/Storage/DriveMutations.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Mutations/DriveMutationsTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Mutations;

public class DriveMutationsTests
{
    [Fact]
    public async Task CreateDrive_InvalidName_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = Guid.NewGuid(), ["roles"] = new[] { "admin" } });

        var result = await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "My Invalid Drive!", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id }
                  errors { code field }
                }
              }
            }
            """);

        var errors = result.ExpectQueryResult()
            .Data!["storage"]!["createDrive"]!["errors"]!
            .AsArray();

        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0]!["code"]!.GetValue<string>());
        Assert.Equal("name", errors[0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateDrive_ValidInput_ReturnsDrive()
    {
        var tenantId = Guid.NewGuid();
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureServices: s => s.AddDbContext<StrgDbContext>(o => o.UseSqlite("DataSource=:memory:")),
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = Guid.NewGuid(), ["roles"] = new[] { "admin" } });

        var result = await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "my-drive", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id name }
                  errors { code }
                }
              }
            }
            """);

        var drive = result.ExpectQueryResult().Data!["storage"]!["createDrive"]!["drive"];
        Assert.NotNull(drive);
        Assert.Equal("my-drive", drive!["name"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter DriveMutationsTests -v
```

- [ ] **Step 3: Create `DriveMutations.cs`**

```csharp
// src/Strg.GraphQL/Mutations/Storage/DriveMutations.cs
using System.Text.RegularExpressions;
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.GraphQL.Inputs.Drive;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.Drive;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class DriveMutations
{
    private static readonly Regex ValidDriveName = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    [Authorize(Policy = "Admin")]
    public async Task<CreateDrivePayload> CreateDriveAsync(
        CreateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        if (!ValidDriveName.IsMatch(input.Name))
            return new CreateDrivePayload(null,
                [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-], max 64 chars.", "name")]);

        if (await db.Drives.AnyAsync(d => d.TenantId == tenantId && d.Name == input.Name, ct))
            return new CreateDrivePayload(null,
                [new UserError("DUPLICATE_DRIVE_NAME", $"Drive '{input.Name}' already exists.", "name")]);

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = input.Name,
            ProviderType = input.ProviderType,
            ProviderConfig = input.ProviderConfig,  // stored, never returned
            IsEncrypted = input.IsEncrypted ?? false,
            IsDefault = input.IsDefault ?? false
        };

        db.Drives.Add(drive);
        await db.SaveChangesAsync(ct);
        return new CreateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<UpdateDrivePayload> UpdateDriveAsync(
        UpdateDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, ct);

        if (drive is null)
            return new UpdateDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);

        if (input.Name is not null)
        {
            if (!ValidDriveName.IsMatch(input.Name))
                return new UpdateDrivePayload(null,
                    [new UserError("VALIDATION_ERROR", "Drive name must match [a-z0-9-].", "name")]);
            drive.Name = input.Name;
        }
        if (input.IsDefault.HasValue) drive.IsDefault = input.IsDefault.Value;

        await db.SaveChangesAsync(ct);
        return new UpdateDrivePayload(drive, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<DeleteDrivePayload> DeleteDriveAsync(
        DeleteDriveInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        CancellationToken ct)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(
            d => d.Id == input.Id && d.TenantId == tenantId, ct);

        if (drive is null)
            return new DeleteDrivePayload(null,
                [new UserError("NOT_FOUND", "Drive not found.", null)]);

        drive.IsDeleted = true;
        drive.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new DeleteDrivePayload(drive.Id, null);
    }
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter DriveMutationsTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Mutations/Storage/DriveMutations.cs tests/Strg.GraphQL.Tests/Mutations/DriveMutationsTests.cs
git commit -m "feat(graphql): add DriveMutations (create, update, delete) with payload pattern"
```

---

## Task 12: File mutations (STRG-052)

**Files:**
- Create: `src/Strg.GraphQL/Mutations/Storage/FileMutations.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Mutations/FileMutationsTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Mutations;

public class FileMutationsTests
{
    [Fact]
    public async Task CreateFolder_PathTraversal_ReturnsInvalidPathError()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var result = await executor.ExecuteAsync("""
            mutation {
              storage {
                createFolder(input: { driveId: "00000000-0000-0000-0000-000000000001", path: "../../etc/passwd" }) {
                  file { id }
                  errors { code field }
                }
              }
            }
            """);

        var errors = result.ExpectQueryResult()
            .Data!["storage"]!["createFolder"]!["errors"]!.AsArray();

        Assert.NotEmpty(errors);
        Assert.Equal("INVALID_PATH", errors[0]!["code"]!.GetValue<string>());
        Assert.Equal("path", errors[0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteFile_NotFound_ReturnsNotFoundError()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var result = await executor.ExecuteAsync($"""
            mutation {{
              storage {{
                deleteFile(input: {{ id: "{Guid.NewGuid()}" }}) {{
                  fileId
                  errors {{ code }}
                }}
              }}
            }}
            """);

        var errors = result.ExpectQueryResult()
            .Data!["storage"]!["deleteFile"]!["errors"]!.AsArray();

        Assert.Equal("NOT_FOUND", errors[0]!["code"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileMutationsTests -v
```

- [ ] **Step 3: Create `FileMutations.cs`**

```csharp
// src/Strg.GraphQL/Mutations/Storage/FileMutations.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.GraphQL.Inputs.File;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.File;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class FileMutations
{
    [Authorize(Policy = "FilesWrite")]
    public async Task<CreateFolderPayload> CreateFolderAsync(
        CreateFolderInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var path = StoragePath.Parse(input.Path);
            var folder = await fileService.CreateFolderAsync(input.DriveId, path.Value, userId, ct);
            return new CreateFolderPayload(folder, null);
        }
        catch (StoragePathException ex)
        {
            return new CreateFolderPayload(null, [new UserError("INVALID_PATH", ex.Message, "path")]);
        }
        catch (NotFoundException ex)
        {
            return new CreateFolderPayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<DeleteFilePayload> DeleteFileAsync(
        DeleteFileInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            await fileService.DeleteAsync(input.Id, userId, ct);
            return new DeleteFilePayload(input.Id, null);
        }
        catch (NotFoundException ex)
        {
            return new DeleteFilePayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<MoveFilePayload> MoveFileAsync(
        MoveFileInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var path = StoragePath.Parse(input.TargetPath);
            var file = await fileService.MoveAsync(input.Id, path.Value, input.TargetDriveId,
                (Core.Domain.ConflictResolution?)input.ConflictResolution ?? Core.Domain.ConflictResolution.Fail, userId, ct);
            return new MoveFilePayload(file, null);
        }
        catch (StoragePathException ex)
        {
            return new MoveFilePayload(null, [new UserError("INVALID_PATH", ex.Message, "targetPath")]);
        }
        catch (NotFoundException ex)
        {
            return new MoveFilePayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<CopyFilePayload> CopyFileAsync(
        CopyFileInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var path = StoragePath.Parse(input.TargetPath);
            var file = await fileService.CopyAsync(input.Id, path.Value, input.TargetDriveId,
                (Core.Domain.ConflictResolution?)input.ConflictResolution ?? Core.Domain.ConflictResolution.Fail, userId, ct);
            return new CopyFilePayload(file, null);
        }
        catch (StoragePathException ex)
        {
            return new CopyFilePayload(null, [new UserError("INVALID_PATH", ex.Message, "targetPath")]);
        }
        catch (NotFoundException ex)
        {
            return new CopyFilePayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "FilesWrite")]
    public async Task<RenameFilePayload> RenameFileAsync(
        RenameFileInput input,
        [Service] IFileService fileService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var file = await fileService.RenameAsync(input.Id, input.NewName, userId, ct);
            return new RenameFilePayload(file, null);
        }
        catch (NotFoundException ex)
        {
            return new RenameFilePayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
        catch (ValidationException ex)
        {
            return new RenameFilePayload(null, [new UserError("VALIDATION_ERROR", ex.Message, "newName")]);
        }
    }
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileMutationsTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Mutations/Storage/FileMutations.cs tests/Strg.GraphQL.Tests/Mutations/FileMutationsTests.cs
git commit -m "feat(graphql): add FileMutations (createFolder, deleteFile, moveFile, copyFile, renameFile)"
```

---

## Task 13: Tag mutations (STRG-051)

**Files:**
- Create: `src/Strg.GraphQL/Mutations/Storage/TagMutations.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Mutations/TagMutationsTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Mutations;

public class TagMutationsTests
{
    [Fact]
    public async Task AddTag_KeyTooLong_ReturnsValidationError()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var longKey = new string('x', 256);
        var result = await executor.ExecuteAsync($"""
            mutation {{
              storage {{
                addTag(input: {{ fileId: "{Guid.NewGuid()}", key: "{longKey}", value: "v", valueType: STRING }}) {{
                  tag {{ id }}
                  errors {{ code field }}
                }}
              }}
            }}
            """);

        var errors = result.ExpectQueryResult()
            .Data!["storage"]!["addTag"]!["errors"]!.AsArray();

        Assert.Equal("VALIDATION_ERROR", errors[0]!["code"]!.GetValue<string>());
        Assert.Equal("key", errors[0]!["field"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter TagMutationsTests -v
```

- [ ] **Step 3: Create `TagMutations.cs`**

```csharp
// src/Strg.GraphQL/Mutations/Storage/TagMutations.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.GraphQL.Inputs.Tag;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.Tag;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class TagMutations
{
    [Authorize(Policy = "TagsWrite")]
    public async Task<AddTagPayload> AddTagAsync(
        AddTagInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        if (input.Key.Length > 255)
            return new AddTagPayload(null, [new UserError("VALIDATION_ERROR", "key must be ≤255 chars.", "key")]);
        if (input.Value.Length > 255)
            return new AddTagPayload(null, [new UserError("VALIDATION_ERROR", "value must be ≤255 chars.", "value")]);

        try
        {
            var tag = await tagService.UpsertAsync(input.FileId, userId, input.Key, input.Value, input.ValueType, ct);
            return new AddTagPayload(tag, null);
        }
        catch (NotFoundException ex)
        {
            return new AddTagPayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<UpdateTagPayload> UpdateTagAsync(
        UpdateTagInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        try
        {
            var tag = await tagService.UpdateAsync(input.Id, userId, input.Value, input.ValueType, ct);
            return new UpdateTagPayload(tag, null);
        }
        catch (NotFoundException ex)
        {
            return new UpdateTagPayload(null, [new UserError("NOT_FOUND", ex.Message, null)]);
        }
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveTagPayload> RemoveTagAsync(
        RemoveTagInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        await tagService.RemoveAsync(input.Id, userId, ct);  // idempotent
        return new RemoveTagPayload(input.Id, null);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveAllTagsPayload> RemoveAllTagsAsync(
        RemoveAllTagsInput input,
        [Service] ITagService tagService,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        await tagService.RemoveAllAsync(input.FileId, userId, ct);
        return new RemoveAllTagsPayload(input.FileId, null);
    }
}
```

- [ ] **Step 4: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter TagMutationsTests -v
```

- [ ] **Step 5: Commit**

```bash
git add src/Strg.GraphQL/Mutations/Storage/TagMutations.cs tests/Strg.GraphQL.Tests/Mutations/TagMutationsTests.cs
git commit -m "feat(graphql): add TagMutations (add, update, remove, removeAll) with payload pattern"
```

---

## Task 14: User and admin mutations (STRG-054)

**Files:**
- Create: `src/Strg.GraphQL/Mutations/User/UserMutationHandlers.cs`
- Create: `src/Strg.GraphQL/Mutations/Admin/AdminMutationHandlers.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Mutations/UserMutationsTests.cs
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Mutations;

public class UserMutationsTests
{
    [Fact]
    public async Task UpdateProfile_DisplayNameTooLong_ReturnsValidationError()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var longName = new string('x', 256);
        var result = await executor.ExecuteAsync($"""
            mutation {{
              user {{
                updateProfile(input: {{ displayName: "{longName}" }}) {{
                  user {{ displayName }}
                  errors {{ code field }}
                }}
              }}
            }}
            """);

        var errors = result.ExpectQueryResult()
            .Data!["user"]!["updateProfile"]!["errors"]!.AsArray();
        Assert.Equal("VALIDATION_ERROR", errors[0]!["code"]!.GetValue<string>());
        Assert.Equal("displayName", errors[0]!["field"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter UserMutationsTests -v
```

- [ ] **Step 3: Create `UserMutationHandlers.cs`**

```csharp
// src/Strg.GraphQL/Mutations/User/UserMutationHandlers.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.Core.Services;
using Strg.GraphQL.Inputs.User;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.User;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.User;

[ExtendObjectType<UserMutations>]
public sealed class UserMutationHandlers
{
    [Authorize]
    public async Task<UpdateProfilePayload> UpdateProfileAsync(
        UpdateProfileInput input,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        if (input.DisplayName?.Length > 255)
            return new UpdateProfilePayload(null,
                [new UserError("VALIDATION_ERROR", "displayName must be ≤255 chars.", "displayName")]);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (input.DisplayName is not null) user.DisplayName = input.DisplayName;
        if (input.Email is not null) user.Email = input.Email;
        await db.SaveChangesAsync(ct);
        return new UpdateProfilePayload(user, null);
    }

    [Authorize]
    public async Task<ChangePasswordPayload> ChangePasswordAsync(
        ChangePasswordInput input,
        [Service] IPasswordHasher passwordHasher,
        [Service] StrgDbContext db,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

        if (!passwordHasher.Verify(input.CurrentPassword, user.PasswordHash))
            return new ChangePasswordPayload(null,
                [new UserError("INVALID_PASSWORD", "Current password is incorrect.", "currentPassword")]);

        user.PasswordHash = passwordHasher.Hash(input.NewPassword);
        await db.SaveChangesAsync(ct);
        return new ChangePasswordPayload(user, null);
    }
}
```

- [ ] **Step 4: Create `AdminMutationHandlers.cs`**

```csharp
// src/Strg.GraphQL/Mutations/Admin/AdminMutationHandlers.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.GraphQL.Inputs.Admin;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.User;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Admin;

[ExtendObjectType<AdminMutations>]
public sealed class AdminMutationHandlers
{
    [Authorize(Policy = "Admin")]
    public async Task<UpdateUserQuotaPayload> UpdateUserQuotaAsync(
        UpdateUserQuotaInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        if (input.QuotaBytes < 0)
            return new UpdateUserQuotaPayload(null,
                [new UserError("VALIDATION_ERROR", "quotaBytes must be non-negative.", "quotaBytes")]);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, ct);
        if (user is null)
            return new UpdateUserQuotaPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.QuotaBytes = input.QuotaBytes;
        await db.SaveChangesAsync(ct);
        return new UpdateUserQuotaPayload(user, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<LockUserPayload> LockUserAsync(
        LockUserInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, ct);
        if (user is null)
            return new LockUserPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.LockedUntil = DateTimeOffset.UtcNow.AddYears(100);
        await db.SaveChangesAsync(ct);
        return new LockUserPayload(user, null);
    }

    [Authorize(Policy = "Admin")]
    public async Task<UnlockUserPayload> UnlockUserAsync(
        UnlockUserInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.UserId, ct);
        if (user is null)
            return new UnlockUserPayload(null, [new UserError("NOT_FOUND", "User not found.", null)]);

        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return new UnlockUserPayload(user, null);
    }
}
```

- [ ] **Step 5: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter UserMutationsTests -v
```

- [ ] **Step 6: Commit**

```bash
git add src/Strg.GraphQL/Mutations/User/ src/Strg.GraphQL/Mutations/Admin/ tests/Strg.GraphQL.Tests/Mutations/UserMutationsTests.cs
git commit -m "feat(graphql): add UserMutations (updateProfile, changePassword) and AdminMutations (quota, lock/unlock)"
```

---

## Task 15: GraphQLSubscriptionPublisher + FileEvent DTO (STRG-065)

**Files:**
- Create: `src/Strg.Core/Events/FileEvent.cs`
- Create: `src/Strg.Infrastructure/Consumers/GraphQLSubscriptionPublisher.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/Strg.GraphQL.Tests/Subscriptions/FileSubscriptionsTests.cs
using HotChocolate.Subscriptions;
using Strg.Core.Events;
using Strg.GraphQL.Tests.Helpers;

namespace Strg.GraphQL.Tests.Subscriptions;

public class FileSubscriptionsTests
{
    [Fact]
    public async Task FileEvents_ReceivedAfterPublish()
    {
        var driveId = Guid.NewGuid();
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            globalState: new Dictionary<string, object?> { ["tenantId"] = Guid.NewGuid(), ["userId"] = Guid.NewGuid() });

        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        // Subscribe first
        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType driveId }} }}");

        // Publish event
        var fileEvent = new FileEvent(
            EventType: FileEventType.Uploaded,
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);

        await sender.SendAsync(Topics.FileEvents(driveId), fileEvent, CancellationToken.None);

        // Collect message
        await using var stream = (IResponseStream)subscriptionResult;
        var first = await stream.ReadResultAsync();

        var eventType = first.ExpectQueryResult()
            .Data!["fileEvents"]!["eventType"]!.GetValue<string>();

        Assert.Equal("UPLOADED", eventType);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileSubscriptionsTests -v
```

- [ ] **Step 3: Create `FileEvent.cs`**

```csharp
// src/Strg.Core/Events/FileEvent.cs
namespace Strg.Core.Events;

public enum FileEventType { Uploaded, Deleted, Moved, Copied, Renamed }

public sealed record FileEvent(
    FileEventType EventType,
    Guid FileId,
    Guid DriveId,
    Guid UserId,
    Guid TenantId,      // propagated for tenant isolation guard — never exposed to clients
    string? OldPath,
    string? NewPath,
    DateTimeOffset OccurredAt
);
```

- [ ] **Step 4: Create `GraphQLSubscriptionPublisher.cs`**

```csharp
// src/Strg.Infrastructure/Consumers/GraphQLSubscriptionPublisher.cs
using HotChocolate.Subscriptions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;
using Strg.GraphQL;

namespace Strg.Infrastructure.Consumers;

public sealed class GraphQLSubscriptionPublisher :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<FileCopiedEvent>,
    IConsumer<FileRenamedEvent>
{
    private readonly ITopicEventSender _sender;
    private readonly ILogger<GraphQLSubscriptionPublisher> _logger;

    public GraphQLSubscriptionPublisher(ITopicEventSender sender, ILogger<GraphQLSubscriptionPublisher> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<FileUploadedEvent> ctx)
        => SendAsync(FileEventType.Uploaded, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, null, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileDeletedEvent> ctx)
        => SendAsync(FileEventType.Deleted, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, null, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileMovedEvent> ctx)
        => SendAsync(FileEventType.Moved, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, ctx.Message.OldPath, ctx.Message.NewPath, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileCopiedEvent> ctx)
        => SendAsync(FileEventType.Copied, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, null, ctx.Message.NewPath, ctx.CancellationToken);

    public Task Consume(ConsumeContext<FileRenamedEvent> ctx)
        => SendAsync(FileEventType.Renamed, ctx.Message.FileId, ctx.Message.DriveId,
            ctx.Message.UserId, ctx.Message.TenantId, ctx.Message.OldName, ctx.Message.NewName, ctx.CancellationToken);

    private async Task SendAsync(
        FileEventType type, Guid fileId, Guid driveId, Guid userId, Guid tenantId,
        string? oldPath, string? newPath, CancellationToken ct)
    {
        var fileEvent = new FileEvent(type, fileId, driveId, userId, tenantId, oldPath, newPath, DateTimeOffset.UtcNow);

        await _sender.SendAsync(Topics.FileEvents(driveId), fileEvent, ct);

        _logger.LogDebug(
            "Published {EventType} event to topic {Topic}",
            type, Topics.FileEvents(driveId));
    }
}
```

- [ ] **Step 5: Register consumer in MassTransit config (in `Strg.Infrastructure` MassTransit setup)**

```csharp
// In Strg.Infrastructure MassTransit configuration (wherever AddMassTransit is called):
cfg.AddConsumer<GraphQLSubscriptionPublisher>();
```

- [ ] **Step 6: Run — expect PASS**

```bash
dotnet test tests/Strg.GraphQL.Tests --filter FileSubscriptionsTests -v
```

- [ ] **Step 7: Commit**

```bash
git add src/Strg.Core/Events/FileEvent.cs src/Strg.Infrastructure/Consumers/GraphQLSubscriptionPublisher.cs
git commit -m "feat(graphql): add FileEvent DTO and GraphQLSubscriptionPublisher MassTransit consumer"
```

---

## Task 16: FileSubscriptions GraphQL type (STRG-066)

**Files:**
- Create: `src/Strg.GraphQL/Subscriptions/Payloads/FileEventPayload.cs`
- Create: `src/Strg.GraphQL/Types/FileEventOutputType.cs`
- Create: `src/Strg.GraphQL/Subscriptions/FileSubscriptions.cs`

- [ ] **Step 1: Create `FileEventPayload.cs`**

```csharp
// src/Strg.GraphQL/Subscriptions/Payloads/FileEventPayload.cs
using Strg.Core.Domain;
using Strg.Core.Events;

namespace Strg.GraphQL.Subscriptions.Payloads;

// Output payload — no TenantId field (never exposed to clients)
public sealed record FileEventPayload(
    FileEventType EventType,
    FileItem File,
    Guid DriveId,
    DateTimeOffset OccurredAt
);
```

- [ ] **Step 2: Create `FileEventOutputType.cs`**

```csharp
// src/Strg.GraphQL/Types/FileEventOutputType.cs
using HotChocolate.Types;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Types;

public sealed class FileEventOutputType : ObjectType<FileEventPayload>
{
    protected override void Configure(IObjectTypeDescriptor<FileEventPayload> descriptor)
    {
        // TenantId is not on FileEventPayload — by design, never exposed
        descriptor.Field(e => e.EventType);
        descriptor.Field(e => e.File);
        descriptor.Field(e => e.DriveId);
        descriptor.Field(e => e.OccurredAt);
    }
}
```

- [ ] **Step 3: Create `FileSubscriptions.cs`**

```csharp
// src/Strg.GraphQL/Subscriptions/FileSubscriptions.cs
using HotChocolate;
using HotChocolate.AspNetCore.Authorization;
using HotChocolate.Subscriptions;
using HotChocolate.Types;
using Strg.Core.Events;
using Strg.GraphQL.DataLoaders;
using Strg.GraphQL.Subscriptions.Payloads;

namespace Strg.GraphQL.Subscriptions;

[ExtendObjectType("Subscription")]
public sealed class FileSubscriptions
{
    [Subscribe]
    [Authorize(Policy = "FilesRead")]
    public async Task<FileEventPayload> OnFileEvent(
        Guid driveId,
        [EventMessage] FileEvent fileEvent,
        [GlobalState("tenantId")] Guid tenantId,
        [Service] FileItemByIdDataLoader fileLoader,
        CancellationToken ct)
    {
        // Tenant isolation guard — belt-and-suspenders over topic scoping
        if (fileEvent.TenantId != tenantId)
            throw new UnauthorizedAccessException("Subscription event tenant mismatch.");

        var file = await fileLoader.LoadAsync(fileEvent.FileId, ct)
            ?? throw new InvalidOperationException($"File {fileEvent.FileId} not found.");

        return new FileEventPayload(fileEvent.EventType, file, fileEvent.DriveId, fileEvent.OccurredAt);
    }

    [SubscribeResolver]
    public ValueTask<ISourceStream<FileEvent>> SubscribeToFileEventsAsync(
        Guid driveId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken ct)
        => receiver.SubscribeAsync<FileEvent>(Topics.FileEvents(driveId), ct);
}
```

- [ ] **Step 4: Run all tests**

```bash
dotnet test tests/Strg.GraphQL.Tests -v
```
Expected: All tests PASS.

- [ ] **Step 5: Final integration smoke test against running API**

```bash
dotnet run --project src/Strg.Api &
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -d '{"query":"{ __typename }"}' | jq .
# Expected: {"data":{"__typename":"Query"}}
```

- [ ] **Step 6: Commit**

```bash
git add src/Strg.GraphQL/Subscriptions/ src/Strg.GraphQL/Types/FileEventOutputType.cs
git commit -m "feat(graphql): add FileSubscriptions with tenant isolation guard and DataLoader file resolution"
```

---

## Verification Checklist

Run after all tasks complete:

- [ ] `dotnet build` — 0 errors, 0 warnings
- [ ] `dotnet test tests/Strg.GraphQL.Tests` — all tests pass
- [ ] Schema introspection: `query { __schema { types { name } } }` — returns all expected types
- [ ] `Drive.providerConfig` absent from introspection
- [ ] `*.tenantId` absent from all type introspections
- [ ] Namespace queries work: `query { storage { drives(first: 5) { nodes { id } } } }`
- [ ] Namespace mutations work: `mutation { storage { createDrive(...) { drive { id } errors { code } } } }`
- [ ] Depth limit: query with 11 levels → rejected
- [ ] Complexity limit: query exceeding 100 → rejected
- [ ] Production introspection disabled: `ModifyOptions(o => o.EnableSchemaIntrospection = false)` active

---

## Inbox Plan (separate)

The inbox GraphQL features (STRG-310, STRG-311, STRG-312) depend on inbox entity issues (STRG-302, STRG-304, STRG-305) that are not yet implemented. Write `docs/superpowers/plans/2026-04-20-graphql-inbox.md` once those entity issues are complete.
