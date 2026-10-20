using Basalt.Application.Bookmarks;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Ports.Storage;

/// <summary>Identity and shared settings of a vault, stored in <c>.basalt/vault.json</c> (synchronised).</summary>
public sealed record VaultConfig(VaultId Id, VaultSettings Settings);

/// <summary>Typed access to the JSON files inside a vault's <c>.basalt/</c> folder (spec 03 §1.6, 08 §1).</summary>
public interface IVaultConfigStore
{
    /// <summary>Loads <c>.basalt/vault.json</c>; <see cref="DocumentLoadStatus.Missing"/>/<see cref="DocumentLoadStatus.Invalid"/>
    /// return a null value.</summary>
    Task<DocumentLoad<VaultConfig?>> LoadVaultConfigAsync(IVaultFileSystem vault, CancellationToken ct);

    Task<Result<Unit>> SaveVaultConfigAsync(IVaultFileSystem vault, VaultConfig config, CancellationToken ct);

    /// <summary>Loads <c>.basalt/bookmarks.json</c>; missing or invalid documents return <see cref="BookmarksDocument.Empty"/>.</summary>
    Task<DocumentLoad<BookmarksDocument>> LoadBookmarksAsync(IVaultFileSystem vault, CancellationToken ct);

    /// <summary>Saves the bookmarks. If the stored document is invalid (e.g. sync conflict markers) it is kept aside first,
    /// never overwritten (spec 08 §1.1, ADR-0014).</summary>
    Task<Result<Unit>> SaveBookmarksAsync(IVaultFileSystem vault, BookmarksDocument bookmarks, CancellationToken ct);

    /// <summary>Loads <c>.basalt/workspace.{deviceId}.json</c>; missing or invalid documents return null.</summary>
    Task<DocumentLoad<WorkspaceState?>> LoadWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, CancellationToken ct);

    Task<Result<Unit>> SaveWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, WorkspaceState workspace, CancellationToken ct);
}

/// <summary>Recently opened files of a vault on this device (<c>{AppData}/vaults/{vaultId}/recent.json</c>, F-011).</summary>
public interface IRecentFilesStore
{
    /// <summary>Most recent first; missing or invalid documents return an empty list.</summary>
    Task<IReadOnlyList<VaultPath>> LoadAsync(VaultId vault, CancellationToken ct);

    Task<Result<Unit>> SaveAsync(VaultId vault, IReadOnlyList<VaultPath> files, CancellationToken ct);
}
