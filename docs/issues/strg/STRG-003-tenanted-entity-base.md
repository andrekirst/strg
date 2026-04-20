---
id: STRG-003
title: Create TenantedEntity base class and domain model foundation
milestone: v0.1
priority: critical
status: open
type: implementation
labels: [core, domain, entities]
depends_on: [STRG-001]
blocks: [STRG-004, STRG-011, STRG-021, STRG-031, STRG-046]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-003: Create TenantedEntity base class and domain model foundation

## Summary

Create the base domain classes that all entities in `Strg.Core` inherit from: `TenantedEntity`, `Entity`, and domain value types. These establish the multi-tenancy scaffolding and audit timestamps on every entity.

## Background / Context

Every entity in strg carries a `TenantId` for future multi-tenant isolation and `CreatedAt`/`UpdatedAt` for audit purposes. A shared base class ensures consistency and prevents forgetting these fields on new entities. This is a foundational class — all other domain entities depend on it.

## Technical Specification

### File: `src/Strg.Core/Domain/Entity.cs`

```csharp
namespace Strg.Core.Domain;

public abstract class Entity
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
```

### File: `src/Strg.Core/Domain/TenantedEntity.cs`

```csharp
namespace Strg.Core.Domain;

public abstract class TenantedEntity : Entity
{
    public Guid TenantId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt.HasValue;
}
```

### File: `src/Strg.Core/Domain/Tenant.cs`

```csharp
namespace Strg.Core.Domain;

public sealed class Tenant : Entity
{
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

### File: `src/Strg.Core/Domain/IDomainEvent.cs`

Marker interface for domain events (used by outbox).

```csharp
namespace Strg.Core.Domain;

public interface IDomainEvent { }
```

## Acceptance Criteria

- [ ] `Entity` base class exists with `Guid Id`
- [ ] `TenantedEntity` extends `Entity` with `TenantId`, `CreatedAt`, `UpdatedAt`, `DeletedAt`, `IsDeleted`
- [ ] `Tenant` entity exists
- [ ] `IDomainEvent` marker interface exists
- [ ] All classes are in `Strg.Core.Domain` namespace
- [ ] `Strg.Core` has zero external NuGet dependencies after this issue
- [ ] `TenantedEntity.UpdatedAt` is mutable (for EF Core update tracking)
- [ ] `TenantedEntity.CreatedAt` is immutable (`init`)
- [ ] `TenantedEntity.Id` is immutable (`init`)

## Test Cases

- **TC-001**: `new ConcreteEntity()` → `Id` is a non-empty Guid
- **TC-002**: `new ConcreteEntity()` → `CreatedAt` and `UpdatedAt` are close to `UtcNow`
- **TC-003**: `entity.IsDeleted` → `false` when `DeletedAt` is null; `true` when set
- **TC-004**: Setting `entity.Id = anotherGuid` → compile error (init-only)
- **TC-005**: `TenantedEntity` has no EF Core package reference (pure domain)

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/Entity.cs`
- [ ] Create `src/Strg.Core/Domain/TenantedEntity.cs`
- [ ] Create `src/Strg.Core/Domain/Tenant.cs`
- [ ] Create `src/Strg.Core/Domain/IDomainEvent.cs`
- [ ] Write unit tests in `Strg.Core.Tests`
- [ ] Verify `Strg.Core` has no external package references

## Testing Tasks

- [ ] Test `IsDeleted` property in `Strg.Core.Tests/Domain/TenantedEntityTests.cs`
- [ ] Test that `Id` is always non-empty Guid on construction
- [ ] Test `CreatedAt` is approximately `UtcNow` on construction

## Security Review Checklist

- [ ] `TenantId` is always required (cannot be default Guid) — consider adding guard in EF Core configuration
- [ ] No business logic in base entities that could be bypassed

## Code Review Checklist

- [ ] All properties follow C# naming conventions (PascalCase)
- [ ] `init` used appropriately for immutable properties
- [ ] Namespace is `Strg.Core.Domain`
- [ ] File-scoped namespaces used

## Definition of Done

- [ ] All 4 files created
- [ ] Unit tests pass
- [ ] Zero external dependencies in Strg.Core
