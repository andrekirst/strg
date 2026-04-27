namespace Strg.Api.Endpoints;

/// <summary>
/// Request body for <c>POST /api/v1/drives/{driveId}/folders</c>. The <see cref="Path"/> is the
/// virtual storage path of the leaf folder; intermediate segments are auto-created by the
/// endpoint as virtual <c>FileItem(IsDirectory=true)</c> rows. Validated through
/// <see cref="Strg.Core.Storage.StoragePath.Parse"/> — traversal attempts surface as a 400.
/// </summary>
public sealed record CreateFolderRequest(string Path);
