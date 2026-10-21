using System.Text;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Sync;

public sealed class SyncStateCommitterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobSha Sha(string text) => GitBlobHasher.Compute(Utf8(text));

    private static Manifest ManifestOf(params (string Path, string Content)[] files) =>
        Manifest.Create(files.Select(file => new ManifestEntry(P(file.Path), Sha(file.Content), Utf8(file.Content).LongLength))).Value;

    private static async Task WriteAsync(InMemoryVaultFileSystem fileSystem, string path, string content) =>
        (await fileSystem.WriteAllBytesAsync(P(path), Utf8(content), null, Ct)).IsSuccess.Should().BeTrue();

    private static RemoteHead Head() => new(
        CommitSha.Parse("1111111111111111111111111111111111111111").Value,
        TreeSha.Parse("2222222222222222222222222222222222222222").Value);

    private static SyncCommitRequest Request(
        Manifest baseline,
        Manifest target,
        HashCache cache,
        IReadOnlyList<VaultPath>? deferred = null) =>
        new(
            new SyncBase(null, null, baseline),
            target,
            Head(),
            deferred ?? [],
            cache,
            2,
            1,
            1,
            0,
            1,
            TimeSpan.FromSeconds(3),
            SyncOutcome.Published);

    [Fact]
    public async Task Commit_BuildsTheNewBaseAndKeepsTheOldEntryForDeferredPaths()
    {
        var store = new InMemorySyncStateStore();
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "new A");
        await WriteAsync(fileSystem, "b.md", "old B");
        await WriteAsync(fileSystem, "c.md", "C");
        var baseline = ManifestOf(("a.md", "old A"), ("b.md", "old B"), ("d.md", "D"));
        var target = ManifestOf(("a.md", "new A"), ("b.md", "new B"), ("c.md", "C"));
        var cache = HashCache.Create([
            new HashCacheEntry(P("a.md"), 5, 1, Sha("old A")),
            new HashCacheEntry(P("b.md"), 5, 2, Sha("old B")),
            new HashCacheEntry(P("d.md"), 1, 3, Sha("D")),
        ]);
        await store.SaveJournalAsync(new SyncJournal(1, null, target, [], Now), Ct);

        var result = await new SyncStateCommitter(store, fileSystem, new FakeClock())
            .CommitAsync(Request(baseline, target, cache, [P("b.md")]), Ct);

        result.IsSuccess.Should().BeTrue();
        var saved = await store.LoadBaseAsync(Ct);
        saved.LastSyncedCommit.Should().Be(Head().Commit);
        saved.LastRemoteTree.Should().Be(Head().Tree);
        saved.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("a.md", "b.md", "c.md");
        saved.Manifest.Get(P("a.md"))!.Value.Sha.Should().Be(Sha("new A"));
        saved.Manifest.Get(P("b.md"))!.Value.Sha.Should().Be(Sha("old B"));
        saved.Manifest.Get(P("c.md"))!.Value.Sha.Should().Be(Sha("C"));

        var updatedCache = await store.LoadHashCacheAsync(Ct);
        updatedCache.Entries.Select(entry => entry.Path.ToString()).Should().Equal("a.md", "c.md");
        updatedCache.Entries[0].Sha.Should().Be(Sha("new A"));

        (await store.LoadJournalAsync(Ct)).Should().BeNull();
        var log = await store.LoadLogAsync(10, Ct);
        log.Should().ContainSingle();
        log[0].Outcome.Should().Be(SyncOutcome.Published);
        log[0].Head.Should().Be(Head().Commit);
        log[0].Downloaded.Should().Be(2);
        log[0].Uploaded.Should().Be(1);
        log[0].Conflicts.Should().Be(1);
        log[0].Deferred.Should().Be(1);
        log[0].When.Should().Be(new FakeClock().UtcNow);
    }

    [Fact]
    public async Task Commit_RemovesDeletedPathsFromTheBaseAndTheCache()
    {
        var store = new InMemorySyncStateStore();
        var fileSystem = new InMemoryVaultFileSystem();
        var baseline = ManifestOf(("a.md", "A"));
        var cache = HashCache.Create([new HashCacheEntry(P("a.md"), 1, 1, Sha("A"))]);

        await new SyncStateCommitter(store, fileSystem, new FakeClock())
            .CommitAsync(Request(baseline, Manifest.Empty, cache), Ct);

        (await store.LoadBaseAsync(Ct)).Manifest.IsEmpty.Should().BeTrue();
        (await store.LoadHashCacheAsync(Ct)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Commit_WhenTheStatFails_KeepsTheBaseEntryButDropsTheCacheEntry()
    {
        var store = new InMemorySyncStateStore();
        var fileSystem = new InMemoryVaultFileSystem();
        var target = ManifestOf(("a.md", "A"));

        var result = await new SyncStateCommitter(store, fileSystem, new FakeClock())
            .CommitAsync(Request(Manifest.Empty, target, HashCache.Empty), Ct);

        result.IsSuccess.Should().BeTrue();
        (await store.LoadBaseAsync(Ct)).Manifest.Get(P("a.md")).Should().NotBeNull();
        (await store.LoadHashCacheAsync(Ct)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Commit_WhenSavingTheBaseFails_LeavesTheJournalAndLogUntouched()
    {
        var store = new InMemorySyncStateStore { FailNextSave = nameof(InMemorySyncStateStore.SaveBaseAsync) };
        var fileSystem = new InMemoryVaultFileSystem();
        await store.SaveJournalAsync(new SyncJournal(1, null, Manifest.Empty, [], Now), Ct);

        var result = await new SyncStateCommitter(store, fileSystem, new FakeClock())
            .CommitAsync(Request(Manifest.Empty, Manifest.Empty, HashCache.Empty), Ct);

        result.Error.Code.Should().Be("sync.state_failed");
        (await store.LoadJournalAsync(Ct)).Should().NotBeNull();
        (await store.LoadLogAsync(10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_WhenSavingTheCacheFails_DoesNotAppendTheLogOrClearTheJournal()
    {
        var store = new InMemorySyncStateStore { FailNextSave = nameof(InMemorySyncStateStore.SaveHashCacheAsync) };
        var fileSystem = new InMemoryVaultFileSystem();
        await store.SaveJournalAsync(new SyncJournal(1, null, Manifest.Empty, [], Now), Ct);

        var result = await new SyncStateCommitter(store, fileSystem, new FakeClock())
            .CommitAsync(Request(Manifest.Empty, Manifest.Empty, HashCache.Empty), Ct);

        result.IsFailure.Should().BeTrue();
        (await store.LoadJournalAsync(Ct)).Should().NotBeNull();
        (await store.LoadLogAsync(10, Ct)).Should().BeEmpty();
    }
}
