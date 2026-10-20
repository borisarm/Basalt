using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Basalt.Application.Indexing;

/// <summary>
/// Attaches index, metadata cache, indexer and file watcher to a session when the vault opens (spec 08 §2.2): the
/// cache is filled from the persisted index first (UI usable immediately) and the incremental scan runs in background.
/// </summary>
public sealed class IndexingSessionInitializer(
    INoteIndexFactory indexes,
    IMarkdownAnalyzer analyzer,
    IEventBus events,
    TimeProvider time,
    ILoggerFactory loggers) : IVaultSessionInitializer
{
    public async Task<Result<Unit>> InitializeAsync(VaultSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = await indexes.OpenAsync(session.Id, ct).ConfigureAwait(false);
        session.Own(index);
        var cache = new MetadataCache();
        var indexer = new VaultIndexer(session, index, cache, analyzer, events, time, loggers.CreateLogger<VaultIndexer>());
        session.Own(indexer);
        session.Set<IMetadataCache>(cache);
        session.Set(indexer);

        session.Own(new Subscriptions(
        [
            events.Subscribe<NoteSaved>((e, _) => Schedule(e.Id == session.Id, indexer, [e.Path])),
            events.Subscribe<EntriesCreated>((e, _) => Schedule(e.Id == session.Id, indexer, e.Paths)),
            events.Subscribe<FilesChangedBySync>((e, _) => Schedule(e.Id == session.Id, indexer, e.Paths)),
            events.Subscribe<EntryRenamed>(async (e, c) =>
            {
                if (e.Id == session.Id)
                {
                    await indexer.RenameAsync(e.From, e.To, c).ConfigureAwait(false);
                    indexer.Schedule([e.To]);
                }
            }),
            events.Subscribe<EntriesDeleted>(async (e, c) =>
            {
                if (e.Id == session.Id)
                {
                    await indexer.RemoveAsync(e.Paths, c).ConfigureAwait(false);
                }
            }),
            events.Subscribe<VaultFileChangedExternally>(async (e, c) =>
            {
                if (e.Id != session.Id)
                {
                    return;
                }

                switch (e.Change.Kind)
                {
                    case FileSystemChangeKind.RescanRequired:
                        await indexer.ScanAsync(c).ConfigureAwait(false);
                        break;
                    case FileSystemChangeKind.Renamed when e.Change.OldPath is { } old:
                        await indexer.RenameAsync(old, e.Change.Path, c).ConfigureAwait(false);
                        indexer.Schedule([e.Change.Path]);
                        break;
                    default:
                        indexer.Schedule([e.Change.Path]);
                        break;
                }
            }),
        ]));

        await indexer.LoadFromIndexAsync(ct).ConfigureAwait(false);
        session.Own(new WatcherPump(session, events));
        indexer.StartScan();
        return Result.Success();
    }

    private static ValueTask Schedule(bool sameVault, VaultIndexer indexer, IEnumerable<Domain.Notes.VaultPath> paths)
    {
        if (sameVault)
        {
            indexer.Schedule(paths);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Subscriptions(IReadOnlyList<IDisposable> subscriptions) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Forwards file watcher changes as <see cref="VaultFileChangedExternally"/> events.</summary>
    private sealed class WatcherPump : IAsyncDisposable
    {
        private readonly IFileWatcher _watcher;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public WatcherPump(VaultSession session, IEventBus events)
        {
            _watcher = session.FileSystem.CreateWatcher();
            _loop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var change in _watcher.WatchAsync(_stop.Token).ConfigureAwait(false))
                    {
                        await events.PublishAsync(new VaultFileChangedExternally(session.Id, change), _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _watcher.DisposeAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
