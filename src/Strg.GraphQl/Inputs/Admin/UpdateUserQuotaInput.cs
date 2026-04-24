namespace Strg.GraphQl.Inputs.Admin;

public sealed record UpdateUserQuotaInput(Guid UserId, long QuotaBytes);
