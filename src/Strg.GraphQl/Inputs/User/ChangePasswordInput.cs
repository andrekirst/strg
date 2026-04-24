namespace Strg.GraphQl.Inputs.User;

public sealed record ChangePasswordInput(string CurrentPassword, string NewPassword);
