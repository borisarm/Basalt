using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Commit history of one file (spec 03 §2 and 06 §13).</summary>
public sealed record GetFileHistory(VaultId VaultId, VaultPath Path, int Max = 50) : IQuery<IReadOnlyList<RemoteCommitInfo>>, IVaultRequest;

public sealed record GetFileVersion(VaultId VaultId, VaultPath Path, CommitSha Commit) : IQuery<byte[]?>, IVaultRequest;

/// <summary>Writes the old content as a normal local edit; the next sync publishes it (spec 06 §13).</summary>
public sealed record RestoreFileVersion(VaultId VaultId, VaultPath Path, CommitSha Commit) : ICommand<Unit>, IVaultRequest;

public sealed class GetFileHistoryHandler(IVaultSessions sessions)
    : IQueryHandler<GetFileHistory, IReadOnlyList<RemoteCommitInfo>>
{
    public Task<Result<IReadOnlyList<RemoteCommitInfo>>> HandleAsync(GetFileHistory query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Remote(sessions, query.VaultId, repository =>
            repository.GetFileHistoryAsync(query.Path, Math.Max(1, query.Max), ct));
    }

    internal static Task<Result<T>> Remote<T>(
        IVaultSessions sessions,
        VaultId vault,
        Func<IRemoteRepository, Task<Result<T>>> action)
    {
        var session = sessions.Get(vault);
        if (session.IsFailure)
        {
            return Task.FromResult(Result<T>.Failure(session.Error));
        }

        return session.Value.TryGet<IRemoteRepository>() is { } repository
            ? action(repository)
            : Task.FromResult(Result<T>.Failure(
                new Error(SyncErrors.NotConfigured, "The vault is not configured for sync.")));
    }
}

public sealed class GetFileVersionHandler(IVaultSessions sessions) : IQueryHandler<GetFileVersion, byte[]?>
{
    public Task<Result<byte[]?>> HandleAsync(GetFileVersion query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetFileHistoryHandler.Remote(sessions, query.VaultId, repository =>
            repository.GetFileAtCommitAsync(query.Path, query.Commit, ct));
    }
}

public sealed class RestoreFileVersionHandler(IVaultSessions sessions)
    : ICommandHandler<RestoreFileVersion, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RestoreFileVersion command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var repository = session.Value.TryGet<IRemoteRepository>();
        if (repository is null)
        {
            return new Error(SyncErrors.NotConfigured, "The vault is not configured for sync.");
        }

        var content = await repository.GetFileAtCommitAsync(command.Path, command.Commit, ct).ConfigureAwait(false);
        if (content.IsFailure)
        {
            return content.Error;
        }

        return content.Value is null
            ? new Error(RemoteErrors.NotFound, "The file does not exist in that commit.")
            : await session.Value.FileSystem.WriteAllBytesAsync(command.Path, content.Value, null, ct).ConfigureAwait(false);
    }
}
