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

/// <summary>
/// Outcome of an operation that returns <typeparamref name="T"/> on success, or a typed
/// <typeparamref name="TError"/> payload on failure. Sibling of <see cref="Result{T}"/> —
/// chosen by callers whose failure modes carry data the caller must branch on (e.g.,
/// <c>RangeNotSatisfiable(long Size)</c> for HTTP 416). The string-coded
/// <see cref="Result{T}"/> remains the default for handlers whose failures are flat.
///
/// <para>Equality dispatches through <see cref="EqualityComparer{TError}.Default"/> so an
/// abstract-record discriminated union (sealed sub-records) compares structurally — the
/// canonical shape of <typeparamref name="TError"/>.</para>
///
/// <para><b>Audit pipeline integration.</b> <c>AuditBehavior.BuildIsSuccessReader</c>
/// reflects on this open generic via <c>typeof(Result&lt;,&gt;)</c> and binds to
/// <see cref="IsSuccess"/>; without that branch, audit emission would silently treat every
/// failure as success.</para>
/// </summary>
public readonly struct Result<T, TError> : IEquatable<Result<T, TError>>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public TError? Error { get; }

    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, T? value, TError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static Result<T, TError> Success(T value) => new(true, value, default);

    public static Result<T, TError> Failure(TError error) => new(false, default, error);

    public bool Equals(Result<T, TError> other) =>
        IsSuccess == other.IsSuccess
        && EqualityComparer<T?>.Default.Equals(Value, other.Value)
        && EqualityComparer<TError?>.Default.Equals(Error, other.Error);

    public override bool Equals(object? obj) => obj is Result<T, TError> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IsSuccess, Value, Error);
    public static bool operator ==(Result<T, TError> left, Result<T, TError> right) => left.Equals(right);
    public static bool operator !=(Result<T, TError> left, Result<T, TError> right) => !left.Equals(right);
}
