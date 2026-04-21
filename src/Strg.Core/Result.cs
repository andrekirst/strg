namespace Strg.Core;

/// <summary>
/// Outcome of an operation that can fail with a known error code.
/// Prefer <see cref="Result"/> over throwing for validation failures and other expected error paths;
/// reserve exceptions for genuinely exceptional conditions.
/// </summary>
public readonly struct Result : IEquatable<Result>
{
    public bool IsSuccess { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, string? errorCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static Result Success() => new(true, null, null);

    public static Result Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);

    public bool Equals(Result other) =>
        IsSuccess == other.IsSuccess
        && ErrorCode == other.ErrorCode
        && ErrorMessage == other.ErrorMessage;

    public override bool Equals(object? obj) => obj is Result other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IsSuccess, ErrorCode, ErrorMessage);
    public static bool operator ==(Result left, Result right) => left.Equals(right);
    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}

/// <summary>
/// Outcome of an operation that returns a value on success, or an error code on failure.
/// </summary>
public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, T? value, string? errorCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static Result<T> Success(T value) => new(true, value, null, null);

    public static Result<T> Failure(string errorCode, string errorMessage) =>
        new(false, default, errorCode, errorMessage);
}
