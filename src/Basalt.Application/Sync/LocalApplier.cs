using Basalt.Application.Common;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Paths written/deleted by the sync and paths left untouched because the user edited them first.</summary>
public sealed record LocalApplyResult(IReadOnlyList<VaultPath> Applied, IReadOnlyList<VaultPath> Deferred);

/// <summary>
/// Applies the target manifest to disk (spec 06 §6, step 8): optimistic writes/deletes with the scanned SHA, local
/// moves instead of re-downloading a blob that is being deleted (spec 06 §7.2), removal of empty folders and the
/// <see cref="FilesChangedBySync"/> event. Paths edited during the cycle are deferred, never overwritten.
/// </summary>
public sealed class LocalApplier
{
    private readonly IVaultFileSystem _fileSystem;
    private readonly VaultLocks _locks;
    private readonly IEventBus _events;

    public LocalApplier(IVaultFileSystem fileSystem, VaultLocks locks, IEventBus events)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(events);
        _fileSystem = fileSystem;
        _locks = locks;
        _events = events;
    }

    public async Task<Result<LocalApplyResult>> ApplyAsync(
        VaultId vault,
        Manifest local,
        Manifest target,
        IReadOnlyDictionary<BlobSha, byte[]> contents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(contents);

        var writes = target.Entries
            .Where(entry => local.Get(entry.Path) is not { } current || current.Sha != entry.Sha)
            .ToList();
        var deletes = local.Entries.Where(entry => !target.Contains(entry.Path)).ToList();
        var pendingWrites = new List<ManifestEntry>();
        var deletedBySha = deletes
            .GroupBy(entry => entry.Sha)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Path).Order().ToList());

        var applied = new List<VaultPath>();
        var deferred = new HashSet<VaultPath>();
        var removed = new List<VaultPath>();
        var movedFrom = new HashSet<VaultPath>();

        foreach (var write in writes)
        {
            if (deletedBySha.TryGetValue(write.Sha, out var candidates) && candidates.Count > 0)
            {
                var source = candidates[0];
                using var lease = await _locks.WriteAsync(vault, ct).ConfigureAwait(false);
                if (await MatchesAsync(source, write.Sha, ct).ConfigureAwait(false))
                {
                    var moved = await _fileSystem.MoveAsync(source, write.Path, ct).ConfigureAwait(false);
                    if (moved.IsSuccess)
                    {
                        candidates.RemoveAt(0);
                        movedFrom.Add(source);
                        removed.Add(source);
                        applied.Add(source);
                        applied.Add(write.Path);
                        continue;
                    }
                }
            }

            pendingWrites.Add(write);
        }

        foreach (var write in pendingWrites)
        {
            if (!contents.TryGetValue(write.Sha, out var content))
            {
                return new Error(SyncErrors.ContentMissing, "The sync has no content for a file it must write.")
                    .With("path", write.Path.ToString())
                    .With("sha", write.Sha.ToString());
            }

            var previous = local.Get(write.Path);
            Result<Unit> result;
            using (await _locks.WriteAsync(vault, ct).ConfigureAwait(false))
            {
                result = previous is { } existing
                    ? await _fileSystem.WriteAllBytesAsync(write.Path, content, existing.Sha, ct).ConfigureAwait(false)
                    : await _fileSystem.CreateNewAsync(write.Path, content, ct).ConfigureAwait(false);
            }

            if (result.IsSuccess)
            {
                applied.Add(write.Path);
            }
            else if (IsDeferred(result.Error.Code))
            {
                deferred.Add(write.Path);
            }
            else
            {
                return Result<LocalApplyResult>.Failure(result.Error);
            }
        }

        foreach (var delete in deletes.Where(entry => !movedFrom.Contains(entry.Path)))
        {
            Result<Unit> result;
            using (await _locks.WriteAsync(vault, ct).ConfigureAwait(false))
            {
                result = await _fileSystem.DeleteIfUnchangedAsync(delete.Path, delete.Sha, ct).ConfigureAwait(false);
            }

            if (result.IsSuccess || result.Error.Code == ErrorCodes.FsNotFound)
            {
                applied.Add(delete.Path);
                removed.Add(delete.Path);
            }
            else if (IsDeferred(result.Error.Code))
            {
                deferred.Add(delete.Path);
            }
            else
            {
                return Result<LocalApplyResult>.Failure(result.Error);
            }
        }

        foreach (var folder in removed.Select(path => path.Parent).Distinct().ToList())
        {
            for (var current = folder; !current.IsRoot; current = current.Parent)
            {
                using (await _locks.WriteAsync(vault, ct).ConfigureAwait(false))
                {
                    var pruned = await _fileSystem.DeleteDirectoryIfEmptyAsync(current, ct).ConfigureAwait(false);
                    if (pruned.IsFailure)
                    {
                        return Result<LocalApplyResult>.Failure(pruned.Error);
                    }
                }
            }
        }

        var changed = applied.Distinct().Order().ToList();
        if (changed.Count > 0)
        {
            await _events.PublishAsync(new FilesChangedBySync(vault, changed), ct).ConfigureAwait(false);
        }

        return new LocalApplyResult(changed, [.. deferred.Order()]);
    }

    private async Task<bool> MatchesAsync(VaultPath path, BlobSha sha, CancellationToken ct)
    {
        var read = await _fileSystem.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return read.IsSuccess && GitBlobHasher.Compute(read.Value) == sha;
    }

    private static bool IsDeferred(string code) =>
        code is ErrorCodes.FsConcurrentModification or ErrorCodes.FsAlreadyExists or ErrorCodes.FsNotFound;
}
