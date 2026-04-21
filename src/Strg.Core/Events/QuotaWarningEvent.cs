using Strg.Core.Domain;

namespace Strg.Core.Events;

public sealed record QuotaWarningEvent(
    Guid TenantId, Guid UserId, long UsedBytes, long QuotaBytes
) : IDomainEvent;
