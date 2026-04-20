using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Strg.GraphQL.Inputs.Tag;
using DomainTag = Strg.Core.Domain.Tag;
using Strg.GraphQL.Payloads;
using Strg.GraphQL.Payloads.Tag;
using Strg.Infrastructure.Data;

namespace Strg.GraphQL.Mutations.Storage;

[ExtendObjectType<StorageMutations>]
public sealed class TagMutations
{
    [Authorize(Policy = "TagsWrite")]
    public async Task<AddTagPayload> AddTagAsync(
        AddTagInput input,
        [Service] StrgDbContext db,
        [GlobalState("tenantId")] Guid tenantId,
        [GlobalState("userId")] Guid userId,
        CancellationToken ct)
    {
        if (input.Key.Length > 255)
            return new AddTagPayload(null, [new UserError("VALIDATION_ERROR", "key must be ≤255 chars.", "key")]);
        if (input.Value.Length > 255)
            return new AddTagPayload(null, [new UserError("VALIDATION_ERROR", "value must be ≤255 chars.", "value")]);

        var fileExists = await db.Files.AnyAsync(f => f.Id == input.FileId, ct);
        if (!fileExists)
            return new AddTagPayload(null, [new UserError("NOT_FOUND", "File not found.", null)]);

        var existing = await db.Tags.FirstOrDefaultAsync(
            t => t.FileId == input.FileId && t.Key == input.Key, ct);

        if (existing is not null)
        {
            existing.Value = input.Value;
            existing.ValueType = input.ValueType;
            await db.SaveChangesAsync(ct);
            return new AddTagPayload(existing, null);
        }

        var tag = new DomainTag
        {
            TenantId = tenantId,
            FileId = input.FileId,
            UserId = userId,
            Key = input.Key,
            Value = input.Value,
            ValueType = input.ValueType
        };

        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        return new AddTagPayload(tag, null);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<UpdateTagPayload> UpdateTagAsync(
        UpdateTagInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == input.Id, ct);
        if (tag is null)
            return new UpdateTagPayload(null, [new UserError("NOT_FOUND", "Tag not found.", null)]);

        tag.Value = input.Value;
        tag.ValueType = input.ValueType;
        await db.SaveChangesAsync(ct);
        return new UpdateTagPayload(tag, null);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveTagPayload> RemoveTagAsync(
        RemoveTagInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == input.Id, ct);
        if (tag is not null)
        {
            tag.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new RemoveTagPayload(input.Id, null);
    }

    [Authorize(Policy = "TagsWrite")]
    public async Task<RemoveAllTagsPayload> RemoveAllTagsAsync(
        RemoveAllTagsInput input,
        [Service] StrgDbContext db,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Tags
            .Where(t => t.FileId == input.FileId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedAt, now), ct);
        return new RemoveAllTagsPayload(input.FileId, null);
    }
}
