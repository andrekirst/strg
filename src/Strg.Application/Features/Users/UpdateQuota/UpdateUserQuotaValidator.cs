using FluentValidation;

namespace Strg.Application.Features.Users.UpdateQuota;

/// <summary>
/// Syntactic validation for <see cref="UpdateUserQuotaCommand"/>. Rejects the
/// <c>00000000-…</c> pseudo-Guid that route binding accepts and a negative quota. Mirrors the
/// existing GraphQL admin handler's negative-quota rejection
/// (<c>AdminMutationHandlers.cs:19</c>) so the two surfaces share a single canonical rule.
/// </summary>
public sealed class UpdateUserQuotaValidator : AbstractValidator<UpdateUserQuotaCommand>
{
    public UpdateUserQuotaValidator()
    {
        RuleFor(c => c.UserId).NotEqual(Guid.Empty);
        RuleFor(c => c.QuotaBytes).GreaterThanOrEqualTo(0);
    }
}
