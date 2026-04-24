namespace Strg.Core.Exceptions;

public sealed class ValidationException(string message, string? propertyName = null) : Exception(message)
{
    public string? PropertyName { get; } = propertyName;
}
