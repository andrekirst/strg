using HotChocolate.Data.Filters;
using Strg.Core.Domain;

namespace Strg.GraphQl.Inputs.File;

/// <summary>
/// Tightly-scoped Hot Chocolate filter input for the <c>GetFiles</c> GraphQL query.
/// <see cref="FilterInputType{T}"/>'s default convention reflects every public property on
/// <see cref="FileItem"/> into the filter — including <c>TenantId</c>, <c>StorageKey</c>,
/// <c>Path</c>, etc. — even though those are <c>Ignore</c>d on the output type. That would let
/// a caller filter (and infer existence) by tenant id or storage-key shape.
///
/// <para><c>BindFieldsExplicitly()</c> suppresses the convention; the only field surfaced is
/// <see cref="FileItem.Tags"/> so clients can write <c>where: { tags: { some: { ... } } }</c>.
/// Other filter fields stay on the existing scalar <c>FileFilterInput</c> argument.</para>
/// </summary>
public sealed class FileItemFilterInputType : FilterInputType<FileItem>
{
    protected override void Configure(IFilterInputTypeDescriptor<FileItem> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(f => f.Tags);
    }
}
