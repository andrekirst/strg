---
id: STRG-301
title: UserInboxSettings entity
milestone: v0.1
priority: high
status: open
type: implementation
labels: [domain, inbox, identity]
depends_on: [STRG-011, STRG-004]
blocks: [STRG-308]
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-301: UserInboxSettings entity

## Summary

Create a `UserInboxSettings` entity (1:1 with `User`) that holds per-user inbox configuration. For v0.1 the only field is `IsInboxEnabled` (default `true`), which lets a user temporarily bypass inbox rule processing without deleting their rules. The entity is auto-created during user registration.

## Technical Specification

### Domain entity (`src/Strg.Core/Domain/UserInboxSettings.cs`)

```csharp
public sealed class UserInboxSettings : TenantedEntity
{
    public Guid UserId { get; init; }
    public bool IsInboxEnabled { get; set; } = true;
}
```

The entity inherits `TenantedEntity` (soft-delete + tenant isolation apply automatically).

### EF Core configuration (`src/Strg.Infrastructure/Persistence/Configurations/UserInboxSettingsConfiguration.cs`)

```csharp
public class UserInboxSettingsConfiguration : IEntityTypeConfiguration<UserInboxSettings>
{
    public void Configure(EntityTypeBuilder<UserInboxSettings> builder)
    {
        builder.ToTable("user_inbox_settings");

        builder.HasIndex(s => s.UserId).IsUnique();

        builder.Property(s => s.IsInboxEnabled).HasDefaultValue(true);

        builder.HasQueryFilter(s => !s.IsDeleted && s.TenantId == /* tenantContext.TenantId */);
    }
}
```

Add `DbSet<UserInboxSettings> UserInboxSettings => Set<UserInboxSettings>();` to `StrgDbContext`.

### Auto-creation on registration

In the user registration handler (`src/Strg.Infrastructure/Identity/UserRegistrationService.cs` or equivalent), after creating the `User` entity:

```csharp
var inboxSettings = new UserInboxSettings
{
    UserId = user.Id,
    TenantId = user.TenantId
};
_db.UserInboxSettings.Add(inboxSettings);
// SaveChangesAsync called by the caller
```

### GraphQL

Extend the `User` GraphQL type to expose:
- `inboxSettings: UserInboxSettings`

Add mutation:
- `updateInboxSettings(isInboxEnabled: Boolean!): UpdateInboxSettingsPayload!`

### Migration

New migration: `AddUserInboxSettings` that creates the `user_inbox_settings` table.

## Acceptance Criteria

- [ ] `UserInboxSettings` entity exists with `UserId` and `IsInboxEnabled`
- [ ] EF Core global query filter on `UserInboxSettings` enforces tenant isolation + soft-delete
- [ ] Unique index on `UserId` within the table
- [ ] Auto-created with `IsInboxEnabled = true` during user registration
- [ ] `UserInboxSettings` exposed on the `User` GraphQL type
- [ ] `updateInboxSettings` mutation updates the flag and persists
- [ ] Migration `AddUserInboxSettings` applies cleanly

## Test Cases

- TC-001: New user registration → `UserInboxSettings` row created with `IsInboxEnabled = true`
- TC-002: `updateInboxSettings(isInboxEnabled: false)` persists and returns updated settings
- TC-003: Querying `UserInboxSettings` from a different tenant returns `null` (tenant isolation)
- TC-004: Soft-deleted user → `UserInboxSettings` row excluded from all queries

## Implementation Tasks

- [ ] Create `src/Strg.Core/Domain/UserInboxSettings.cs`
- [ ] Create `src/Strg.Infrastructure/Persistence/Configurations/UserInboxSettingsConfiguration.cs`
- [ ] Add `DbSet` to `StrgDbContext`
- [ ] Wire auto-creation in user registration service
- [ ] Create EF Core migration `AddUserInboxSettings`
- [ ] Expose on `User` GraphQL type
- [ ] Add `updateInboxSettings` mutation
- [ ] Write unit + integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-004 tests pass
- [ ] Migration applies cleanly
- [ ] User registration integration test includes `UserInboxSettings` assertion
