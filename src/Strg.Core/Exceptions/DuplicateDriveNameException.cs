namespace Strg.Core.Exceptions;

public sealed class DuplicateDriveNameException : Exception
{
    public DuplicateDriveNameException(string name)
        : base($"A drive named '{name}' already exists.") { }
}
