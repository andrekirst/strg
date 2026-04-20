namespace Strg.GraphQL.Inputs.Drive;

public sealed record UpdateDriveInput(Guid Id, string? Name, bool? IsDefault);
