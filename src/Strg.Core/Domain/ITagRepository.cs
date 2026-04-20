namespace Strg.Core.Domain;

public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByFileAsync(Guid fileId, Guid userId, CancellationToken cancellationToken = default);
    Task<Tag?> GetByKeyAsync(Guid fileId, Guid userId, string key, CancellationToken cancellationToken = default);
    Task UpsertAsync(Tag tag, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid fileId, Guid userId, string key, CancellationToken cancellationToken = default);
    Task RemoveAllAsync(Guid fileId, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tag>> SearchAsync(Guid tenantId, Guid userId, string? key, string? value, CancellationToken cancellationToken = default);
}
