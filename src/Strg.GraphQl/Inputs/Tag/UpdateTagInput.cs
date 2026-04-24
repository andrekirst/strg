using Strg.Core.Domain;
namespace Strg.GraphQl.Inputs.Tag;

public sealed record UpdateTagInput(Guid Id, string Value, TagValueType ValueType);
