---
id: STRG-085
title: Set up FluentValidation for request validation
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [api, validation, infrastructure]
depends_on: [STRG-010]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-085: Set up FluentValidation for request validation

## Summary

Configure FluentValidation for input validation on REST request bodies and GraphQL mutation inputs. Validation errors map to `400 Bad Request` (REST) and `VALIDATION_ERROR` (GraphQL). Replaces ad-hoc validation in services.

## Technical Specification

### Package: `FluentValidation.AspNetCore`

### Registration in `Program.cs`:

```csharp
builder.Services.AddValidatorsFromAssemblyContaining<Program>(
    lifetime: ServiceLifetime.Scoped);
builder.Services.AddFluentValidationAutoValidation();
```

### Example validators:

```csharp
// src/Strg.Api/Validators/CreateFolderRequestValidator.cs
public class CreateFolderRequestValidator : AbstractValidator<CreateFolderRequest>
{
    public CreateFolderRequestValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty()
            .MaximumLength(4096)
            .Must(p => !p.Contains(".."))
            .WithMessage("Path must not contain '..'.");
    }
}

// src/Strg.Api/Validators/MoveFileRequestValidator.cs
public class MoveFileRequestValidator : AbstractValidator<MoveFileRequest>
{
    public MoveFileRequestValidator()
    {
        RuleFor(x => x.TargetPath)
            .NotEmpty()
            .MaximumLength(4096);
    }
}
```

### Validation error response format (REST):

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Validation failed",
  "status": 400,
  "errors": {
    "path": ["Path must not contain '..'."]
  }
}
```

### GraphQL integration:

FluentValidation is called from mutation resolvers via `IValidator<T>` injection:

```csharp
public async Task<FileItem> CreateFolder(CreateFolderInput input, [Service] IValidator<CreateFolderInput> validator, ...)
{
    var result = await validator.ValidateAsync(input);
    if (!result.IsValid)
        throw new ValidationException(result.Errors.First().ErrorMessage);
}
```

## Acceptance Criteria

- [ ] Invalid `CreateFolderRequest` → `400 Bad Request` with field-level errors
- [ ] Valid request → passes validation, processed normally
- [ ] Empty `path` → `400` with message "Path is required"
- [ ] Path with `..` → `400` with traversal warning
- [ ] Validators loaded automatically from assembly (no manual registration)

## Test Cases

- **TC-001**: `POST /folders` with empty `path` → `400` with validation error
- **TC-002**: `POST /folders` with `path: "a/b/c"` → validation passes
- **TC-003**: `POST /folders` with `path: "../etc/passwd"` → `400`

## Implementation Tasks

- [ ] Install `FluentValidation.AspNetCore`
- [ ] Configure `AddValidatorsFromAssemblyContaining<Program>()`
- [ ] Create validators for: `CreateFolderRequest`, `MoveFileRequest`, `CopyFileRequest`
- [ ] Create `ValidationProblemDetailsFilter` to format 400 as RFC 7807

## Testing Tasks

- [ ] Unit test each validator rule
- [ ] Integration test: invalid request → 400 with correct error field

## Security Review Checklist

- [ ] Traversal pattern check in `CreateFolderRequestValidator` (`..` blocked)
- [ ] Max length limits prevent buffer attacks

## Code Review Checklist

- [ ] Validators are in `Strg.Api/Validators/` (not co-located with endpoint handlers)
- [ ] `AbstractValidator<T>` not `IValidator<T>` (validator discovery requires base class)
- [ ] Validation duplicates `StoragePath.Parse()` for path inputs (belt-and-suspenders)

## Definition of Done

- [ ] All request types have validators
- [ ] Invalid requests return 400 with field-level errors
