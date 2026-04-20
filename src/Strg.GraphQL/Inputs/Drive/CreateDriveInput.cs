namespace Strg.GraphQL.Inputs.Drive;

public sealed record CreateDriveInput(string Name, string ProviderType, string ProviderConfig, bool? IsDefault, bool? IsEncrypted);
