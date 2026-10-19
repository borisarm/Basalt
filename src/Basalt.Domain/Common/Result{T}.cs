namespace Basalt.Domain.Common;

/// <summary>Outcome of an operation that can fail in an expected way (spec 01 §4.2).</summary>
public readonly record struct Result<T>
{
    private static readonly Error Uninitialized = new(DomainErrors.ResultUninitialized, "Result was not initialized.");

    private readonly T _value;
    private readonly Error? _error;

    private Result(T value, Error? error, bool isSuccess)
    {
        _value = value;
        _error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>The value. Throws <see cref="InvalidOperationException"/> when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"Cannot access the value of a failed result ({Error}).");

    /// <summary>The error. Throws <see cref="InvalidOperationException"/> when the result is a success.</summary>
    public Error Error => IsSuccess
        ? throw new InvalidOperationException("Cannot access the error of a successful result.")
        : _error ?? Uninitialized;

    public static Result<T> Success(T value) => new(value, null, true);

    public static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default!, error, false);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return IsSuccess ? Result<TOut>.Success(f(_value)) : Result<TOut>.Failure(Error);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return IsSuccess ? f(_value) : Result<TOut>.Failure(Error);
    }

    public async Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        // Stryker disable once Boolean : ConfigureAwait(false) is not observable by tests (equivalent mutant)
        return IsSuccess ? await f(_value).ConfigureAwait(false) : Result<TOut>.Failure(Error);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value) : onFailure(Error);
    }

    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({Error})";
}
