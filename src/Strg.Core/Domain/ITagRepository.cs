namespace Strg.Core.Domain;

public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByFileAsync(Guid fileId, Guid userId, CancellationToken ct = default);
    Task<Tag?> GetByKeyAsync(Guid fileId, Guid userId, string key, CancellationToken ct = default);
    Task UpsertAsync(Tag tag, CancellationToken ct = default);
    Task RemoveAsync(Guid fileId, Guid userId, string key, CancellationToken ct = default);
    Task RemoveAllAsync(Guid fileId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> SearchAsync(Guid tenantId, Guid userId, string? key, string? value, CancellationToken ct = default);
}
