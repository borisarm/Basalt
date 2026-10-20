using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace Basalt.Application.Indexing;

/// <summary>
/// Keeps the persistent index and the metadata cache of one vault in sync with its files (spec 08 §2.2): an
/// incremental scan at open (size/mtime against the index, batches of 200, full re-index when the analyzer version
/// changes) and hot updates for saved, renamed, deleted, synced or externally changed files (debounced 300 ms per
/// path). Hidden entries (.basalt, .trash, dot-files) are not indexed.
/// </summary>
public sealed partial class VaultIndexer : IAsyncDisposable
{
    public const int BatchSize = 200;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly VaultSession _session;
    private readonly INoteIndex _index;
    private readonly MetadataCache _cache;
    private readonly IMarkdownAnalyzer _analyzer;
    private readonly IEventBus _events;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly Lock _pendingGate = new();
    private readonly HashSet<VaultPath> _pending = [];
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _shutdown = new();

    public VaultIndexer(VaultSession session, INoteIndex index, MetadataCache cache, IMarkdownAnalyzer analyzer, IEventBus events, TimeProvider time, ILogger<VaultIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _index = index;
        _cache = cache;
        _analyzer = analyzer;
        _events = events;
        _logger = logger;
        _timer = time.CreateTimer(_ => _ = FlushSafelyAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public IMetadataCache Cache => _cache;

    /// <summary>Completes when the initial scan finished (the UI is usable before that).</summary>
    public Task ScanCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the cache from the persisted index (fast path at open).</summary>
    public async Task LoadFromIndexAsync(CancellationToken ct) =>
        _cache.Load(await _index.GetAllSummariesAsync(ct).ConfigureAwait(false));

    public void StartScan() => ScanCompletion = Task.Run(() => ScanAsync(_shutdown.Token));

    /// <summary>Reconciles the index with the files on disk.</summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await _index.GetSnapshotInfoAsync(ct).ConfigureAwait(false);
            var fullReindex = info.AnalyzerVersion != _analyzer.Version;
            if (fullReindex)
            {
                await _index.ResetAsync(_analyzer.Version, ct).ConfigureAwait(false);
                info = new IndexSnapshotInfo(_analyzer.Version, new Dictionary<VaultPath, IndexStamp>());
            }

            var stale = new List<FileEntry>();
            var seen = new HashSet<VaultPath>();
            await foreach (var entry in _session.FileSystem.EnumerateAsync(VaultPath.Root, recursive: true, ct).ConfigureAwait(false))
            {
                if (entry.IsDirectory || entry.Path.IsHidden)
                {
                    continue;
                }

                seen.Add(entry.Path);
                if (!info.Entries.TryGetValue(entry.Path, out var stamp) || stamp.Size != entry.Size || stamp.LastWriteUtc != entry.LastWriteUtc)
                {
                    stale.Add(entry);
                }
            }

            var removed = info.Entries.Keys.Where(p => !seen.Contains(p)).ToList();
            var upserted = new List<IndexedNoteSummary>();
            for (var i = 0; i < stale.Count; i += BatchSize)
            {
                var batch = new List<IndexedNote>();
                foreach (var entry in stale.Skip(i).Take(BatchSize))
                {
                    if (await ReadAsync(entry.Path, entry.Size, entry.LastWriteUtc, ct).ConfigureAwait(false) is { } note)
                    {
                        batch.Add(note);
                    }
                }

                await _index.UpsertManyAsync(batch, ct).ConfigureAwait(false);
                upserted.AddRange(batch.Select(Summary));
                await _events.PublishAsync(new IndexingProgressChanged(_session.Id, Math.Min(i + BatchSize, stale.Count), stale.Count), ct).ConfigureAwait(false);
            }

            foreach (var path in removed)
            {
                await _index.RemoveAsync(path, ct).ConfigureAwait(false);
            }

            if (fullReindex || upserted.Count + removed.Count > BatchSize)
            {
                _cache.Load(await _index.GetAllSummariesAsync(ct).ConfigureAwait(false));
                await PublishChangedAsync(_cache.Current.Files.Keys.Concat(removed).ToHashSet(), ct).ConfigureAwait(false);
            }
            else if (upserted.Count + removed.Count > 0)
            {
                await PublishChangedAsync(_cache.Apply(upserted, removed), ct).ConfigureAwait(false);
            }

            LogScanned(_logger, seen.Count, stale.Count, removed.Count);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Queues paths for re-indexing after the debounce delay.</summary>
    public void Schedule(IEnumerable<VaultPath> paths)
    {
        lock (_pendingGate)
        {
            foreach (var path in paths.Where(p => !p.IsHidden))
            {
                _pending.Add(path);
            }

            if (_pending.Count > 0)
            {
                _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Processes queued paths now (also used on shutdown and by tests).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        List<VaultPath> paths;
        lock (_pendingGate)
        {
            paths = [.. _pending];
            _pending.Clear();
        }

        if (paths.Count == 0)
        {
            return;
        }

        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var upserted = new List<IndexedNoteSummary>();
            var removed = new List<VaultPath>();
            foreach (var path in paths)
            {
                var stat = await _session.FileSystem.StatAsync(path, ct).ConfigureAwait(false);
                if (stat.IsFailure)
                {
                    if (_cache.Current.Files.ContainsKey(path))
                    {
                        await _index.RemoveAsync(path, ct).ConfigureAwait(false);
                        removed.Add(path);
                    }

                    continue;
                }

                if (stat.Value.IsDirectory)
                {
                    // A new or renamed folder: index its content.
                    await foreach (var entry in _session.FileSystem.EnumerateAsync(path, recursive: true, ct).ConfigureAwait(false))
                    {
                        if (!entry.IsDirectory && !entry.Path.IsHidden && await ReadAsync(entry.Path, entry.Size, entry.LastWriteUtc, ct).ConfigureAwait(false) is { } child)
                        {
                            await _index.UpsertAsync(child, ct).ConfigureAwait(false);
                            upserted.Add(Summary(child));
                        }
                    }

                    continue;
                }

                if (await ReadAsync(path, stat.Value.Size, stat.Value.LastWriteUtc, ct).ConfigureAwait(false) is { } note)
                {
                    await _index.UpsertAsync(note, ct).ConfigureAwait(false);
                    upserted.Add(Summary(note));
                }
            }

            await PublishChangedAsync(_cache.Apply(upserted, removed), ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Applies a rename (file or folder) to the index and cache immediately.</summary>
    public async Task RenameAsync(VaultPath from, VaultPath to, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var touched = new HashSet<VaultPath>();
            var moved = _cache.Current.Files.Keys.Where(p => p == from || p.IsDescendantOf(from)).ToList();
            foreach (var path in moved)
            {
                var target = path == from ? to : to.Combine(path.RelativeTo(from)).Value;
                await _index.RenameAsync(path, target, ct).ConfigureAwait(false);
                touched.UnionWith(_cache.Rename(path, target));
            }

            await PublishChangedAsync(touched, ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Removes deleted entries (files or whole folders) from the index and cache.</summary>
    public async Task RemoveAsync(IReadOnlyList<VaultPath> paths, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = _cache.Current.Files.Keys.Where(p => paths.Any(d => p == d || p.IsDescendantOf(d))).ToList();
            foreach (var path in removed)
            {
                await _index.RemoveAsync(path, ct).ConfigureAwait(false);
            }

            await PublishChangedAsync(_cache.Apply([], removed), ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await ScanCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _work.WaitAsync().ConfigureAwait(false);
        _work.Dispose();
        _shutdown.Dispose();
    }

    private async Task<IndexedNote?> ReadAsync(VaultPath path, long size, DateTimeOffset lastWrite, CancellationToken ct)
    {
        var bytes = await _session.FileSystem.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.IsFailure)
        {
            return null; // deleted or locked meanwhile: the watcher or next scan catches up
        }

        var kind = FileClassifier.Classify(path);
        var stamp = new IndexStamp(size, lastWrite, GitBlobHasher.Compute(bytes.Value));
        if (kind != FileKind.Note)
        {
            return new IndexedNote(path, kind, stamp, null, NoteMetadata.Empty);
        }

        var text = NoteContent.FromBytes(bytes.Value).Text;
        return new IndexedNote(path, kind, stamp, text, _analyzer.Analyze(text));
    }

    private async Task FlushSafelyAsync()
    {
        try
        {
            await FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // background indexing must survive unexpected failures; the next scan repairs the index
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRefreshFailed(_logger, ex);
        }
    }

    private Task PublishChangedAsync(IReadOnlySet<VaultPath> paths, CancellationToken ct) =>
        paths.Count == 0 ? Task.CompletedTask : _events.PublishAsync(new MetadataChanged(_session.Id, paths), ct).AsTask();

    private static IndexedNoteSummary Summary(IndexedNote note) => new(note.Path, note.Kind, note.Stamp, note.Metadata);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index scan: {Files} files, {Indexed} (re)indexed, {Removed} removed")]
    private static partial void LogScanned(ILogger logger, int files, int indexed, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hot index refresh failed")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
