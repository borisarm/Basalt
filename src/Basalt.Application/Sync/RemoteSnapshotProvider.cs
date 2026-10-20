using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>
/// Reads the remote head and manifest with a small in-memory tree cache and an ETag (spec 06 §6, steps 2 and 3).
/// Paths that cannot be materialised are quarantined instead of failing the cycle: case-fold collisions always (the
/// manifest must be portable) and whatever the repository reports as an invalid vault path (spec 06 §7.4 and §7.5).
/// </summary>
public sealed class RemoteSnapshotProvider
{
    private const int MaxCachedTrees = 3;

    private readonly IRemoteRepository _repository;
    private readonly List<RemoteTreeCacheEntry> _trees = [];
    private RemoteHead? _head;
    private Manifest _manifest = Manifest.Empty;
    private IReadOnlyList<RemotePathIssue> _quarantined = [];
    private string? _etag;

    /// <param name="cachedTrees">Persisted trees, oldest first; the last <see cref="MaxCachedTrees"/> are kept.</param>
    public RemoteSnapshotProvider(IRemoteRepository repository, IEnumerable<RemoteTreeCacheEntry>? cachedTrees = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        if (cachedTrees is null)
        {
            return;
        }

        foreach (var entry in cachedTrees)
        {
            Remember(entry);
        }
    }

    /// <summary>Cached trees, oldest first, for the state store to persist (spec 06 §4).</summary>
    public IReadOnlyList<RemoteTreeCacheEntry> CachedTrees => [.. _trees];

    public async Task<Result<RemoteSnapshot>> GetAsync(CancellationToken ct)
    {
        var check = await _repository.GetHeadAsync(_etag, ct).ConfigureAwait(false);
        if (check.IsFailure)
        {
            return Result<RemoteSnapshot>.Failure(check.Error);
        }

        var state = check.Value;
        if (state.NotModified && _head is not null)
        {
            _etag = state.ETag ?? _etag;
            return new RemoteSnapshot(_head, _manifest, _etag, _quarantined, NotModified: true);
        }

        if (state.NotModified)
        {
            check = await _repository.GetHeadAsync(null, ct).ConfigureAwait(false);
            if (check.IsFailure)
            {
                return Result<RemoteSnapshot>.Failure(check.Error);
            }

            state = check.Value;
        }

        _etag = state.ETag;
        if (state.Head is null)
        {
            _head = null;
            _manifest = Manifest.Empty;
            _quarantined = [];
            return new RemoteSnapshot(null, Manifest.Empty, _etag, [], NotModified: false);
        }

        if (_head is { } current && current.Tree == state.Head.Tree)
        {
            _head = state.Head;
            return new RemoteSnapshot(_head, _manifest, _etag, _quarantined, NotModified: false);
        }

        var tree = await GetTreeAsync(state.Head.Tree, ct).ConfigureAwait(false);
        if (tree.IsFailure)
        {
            return Result<RemoteSnapshot>.Failure(tree.Error);
        }

        var (manifest, quarantined) = Flatten(tree.Value);
        _head = state.Head;
        _manifest = manifest;
        _quarantined = quarantined;
        return new RemoteSnapshot(_head, manifest, _etag, quarantined, NotModified: false);
    }

    private async Task<Result<RemoteTreeSnapshot>> GetTreeAsync(TreeSha tree, CancellationToken ct)
    {
        var index = _trees.FindIndex(entry => entry.Tree == tree);
        if (index >= 0)
        {
            var cached = _trees[index];
            _trees.RemoveAt(index);
            _trees.Add(cached);
            return cached.Snapshot;
        }

        var result = await _repository.GetTreeAsync(tree, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Remember(new RemoteTreeCacheEntry(tree, result.Value));
        }

        return result;
    }

    private void Remember(RemoteTreeCacheEntry entry)
    {
        _trees.RemoveAll(cached => cached.Tree == entry.Tree);
        _trees.Add(entry);
        while (_trees.Count > MaxCachedTrees)
        {
            _trees.RemoveAt(0);
        }
    }

    private static (Manifest Manifest, IReadOnlyList<RemotePathIssue> Quarantined) Flatten(RemoteTreeSnapshot snapshot)
    {
        var quarantined = new List<RemotePathIssue>(snapshot.Quarantined);
        var byCaseFold = new Dictionary<string, RemoteTreeFile>(StringComparer.Ordinal);
        var kept = new List<RemoteTreeFile>(snapshot.Files.Count);
        foreach (var file in snapshot.Files.OrderBy(file => file.Path))
        {
            if (!byCaseFold.TryAdd(file.Path.CaseFoldKey, file))
            {
                quarantined.Add(new RemotePathIssue(file.Path.ToString(), RemoteQuarantineReasons.CaseCollision));
                continue;
            }

            kept.Add(file);
        }

        var manifest = Manifest.Create(kept.Select(file => new ManifestEntry(file.Path, file.Sha, file.Size))).Value;
        return (manifest, quarantined);
    }
}
