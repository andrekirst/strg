using Strg.Core.Domain;
namespace Strg.GraphQl.Inputs.Tag;

public sealed record AddTagInput(Guid FileId, string Key, string Value, TagValueType ValueType);
