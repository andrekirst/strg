using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Files.Move;

/// <summary>
/// Moves a single <see cref="FileItem"/> to a new path within the same drive. The handler
/// returns <see cref="Result{T}"/> over <see cref="FileItem"/> so the endpoint can project the
/// post-move row to its DTO without a second DB round-trip. Failure modes (NotFound, InvalidPath,
/// Conflict, CrossDriveUnsupported, DirectoryMoveUnsupported) are flat error codes — the REST
/// shim maps them to HTTP status. Audit row is written by <c>AuditLogConsumer</c> via the
/// <c>FileMovedEvent</c> outbox roundtrip — this command deliberately does NOT implement
/// <see cref="IAuditedCommand"/> so the consumer is the single audit-write site (matches
/// <c>DeleteFileCommand</c>'s audit-via-event posture).
///
/// <para><b>v1 limitations.</b> Cross-drive moves and directory moves are rejected with
/// dedicated error codes so callers receive a precise reason rather than a generic 500. The
/// follow-up trackers carry the deferred work (cross-drive copy-then-delete, descendant path
/// rewrite) — see the source-issue close comment.</para>
/// </summary>
public sealed record MoveFileCommand(
    Guid DriveId,
    Guid FileId,
    string TargetPath,
    Guid? TargetDriveId)
    : ICommand<Result<FileItem>>, ITenantScopedCommand;
