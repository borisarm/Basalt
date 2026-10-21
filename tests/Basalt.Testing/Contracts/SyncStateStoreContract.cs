using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="ISyncStateStore"/> (spec 03 §1.6 and 06 §4) shared by the fake and the JSON adapter.</summary>
public abstract class SyncStateStoreContract : ContractTest<ISyncStateStore>
{
    private static readonly DateTimeOffset When = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static BlobSha Sha(string text) => GitBlobHasher.Compute(System.Text.Encoding.UTF8.GetBytes(text));

    private static CommitSha Commit(string hex) => CommitSha.Parse(hex).Value;

    private static TreeSha Tree(string hex) => TreeSha.Parse(hex).Value;

    private static Manifest SampleManifest() => Manifest.Create([
        new ManifestEntry(P("A.md"), Sha("A"), 1),
        new ManifestEntry(P("dir/B.md"), Sha("B"), 1),
    ]).Value;

    private static HashCache SampleCache() => HashCache.Create([
        new HashCacheEntry(P("A.md"), 1, 100, Sha("A")),
        new HashCacheEntry(P("dir/B.md"), 2, 200, Sha("B")),
    ]);

    private static RemoteTreeCacheEntry SampleTree(string hex) => new(
        Tree(hex),
        new RemoteTreeSnapshot(
            [new RemoteTreeFile(P("A.md"), Sha("A"), 1)],
            [new RemotePathIssue("aux.md", RemoteQuarantineReasons.InvalidPath)]));

    [Fact]
    public async Task Base_IsEmptyByDefaultAndRoundTrips()
    {
        (await Sut.LoadBaseAsync(Ct)).Should().Be(SyncBase.Empty);

        var baseline = new SyncBase(
            Commit("1111111111111111111111111111111111111111"),
            Tree("2222222222222222222222222222222222222222"),
            SampleManifest());
        (await Sut.SaveBaseAsync(baseline, Ct)).IsSuccess.Should().BeTrue();

        (await Sut.LoadBaseAsync(Ct)).Should().Be(baseline);
    }

    [Fact]
    public async Task HashCache_RoundTrips()
    {
        (await Sut.LoadHashCacheAsync(Ct)).Should().Be(HashCache.Empty);

        await Sut.SaveHashCacheAsync(SampleCache(), Ct);

        (await Sut.LoadHashCacheAsync(Ct)).Should().Be(SampleCache());
    }

    [Fact]
    public async Task Journal_SavesLoadsAndClears()
    {
        (await Sut.LoadJournalAsync(Ct)).Should().BeNull();
        var journal = new SyncJournal(
            2,
            Commit("1111111111111111111111111111111111111111"),
            SampleManifest(),
            [new SyncJournalAction("download", P("A.md"), Sha("A")), new SyncJournalAction("noop", P("B.md"))],
            When);

        (await Sut.SaveJournalAsync(journal, Ct)).IsSuccess.Should().BeTrue();
        var loaded = (await Sut.LoadJournalAsync(Ct))!;
        loaded.Attempt.Should().Be(2);
        loaded.ExpectedHead.Should().Be(journal.ExpectedHead);
        loaded.Target.Should().Be(journal.Target);
        loaded.Actions.Should().HaveCount(2);
        loaded.Actions[0].Kind.Should().Be("download");
        loaded.Actions[0].Path.Should().Be(P("A.md"));
        loaded.Actions[0].Sha.Should().Be(Sha("A"));
        loaded.Actions[1].Kind.Should().Be("noop");
        loaded.Actions[1].Path.Should().Be(P("B.md"));
        loaded.Actions[1].Sha.Should().BeNull();
        loaded.StartedAt.Should().Be(When);
        (await Sut.ClearJournalAsync(Ct)).IsSuccess.Should().BeTrue();
        (await Sut.LoadJournalAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task RemoteTrees_KeepTheThreeMostRecent()
    {
        var trees = new[]
        {
            SampleTree("1111111111111111111111111111111111111111"),
            SampleTree("2222222222222222222222222222222222222222"),
            SampleTree("3333333333333333333333333333333333333333"),
            SampleTree("4444444444444444444444444444444444444444"),
        };

        (await Sut.SaveRemoteTreesAsync(trees, Ct)).IsSuccess.Should().BeTrue();

        var loaded = await Sut.LoadRemoteTreesAsync(Ct);
        loaded.Select(entry => entry.Tree).Should().Equal(trees[1].Tree, trees[2].Tree, trees[3].Tree);
        loaded[0].Snapshot.Files.Single().Path.Should().Be(P("A.md"));
        loaded[0].Snapshot.Quarantined.Single().Reason.Should().Be(RemoteQuarantineReasons.InvalidPath);
    }

    [Fact]
    public async Task Log_KeepsTheLastFiveHundredEntries()
    {
        for (var i = 0; i < 505; i++)
        {
            (await Sut.AppendLogAsync(Entry(i), Ct)).IsSuccess.Should().BeTrue();
        }

        var all = await Sut.LoadLogAsync(1_000, Ct);
        all.Should().HaveCount(500);
        all[0].Uploaded.Should().Be(5);
        all[^1].Uploaded.Should().Be(504);

        var last = await Sut.LoadLogAsync(10, Ct);
        last.Should().HaveCount(10);
        last[0].Uploaded.Should().Be(495);
        last[^1].Uploaded.Should().Be(504);
        (await Sut.LoadLogAsync(0, Ct)).Should().BeEmpty();
    }

    private static SyncLogEntry Entry(int index) => new(
        When.AddSeconds(index),
        SyncOutcome.Published,
        null,
        index,
        index,
        0,
        0,
        0,
        0,
        TimeSpan.FromMilliseconds(index));
}
