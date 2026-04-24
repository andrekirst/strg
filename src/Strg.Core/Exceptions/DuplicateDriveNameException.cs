namespace Strg.Core.Exceptions;

public sealed class DuplicateDriveNameException(string name) : Exception($"A drive named '{name}' already exists.");
