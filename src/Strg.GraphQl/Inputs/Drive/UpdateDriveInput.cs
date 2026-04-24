namespace Strg.GraphQl.Inputs.Drive;

public sealed record UpdateDriveInput(Guid Id, string? Name, bool? IsDefault);
