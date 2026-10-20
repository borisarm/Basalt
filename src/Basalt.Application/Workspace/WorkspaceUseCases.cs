using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Workspace;

/// <summary>Recently opened files of a vault on this device (F-011): most recent first, at most <see cref="Capacity"/>.</summary>
public sealed class RecentFiles(VaultId vault, IRecentFilesStore store) : IAsyncDisposable
{
    public const int Capacity = 50;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<VaultPath>> LoadAsync(CancellationToken ct) => store.LoadAsync(vault, ct);

    public Task<Result<Unit>> RecordAsync(VaultPath path, CancellationToken ct) =>
        UpdateAsync(files => [path, .. files.Where(f => f != path).Take(Capacity - 1)], ct);

    public Task<Result<Unit>> RenameAsync(VaultPath from, VaultPath to, CancellationToken ct) =>
        UpdateAsync(files => [.. files.Select(f => f == from ? to : f.IsDescendantOf(from) ? to.Combine(f.RelativeTo(from)).Value : f)], ct);

    public Task<Result<Unit>> RemoveAsync(IReadOnlyCollection<VaultPath> removed, CancellationToken ct) =>
        UpdateAsync(files => [.. files.Where(f => !removed.Any(r => f == r || f.IsDescendantOf(r)))], ct);

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Result<Unit>> UpdateAsync(Func<IReadOnlyList<VaultPath>, IReadOnlyList<VaultPath>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await store.LoadAsync(vault, ct).ConfigureAwait(false);
            var updated = change(current);
            return updated.SequenceEqual(current) ? Result.Success() : await store.SaveAsync(vault, updated, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Called when a file is opened in the editor.</summary>
public sealed record RecordRecentFile(VaultId VaultId, VaultPath Path) : ICommand<Unit>, IVaultRequest;

public sealed class RecordRecentFileHandler(IVaultSessions sessions) : ICommandHandler<RecordRecentFile, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordRecentFile command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        return session.IsFailure ? session.Error : await session.Value.Get<RecentFiles>().RecordAsync(command.Path, ct).ConfigureAwait(false);
    }
}

public sealed record ListRecentFiles(VaultId VaultId) : IQuery<IReadOnlyList<VaultPath>>, IVaultRequest;

public sealed class ListRecentFilesHandler(IVaultSessions sessions) : IQueryHandler<ListRecentFiles, IReadOnlyList<VaultPath>>
{
    public async Task<Result<IReadOnlyList<VaultPath>>> HandleAsync(ListRecentFiles query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        return session.IsFailure ? session.Error : Result.Success(await session.Value.Get<RecentFiles>().LoadAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>The saved layout of this device, or null when there is none (or it cannot be read).</summary>
public sealed record LoadWorkspace(VaultId VaultId) : IQuery<WorkspaceState?>, IVaultRequest;

public sealed class LoadWorkspaceHandler(IVaultSessions sessions, IVaultConfigStore store, IDeviceInfo device) : IQueryHandler<LoadWorkspace, WorkspaceState?>
{
    public async Task<Result<WorkspaceState?>> HandleAsync(LoadWorkspace query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var load = await store.LoadWorkspaceAsync(session.Value.FileSystem, device.DeviceId, ct).ConfigureAwait(false);
        return load.Value;
    }
}

public sealed record SaveWorkspace(VaultId VaultId, WorkspaceState State) : ICommand<Unit>, IVaultRequest;

public sealed class SaveWorkspaceHandler(IVaultSessions sessions, IVaultConfigStore store, IDeviceInfo device) : ICommandHandler<SaveWorkspace, Unit>
{
    public async Task<Result<Unit>> HandleAsync(SaveWorkspace command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        return session.IsFailure
            ? session.Error
            : await store.SaveWorkspaceAsync(session.Value.FileSystem, device.DeviceId, command.State, ct).ConfigureAwait(false);
    }
}
