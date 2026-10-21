using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="ISyncStateStore"/> for application and sync tests.</summary>
public sealed class InMemorySyncStateStore : ISyncStateStore
{
    private const int MaxLogEntries = 500;
    private const int MaxRemoteTrees = 3;

    private readonly Lock _gate = new();
    private SyncBase _base = SyncBase.Empty;
    private SyncJournal? _journal;
    private HashCache _hashCache = HashCache.Empty;
    private IReadOnlyList<RemoteTreeCacheEntry> _trees = [];
    private readonly List<SyncLogEntry> _log = [];

    /// <summary>When set, the next save of that kind fails and the state is left untouched.</summary>
    public string? FailNextSave { get; set; }

    public Task<SyncBase> LoadBaseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_base);
        }
    }

    public Task<Result<Unit>> SaveBaseAsync(SyncBase @base, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(SaveBaseAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _base = @base;
            return Task.FromResult(Result.Success());
        }
    }

    public Task<SyncJournal?> LoadJournalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_journal);
        }
    }

    public Task<Result<Unit>> SaveJournalAsync(SyncJournal journal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(SaveJournalAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _journal = journal;
            return Task.FromResult(Result.Success());
        }
    }

    public Task<Result<Unit>> ClearJournalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(ClearJournalAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _journal = null;
            return Task.FromResult(Result.Success());
        }
    }

    public Task<HashCache> LoadHashCacheAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_hashCache);
        }
    }

    public Task<Result<Unit>> SaveHashCacheAsync(HashCache cache, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(SaveHashCacheAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _hashCache = cache;
            return Task.FromResult(Result.Success());
        }
    }

    public Task<IReadOnlyList<RemoteTreeCacheEntry>> LoadRemoteTreesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_trees);
        }
    }

    public Task<Result<Unit>> SaveRemoteTreesAsync(IReadOnlyList<RemoteTreeCacheEntry> trees, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(SaveRemoteTreesAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _trees = [.. trees.TakeLast(MaxRemoteTrees)];
            return Task.FromResult(Result.Success());
        }
    }

    public Task<IReadOnlyList<SyncLogEntry>> LoadLogAsync(int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SyncLogEntry>>(max <= 0 ? [] : [.. _log.TakeLast(max)]);
        }
    }

    public Task<Result<Unit>> AppendLogAsync(SyncLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeFailure(nameof(AppendLogAsync)) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            _log.Add(entry);
            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveRange(0, _log.Count - MaxLogEntries);
            }

            return Task.FromResult(Result.Success());
        }
    }

    private Error? TakeFailure(string operation) =>
        FailNextSave == operation
            ? new Error("sync.state_failed", $"Injected failure in {operation}.")
            : null;
}

/// <summary>Creates and caches one <see cref="InMemorySyncStateStore"/> per vault.</summary>
public sealed class InMemorySyncStateStoreFactory : ISyncStateStoreFactory
{
    private readonly Dictionary<VaultId, InMemorySyncStateStore> _stores = [];
    private readonly Lock _gate = new();

    public ISyncStateStore Create(VaultId vault)
    {
        lock (_gate)
        {
            if (!_stores.TryGetValue(vault, out var store))
            {
                store = new InMemorySyncStateStore();
                _stores.Add(vault, store);
            }

            return store;
        }
    }

    public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        lock (_gate)
        {
            _stores.Remove(vault);
        }

        return Task.FromResult(Result.Success());
    }
}
