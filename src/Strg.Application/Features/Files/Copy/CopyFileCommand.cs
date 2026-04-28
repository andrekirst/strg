using Mediator;
using Strg.Application.Abstractions;
using Strg.Core;
using Strg.Core.Domain;

namespace Strg.Application.Features.Files.Copy;

/// <summary>
/// Copies a single <see cref="FileItem"/> to a new path, optionally on a different drive. The
/// handler returns <see cref="Result{T}"/> over the freshly-created <see cref="FileItem"/> so the
/// REST shim can project the new row to its DTO and emit <c>201 Created</c> with a Location
/// header that points at the new file. Failure modes (NotFound, InvalidPath, Conflict,
/// DirectoryCopyUnsupported) are flat error codes — the shim maps them to HTTP status.
///
/// <para><b>Quota shortfall is exception-shaped</b> (<see cref="Strg.Core.Exceptions.QuotaExceededException"/>
/// per <c>project_strg032_quota_decisions.md</c> single-phase commit-as-reservation); the endpoint
/// catches it to surface HTTP 507. Audit row is written by <c>AuditLogConsumer</c> via the
/// <c>FileUploadedEvent</c> outbox roundtrip — this command does NOT implement
/// <see cref="IAuditedCommand"/> so the consumer is the single audit-write site (matches
/// <see cref="Move.MoveFileCommand"/>'s audit-via-event posture).</para>
///
/// <para><b>v1.5 limitations.</b> Directory copy is rejected with the dedicated
/// <c>DirectoryCopyUnsupported</c> error code so callers receive a precise reason rather than a
/// generic 500. Mirrors STRG-040's <c>CrossDriveDirectoryUnsupported</c> rejection — directory
/// copy adds N-blob copy + descendant-row insertion in one transaction, deferred to a follow-up.</para>
/// </summary>
public sealed record CopyFileCommand(
    Guid DriveId,
    Guid FileId,
    string TargetPath,
    Guid? TargetDriveId)
    : ICommand<Result<FileItem>>, ITenantScopedCommand;
