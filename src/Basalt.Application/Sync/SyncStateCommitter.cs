using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>Everything the committer needs to persist the result of one cycle (spec 06 §6, step 9).</summary>
public sealed record SyncCommitRequest(
    SyncBase Baseline,
    Manifest Target,
    RemoteHead? Head,
    IReadOnlyList<VaultPath> Deferred,
    HashCache PreviousHashCache,
    int Downloaded,
    int Uploaded,
    int DeletedLocal,
    int DeletedRemote,
    int Conflicts,
    TimeSpan Duration,
    SyncOutcome Outcome,
    string? ErrorCode = null);

/// <summary>The new base and the log entry that were persisted.</summary>
public sealed record SyncCommitResult(SyncBase Base, SyncLogEntry Log);

/// <summary>
/// Computes and persists the new per-file base, updates the hash cache with the files written by the cycle, appends the
/// log and clears the journal (spec 06 §6, step 9). A deferred path keeps its previous base entry so the next cycle
/// re-plans it; everything else takes the target entry.
/// </summary>
public sealed class SyncStateCommitter
{
    private readonly ISyncStateStore _store;
    private readonly IVaultFileSystem _fileSystem;
    private readonly IClock _clock;

    public SyncStateCommitter(ISyncStateStore store, IVaultFileSystem fileSystem, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _fileSystem = fileSystem;
        _clock = clock;
    }

    public async Task<Result<SyncCommitResult>> CommitAsync(SyncCommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deferred = request.Deferred.ToHashSet();
        var baseManifest = NewBase(request.Baseline.Manifest, request.Target, deferred);
        var newBase = new SyncBase(request.Head?.Commit, request.Head?.Tree, baseManifest);
        var savedBase = await _store.SaveBaseAsync(newBase, ct).ConfigureAwait(false);
        if (savedBase.IsFailure)
        {
            return Result<SyncCommitResult>.Failure(savedBase.Error);
        }

        var cache = await UpdatedCacheAsync(request.Target, newBase.Manifest, deferred, request.PreviousHashCache, ct)
            .ConfigureAwait(false);
        var savedCache = await _store.SaveHashCacheAsync(cache, ct).ConfigureAwait(false);
        if (savedCache.IsFailure)
        {
            return Result<SyncCommitResult>.Failure(savedCache.Error);
        }

        var log = new SyncLogEntry(
            _clock.UtcNow,
            request.Outcome,
            request.Head?.Commit,
            request.Downloaded,
            request.Uploaded,
            request.DeletedLocal,
            request.DeletedRemote,
            request.Conflicts,
            request.Deferred.Count,
            request.Duration,
            request.ErrorCode);
        var appended = await _store.AppendLogAsync(log, ct).ConfigureAwait(false);
        if (appended.IsFailure)
        {
            return Result<SyncCommitResult>.Failure(appended.Error);
        }

        var cleared = await _store.ClearJournalAsync(ct).ConfigureAwait(false);
        return cleared.IsFailure
            ? Result<SyncCommitResult>.Failure(cleared.Error)
            : new SyncCommitResult(newBase, log);
    }

    private static Manifest NewBase(Manifest previous, Manifest target, HashSet<VaultPath> deferred)
    {
        var entries = previous.Entries.ToDictionary(entry => entry.Path, entry => entry);
        foreach (var path in entries.Keys.Where(path => !deferred.Contains(path) && !target.Contains(path)).ToList())
        {
            entries.Remove(path);
        }

        foreach (var entry in target.Entries)
        {
            if (!deferred.Contains(entry.Path))
            {
                entries[entry.Path] = entry;
            }
        }

        return Manifest.Create(entries.Values).Value;
    }

    private async Task<HashCache> UpdatedCacheAsync(
        Manifest target,
        Manifest newBase,
        HashSet<VaultPath> deferred,
        HashCache previous,
        CancellationToken ct)
    {
        var entries = new Dictionary<VaultPath, HashCacheEntry>();
        foreach (var cached in previous.Entries)
        {
            if (newBase.Contains(cached.Path) && !deferred.Contains(cached.Path))
            {
                entries[cached.Path] = cached;
            }
        }

        foreach (var entry in target.Entries)
        {
            if (deferred.Contains(entry.Path))
            {
                entries.Remove(entry.Path);
                continue;
            }

            var stat = await _fileSystem.StatAsync(entry.Path, ct).ConfigureAwait(false);
            if (stat.IsFailure || stat.Value.IsDirectory)
            {
                entries.Remove(entry.Path);
                continue;
            }

            entries[entry.Path] = new HashCacheEntry(entry.Path, stat.Value.Size, stat.Value.LastWriteUtc.UtcTicks, entry.Sha);
        }

        return HashCache.Create(entries.Values);
    }
}
