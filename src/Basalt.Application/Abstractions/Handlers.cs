using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Abstractions;

/// <summary>A use case that changes state (spec 01 §4.1).</summary>
public interface ICommand<TResult>;

/// <summary>A use case that only reads.</summary>
public interface IQuery<TResult>;

/// <summary>Commands/queries that operate on one vault (used by <c>VaultLockDecorator</c> and logging scopes).</summary>
public interface IVaultRequest
{
    VaultId VaultId { get; }
}

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken ct);
}

/// <summary>Validates a request before its handler runs; returns null when valid.</summary>
public interface IValidator<in TRequest>
{
    Error? Validate(TRequest request);
}
