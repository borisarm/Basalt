using System.Text;
using Basalt.Adapters.Markdown;
using Basalt.Application.Events;
using Basalt.Application.Indexing;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Application.Tests.Indexing;

public sealed class VaultIndexerTests : IAsyncDisposable
{
    private static readonly VaultId Vault = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryVaultFileSystem _fs;
    private readonly InMemoryNoteIndexFactory _indexes = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly IMarkdownAnalyzer _analyzer = new ServiceCollection().AddBasaltMarkdownAdapter().BuildServiceProvider().GetRequiredService<IMarkdownAnalyzer>();
    private VaultSession? _session;

    public VaultIndexerTests() => _fs = new InMemoryVaultFileSystem(time: _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private VaultIndexer Indexer => _session!.Get<VaultIndexer>();

    private MetadataSnapshot Cache => _session!.Get<IMetadataCache>().Current;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        await _events.DisposeAsync();
    }

    private async Task WriteAsync(string path, string text) =>
        await _fs.WriteAllBytesAsync(P(path), Encoding.UTF8.GetBytes(text), null, Ct);

    private async Task OpenAsync()
    {
        _session = new VaultSession(Vault, "V", _fs, VaultSettings.Default);
        var initializer = new IndexingSessionInitializer(_indexes, _analyzer, _events, _time, NullLoggerFactory.Instance);
        (await initializer.InitializeAsync(_session, Ct)).IsSuccess.Should().BeTrue();
        await Indexer.ScanCompletion;
    }

