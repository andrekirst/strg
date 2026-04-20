namespace Strg.Core.Exceptions;

public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException() : base("Storage quota exceeded.") { }
}
