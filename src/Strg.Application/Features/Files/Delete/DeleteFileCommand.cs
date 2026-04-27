using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;

namespace Strg.Application.Features.Files.Delete;

/// <summary>
/// Soft-deletes a single <see cref="Strg.Core.Domain.FileItem"/>. When the target is a
/// directory, every descendant under its path prefix is soft-deleted in the same EF
/// transaction. A <c>FileDeletedEvent</c> is published via the MassTransit outbox; the audit
/// row is written by <c>AuditLogConsumer</c> in response to that event, NOT by
/// <c>IAuditScope</c> here.
///
/// <para><b>Marker rationale.</b> <see cref="ITenantScopedCommand"/> rejects calls without a
/// bound tenant before the handler runs (the global query filter would also mask
/// foreign-tenant access as null, but the marker fails fast on a missing JWT). This command
/// deliberately does NOT implement <see cref="IAuditedCommand"/> — the audit row flows
/// through the outbox consumer; layering <c>AuditBehavior</c> on top would produce a second
/// audit row per delete.</para>
/// </summary>
public sealed record DeleteFileCommand(Guid DriveId, Guid FileId)
    : ICommand<Result>, ITenantScopedCommand;
