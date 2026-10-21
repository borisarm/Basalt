using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Ports.Storage;

/// <summary>
/// Per-vault synchronisation state (spec 03 §1.6 and 06 §4): base, hash cache, journal, remote tree cache and log.
/// Losing the caches or the journal only costs performance; losing the base degrades to an initial merge.
/// </summary>
public interface ISyncStateStore
{
    Task<SyncBase> LoadBaseAsync(CancellationToken ct);

    Task<Result<Unit>> SaveBaseAsync(SyncBase @base, CancellationToken ct);

    Task<SyncJournal?> LoadJournalAsync(CancellationToken ct);

    Task<Result<Unit>> SaveJournalAsync(SyncJournal journal, CancellationToken ct);

    Task<Result<Unit>> ClearJournalAsync(CancellationToken ct);

    Task<HashCache> LoadHashCacheAsync(CancellationToken ct);

    Task<Result<Unit>> SaveHashCacheAsync(HashCache cache, CancellationToken ct);

    Task<IReadOnlyList<RemoteTreeCacheEntry>> LoadRemoteTreesAsync(CancellationToken ct);

    Task<Result<Unit>> SaveRemoteTreesAsync(IReadOnlyList<RemoteTreeCacheEntry> trees, CancellationToken ct);

    /// <summary>Last <paramref name="max"/> entries, oldest first.</summary>
    Task<IReadOnlyList<SyncLogEntry>> LoadLogAsync(int max, CancellationToken ct);

    Task<Result<Unit>> AppendLogAsync(SyncLogEntry entry, CancellationToken ct);
}

/// <summary>Creates the per-vault state store (spec 03 §1.6).</summary>
public interface ISyncStateStoreFactory
{
    ISyncStateStore Create(VaultId vault);

    /// <summary>Deletes the vault's sync state only; never touches the vault folder or the index.</summary>
    Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct);
}
