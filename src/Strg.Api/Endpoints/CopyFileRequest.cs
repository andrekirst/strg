namespace Strg.Api.Endpoints;

/// <summary>
/// Request body for the STRG-041 copy endpoint.
///
/// <para><b>TargetPath</b> is the destination path within <see cref="TargetDriveId"/> (or the route
/// drive when omitted). User-supplied input — gated by <c>StoragePath.Parse</c> in the handler so
/// traversal, null bytes, reserved names, and absolute/UNC inputs are rejected before they reach
/// the storage provider.</para>
///
/// <para><b>TargetDriveId</b> is optional. <c>null</c> means "copy within the route drive"; a
/// non-null value enables cross-drive copy. The destination drive must exist and belong to the
/// caller's tenant — the global query filter on <c>StrgDbContext.Drives</c> enforces that
/// transitively when <c>IDriveRepository.GetByIdAsync</c> is called against the resolved id.</para>
/// </summary>
public sealed record CopyFileRequest(string TargetPath, Guid? TargetDriveId);
