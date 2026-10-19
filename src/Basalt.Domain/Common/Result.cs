namespace Basalt.Domain.Common;

/// <summary>Non-generic helpers for <see cref="Result{T}"/>.</summary>
#pragma warning disable CA1716 // "Result" mirrors the specified API
public static class Result
#pragma warning restore CA1716
{
    public static Result<Unit> Success() => Result<Unit>.Success(Unit.Value);

    public static Result<Unit> Failure(Error error) => Result<Unit>.Failure(error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);
}
