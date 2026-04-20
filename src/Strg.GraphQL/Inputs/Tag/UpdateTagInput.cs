using Strg.Core.Domain;
namespace Strg.GraphQL.Inputs.Tag;
public sealed record UpdateTagInput(Guid Id, string Value, TagValueType ValueType);
