using Strg.GraphQl.Inputs.Enums;
namespace Strg.GraphQl.Inputs.File;

public sealed record FileSortInput(FileSortField Field, SortDirection Direction);
