using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Platform.Json;

/// <summary>Per-vault profile in <c>{AppData}/vaults/{id}/sync/profile.json</c> (spec 06 §2).</summary>
internal sealed class JsonSyncProfileStore(IVaultLocalDataStore localData, TimeProvider time) : ISyncProfileStore
{
    public async Task<SyncProfile?> GetAsync(VaultId vault, CancellationToken ct)
    {
        using var store = Open(vault);
        var load = await store.LoadAsync(ct).ConfigureAwait(false);
        return load.Value.Profile;
    }

    public async Task<Result<Unit>> SaveAsync(SyncProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var store = Open(profile.VaultId);
        return await store.SaveAsync(new SyncProfileDocument(profile), ct).ConfigureAwait(false);
    }

    public async Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        using var store = Open(vault);
        return await store.DeleteAsync(ct).ConfigureAwait(false);
    }

    private JsonFileStore<SyncProfileDocument> Open(VaultId vault) =>
        new(
            Path.Combine(localData.DirectoryOf(vault), "sync", "profile.json"),
            PlatformJsonContext.Default.SyncProfileDocument,
            () => new SyncProfileDocument(null),
            currentVersion: 1,
            migrations: [],
            indented: true,
            time);
}
