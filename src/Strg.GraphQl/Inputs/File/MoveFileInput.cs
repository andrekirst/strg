using Strg.GraphQl.Inputs.Enums;
namespace Strg.GraphQl.Inputs.File;

public sealed record MoveFileInput(Guid Id, string TargetPath, Guid? TargetDriveId, ConflictResolution? ConflictResolution);
