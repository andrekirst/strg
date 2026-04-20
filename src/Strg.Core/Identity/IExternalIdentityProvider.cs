namespace Strg.Core.Identity;

public sealed record ExternalIdentityClaim(
    string ProviderName,
    string ExternalId,
    string Email,
    string DisplayName);

public interface IExternalIdentityProvider
{
    string ProviderName { get; }
    Task<ExternalIdentityClaim?> AuthenticateAsync(string code, CancellationToken cancellationToken);
}
