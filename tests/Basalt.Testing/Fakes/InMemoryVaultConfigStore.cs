using System.Collections.Concurrent;
using Basalt.Application.Bookmarks;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="IVaultConfigStore"/> keyed by vault root location.</summary>
public sealed class InMemoryVaultConfigStore : IVaultConfigStore
{
    private readonly ConcurrentDictionary<string, VaultConfig> _configs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _corrupt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BookmarksDocument> _bookmarks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _corruptBookmarks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Root, Guid Device), WorkspaceState> _workspaces = new();

    public void Corrupt(IVaultFileSystem vault) => _corrupt[vault.RootLocation] = true;

    public void CorruptBookmarks(IVaultFileSystem vault) => _corruptBookmarks[vault.RootLocation] = true;

    public Task<DocumentLoad<VaultConfig?>> LoadVaultConfigAsync(IVaultFileSystem vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (_corrupt.ContainsKey(vault.RootLocation))
        {
            return Task.FromResult(new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Invalid, "corrupt"));
        }

        return Task.FromResult(_configs.TryGetValue(vault.RootLocation, out var config)
            ? new DocumentLoad<VaultConfig?>(config, DocumentLoadStatus.Loaded)
            : new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Missing));
    }

    public Task<Result<Unit>> SaveVaultConfigAsync(IVaultFileSystem vault, VaultConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _configs[vault.RootLocation] = config;
        _corrupt.TryRemove(vault.RootLocation, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<DocumentLoad<BookmarksDocument>> LoadBookmarksAsync(IVaultFileSystem vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (_corruptBookmarks.ContainsKey(vault.RootLocation))
        {
            return Task.FromResult(new DocumentLoad<BookmarksDocument>(BookmarksDocument.Empty, DocumentLoadStatus.Invalid, "corrupt"));
        }

        return Task.FromResult(_bookmarks.TryGetValue(vault.RootLocation, out var bookmarks)
            ? new DocumentLoad<BookmarksDocument>(bookmarks, DocumentLoadStatus.Loaded)
            : new DocumentLoad<BookmarksDocument>(BookmarksDocument.Empty, DocumentLoadStatus.Missing));
    }

    public Task<Result<Unit>> SaveBookmarksAsync(IVaultFileSystem vault, BookmarksDocument bookmarks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _bookmarks[vault.RootLocation] = bookmarks;
        _corruptBookmarks.TryRemove(vault.RootLocation, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<DocumentLoad<WorkspaceState?>> LoadWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return Task.FromResult(_workspaces.TryGetValue((vault.RootLocation, deviceId), out var workspace)
            ? new DocumentLoad<WorkspaceState?>(workspace, DocumentLoadStatus.Loaded)
            : new DocumentLoad<WorkspaceState?>(null, DocumentLoadStatus.Missing));
    }

    public Task<Result<Unit>> SaveWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, WorkspaceState workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _workspaces[(vault.RootLocation, deviceId)] = workspace;
        return Task.FromResult(Result.Success());
    }
}

/// <summary>In-memory <see cref="IRecentFilesStore"/>.</summary>
public sealed class InMemoryRecentFilesStore : IRecentFilesStore
{
    private readonly ConcurrentDictionary<VaultId, IReadOnlyList<VaultPath>> _files = new();

    public void Corrupt(VaultId vault) => _files.TryRemove(vault, out _);

    public Task<IReadOnlyList<VaultPath>> LoadAsync(VaultId vault, CancellationToken ct) =>
        Task.FromResult(_files.GetValueOrDefault(vault) ?? []);

    public Task<Result<Unit>> SaveAsync(VaultId vault, IReadOnlyList<VaultPath> files, CancellationToken ct)
    {
        _files[vault] = [.. files];
        return Task.FromResult(Result.Success());
    }
}
