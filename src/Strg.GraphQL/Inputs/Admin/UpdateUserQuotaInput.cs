namespace Strg.GraphQL.Inputs.Admin;
public sealed record UpdateUserQuotaInput(Guid UserId, long QuotaBytes);
