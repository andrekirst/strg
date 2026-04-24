using Strg.GraphQL.Mutations.Admin;
using Strg.GraphQL.Mutations.Storage;
using Strg.GraphQL.Mutations.User;

namespace Strg.GraphQL.Mutations;

[ExtendObjectType("Mutation")]
public sealed class RootMutationExtension
{
    public StorageMutations Storage() => new();
    public UserMutations User() => new();
    public AdminMutations Admin() => new();
    public InboxMutations Inbox() => new();
}

public sealed record InboxMutations
{
    public bool Placeholder => false;
}
