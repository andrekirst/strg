using Strg.GraphQL.Inputs.Enums;
namespace Strg.GraphQL.Inputs.File;

public sealed record CopyFileInput(Guid Id, string TargetPath, Guid? TargetDriveId, ConflictResolution? ConflictResolution);