    private Task<MetadataChanged> NextMetadataChangeAsync(Func<MetadataChanged, bool> predicate)
    {
        var tcs = new TaskCompletionSource<MetadataChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = _events.Subscribe<MetadataChanged>((e, _) =>
        {
            if (predicate(e))
            {
                tcs.TrySetResult(e);
            }

            return ValueTask.CompletedTask;
        });
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct).ContinueWith(t =>
        {
            subscription.Dispose();
            return t.Result;
        }, TaskScheduler.Default);
    }

    [Fact]
    [Trait("Spec", "F-001.3")]
    public async Task Open_IndexesAllNotesAndAttachments_SkippingHiddenEntries()
    {
        await WriteAsync("A.md", "# A\n[[B]] #tag");
        await WriteAsync("B.md", "B");
        await WriteAsync("img.png", "png");
        await WriteAsync(".basalt/vault.json", "{}");
        await WriteAsync(".trash/old.md", "old");

        await OpenAsync();

        Cache.Files.Keys.Select(k => k.ToString()).Order().Should().Equal("A.md", "B.md", "img.png");
        Cache.Graph.Incoming(P("B.md")).Should().ContainSingle();
        (await _indexes.For(Vault).GetAsync(P("A.md"), Ct))!.Text.Should().Be("# A\n[[B]] #tag");
        (await _indexes.For(Vault).GetAsync(P("img.png"), Ct))!.Text.Should().BeNull();
    }

    [Fact]
    public async Task Reopen_IsIncremental_AndDropsDeletedFiles()
    {
        await WriteAsync("A.md", "v1");
        await WriteAsync("B.md", "b");
        await OpenAsync();
        await _session!.DisposeAsync();
        _session = null;

        _time.Advance(TimeSpan.FromMinutes(1));
        await WriteAsync("A.md", "v2 [[B]]");
        await _fs.DeleteAsync(P("B.md"), Ct);
        await OpenAsync();

        Cache.Files.Keys.Should().Equal(P("A.md"));
        (await _indexes.For(Vault).GetAsync(P("A.md"), Ct))!.Text.Should().Be("v2 [[B]]");
        Cache.Graph.IncomingUnresolved("B").Should().ContainSingle();
    }

    [Fact]
    public async Task AnalyzerVersionChange_ReindexesEverything()
    {
        await WriteAsync("A.md", "x");
        await _indexes.For(Vault).ResetAsync(_analyzer.Version + 1, Ct);

        await OpenAsync();

        _indexes.For(Vault).AnalyzerVersion.Should().Be(_analyzer.Version);
        Cache.Files.Should().ContainKey(P("A.md"));
    }

    [Fact]
    [Trait("Spec", "F-005.3")]
    public async Task NoteSaved_UpdatesTheCacheAfterTheDebounce()
    {
        await WriteAsync("C.md", "c");
        await WriteAsync("D.md", "d");
        await OpenAsync();
        var changed = NextMetadataChangeAsync(e => e.Paths.Contains(P("D.md")));

        await WriteAsync("D.md", "now links [[C]]");
        await _events.PublishAsync(new NoteSaved(Vault, P("D.md")), Ct);
        await _events.WaitForIdleAsync(Ct);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await changed;

        Cache.Graph.Incoming(P("C.md")).Select(l => l.Source).Should().Equal(P("D.md"));
    }

    [Fact]
    public async Task Schedule_CoalescesPathsUntilFlushed()
    {
        await WriteAsync("A.md", "a");
        await OpenAsync();
        await WriteAsync("A.md", "[[new]]");

        Indexer.Schedule([P("A.md"), P("A.md"), P(".hidden/x.md")]);
        await Indexer.FlushAsync(Ct);
        await Indexer.FlushAsync(Ct);

        Cache.Graph.IncomingUnresolved("new").Should().ContainSingle();
    }

    [Fact]
    public async Task FolderRename_MovesEveryEntry()
    {
        await WriteAsync("X/a.md", "[[b]]");
        await WriteAsync("X/sub/b.md", "b");
        await OpenAsync();
        await _fs.MoveAsync(P("X"), P("Y"), Ct);

        await _events.PublishAsync(new EntryRenamed(Vault, P("X"), P("Y")), Ct);
        await _events.WaitForIdleAsync(Ct);

        Cache.Files.Keys.Select(k => k.ToString()).Order().Should().Equal("Y/a.md", "Y/sub/b.md");
        (await _indexes.For(Vault).GetAsync(P("Y/sub/b.md"), Ct)).Should().NotBeNull();
        Cache.Graph.Incoming(P("Y/sub/b.md")).Should().ContainSingle();
    }

    [Fact]
    public async Task EntriesDeleted_RemovesFilesAndFolders()
    {
        await WriteAsync("A.md", "a");
        await WriteAsync("Dir/b.md", "b");
        await OpenAsync();

        await _events.PublishAsync(new EntriesDeleted(Vault, [P("Dir"), P("A.md")]), Ct);
        await _events.WaitForIdleAsync(Ct);

        Cache.Files.Should().BeEmpty();
        (await _indexes.For(Vault).GetAllSummariesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-002.7")]
    public async Task ExternalChanges_ReachTheIndexThroughTheWatcher()
    {
        await OpenAsync();
        var changed = NextMetadataChangeAsync(e => e.Paths.Contains(P("Externa.md")));
        var delivered = new TaskCompletionSource();
        using var observed = _events.Subscribe<VaultFileChangedExternally>((_, _) =>
        {
            delivered.TrySetResult();
            return ValueTask.CompletedTask;
        });

        _fs.SimulateExternalWrite(P("Externa.md"), "# Externa");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await _events.WaitForIdleAsync(Ct);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await changed;

        Cache.Files.Should().ContainKey(P("Externa.md"));
    }

    [Fact]
    public async Task ExternalDeleteAndRename_AreApplied()
    {
        await WriteAsync("old.md", "x");
        await WriteAsync("gone.md", "y");
        await OpenAsync();

        _fs.SimulateExternalRename(P("old.md"), P("new.md"));
        _fs.SimulateExternalDelete(P("gone.md"));
        await WaitUntilAsync(() => Cache.Files.ContainsKey(P("new.md")));
        await _events.WaitForIdleAsync(Ct);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        await WaitUntilAsync(() => !Cache.Files.ContainsKey(P("gone.md")));

        Cache.Files.Keys.Should().Equal(P("new.md"));
    }

    [Fact]
    public async Task RescanRequired_ReconcilesWithDisk()
    {
        await OpenAsync();
        await WriteAsync("missed.md", "the watcher overflowed");

        await _events.PublishAsync(new VaultFileChangedExternally(Vault, new Ports.FileSystem.FileSystemChange(Ports.FileSystem.FileSystemChangeKind.RescanRequired, VaultPath.Root)), Ct);
        await _events.WaitForIdleAsync(Ct);

        Cache.Files.Should().ContainKey(P("missed.md"));
    }

    [Fact]
    public async Task EventsOfOtherVaults_AreIgnored()
    {
        await WriteAsync("A.md", "a");
        await OpenAsync();
        var other = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000bb"));

        await _events.PublishAsync(new EntriesDeleted(other, [P("A.md")]), Ct);
        await _events.PublishAsync(new EntryRenamed(other, P("A.md"), P("B.md")), Ct);
        await _events.WaitForIdleAsync(Ct);

        Cache.Files.Keys.Should().Equal(P("A.md"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
