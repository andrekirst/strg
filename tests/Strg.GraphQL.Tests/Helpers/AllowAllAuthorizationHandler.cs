using HotChocolate.Authorization;
using HotChocolate.Resolvers;

namespace Strg.GraphQL.Tests.Helpers;

internal sealed class AllowAllAuthorizationHandler : IAuthorizationHandler
{
    public ValueTask<AuthorizeResult> AuthorizeAsync(
        IMiddlewareContext context,
        AuthorizeDirective directive,
        CancellationToken cancellationToken = default)
        => new(AuthorizeResult.Allowed);

    public ValueTask<AuthorizeResult> AuthorizeAsync(
        AuthorizationContext context,
        IReadOnlyList<AuthorizeDirective> directives,
        CancellationToken cancellationToken = default)
        => new(AuthorizeResult.Allowed);
}
