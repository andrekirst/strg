namespace Strg.GraphQL.Inputs.User;
public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
