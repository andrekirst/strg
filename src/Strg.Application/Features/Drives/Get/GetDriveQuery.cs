using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.Get;

/// <summary>
/// Returns a single tenant-scoped drive by id, or <see langword="null"/> when no drive matches.
/// Null-return rather than NotFoundException preserves the current REST / GraphQL wire contract
/// where callers already handle "drive not found" inline; future Phase-5 cleanup can revisit.
/// </summary>
public sealed record GetDriveQuery(Guid Id) : IQuery<Drive?>;
