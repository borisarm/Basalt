using System.Diagnostics;
using Basalt.Application.Common;
using Basalt.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Basalt.Application.Abstractions;

/// <summary>Logs the use case name, duration and error code. Never logs request data (it may contain note content).</summary>
public sealed partial class LoggingHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingHandlerDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.HandleAsync(command, ct).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (result.IsSuccess)
        {
            LogSucceeded(logger, typeof(TCommand).Name, elapsed);
        }
        else
        {
            LogFailed(logger, typeof(TCommand).Name, result.Error.Code, elapsed);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{UseCase} succeeded in {ElapsedMs:F1} ms")]
    private static partial void LogSucceeded(ILogger logger, string useCase, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{UseCase} failed with {ErrorCode} in {ElapsedMs:F1} ms")]
    private static partial void LogFailed(ILogger logger, string useCase, string errorCode, double elapsedMs);
}

/// <summary>Runs every registered <see cref="IValidator{TRequest}"/> and stops at the first error.</summary>
public sealed class ValidationHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct)
    {
        foreach (var validator in validators)
        {
            if (validator.Validate(command) is { } error)
            {
                return Task.FromResult(Result.Failure<TResult>(error));
            }
        }

        return inner.HandleAsync(command, ct);
    }
}

/// <summary>Serialises commands of the same vault (spec 01 §4.1); commands of other vaults run in parallel.</summary>
public sealed class VaultLockDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    VaultLocks locks) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct)
    {
        if (command is not IVaultRequest vaultRequest)
        {
            return await inner.HandleAsync(command, ct).ConfigureAwait(false);
        }

        using var lease = await locks.WriteAsync(vaultRequest.VaultId, ct).ConfigureAwait(false);
        return await inner.HandleAsync(command, ct).ConfigureAwait(false);
    }
}
