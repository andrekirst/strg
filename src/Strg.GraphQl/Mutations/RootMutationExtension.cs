using Strg.GraphQl.Mutations.Admin;
using Strg.GraphQl.Mutations.Storage;
using Strg.GraphQl.Mutations.User;

namespace Strg.GraphQl.Mutations;

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
