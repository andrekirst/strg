namespace Strg.Api.Endpoints;

/// <summary>
/// Request body for the STRG-040 file-move endpoint. <see cref="TargetPath"/> is required and
/// MUST go through <c>StoragePath.Parse</c> in the handler before reaching any storage
/// provider — never trust the raw client value. <see cref="TargetDriveId"/> is optional; when
/// omitted the move stays in the source drive (the route's <c>{driveId}</c> is reused).
/// </summary>
public sealed record MoveFileRequest(string TargetPath, Guid? TargetDriveId);
