using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Tests.Sync;

public sealed class SyncJournalActionTests
{
    private static readonly VaultPath P = VaultPath.Create("a.md").Value;
    private static readonly BlobSha Sha = BlobSha.Parse(new string('a', 40)).Value;
    private static readonly BlobSha Other = BlobSha.Parse(new string('b', 40)).Value;

    [Fact]
    public void From_MapsEveryActionKind()
    {
        SyncJournalAction.From(new SyncAction.Download(P, Other)).Should().Be(new SyncJournalAction("download", P, Other));
        SyncJournalAction.From(new SyncAction.Upload(P, Other)).Should().Be(new SyncJournalAction("upload", P, Other));
        SyncJournalAction.From(new SyncAction.DeleteLocal(P, Other)).Should().Be(new SyncJournalAction("delete_local", P, Other));
        SyncJournalAction.From(new SyncAction.DeleteRemote(P)).Should().Be(new SyncJournalAction("delete_remote", P));
        SyncJournalAction.From(new SyncAction.MergeText(P, Sha, Other, Sha)).Should().Be(new SyncJournalAction("merge", P, Sha));
        SyncJournalAction.From(new SyncAction.CreateConflictCopy(P, ConflictSide.Local, Other))
            .Should().Be(new SyncJournalAction("conflict_copy", P, Other));
        SyncJournalAction.From(new SyncAction.NoOp(P)).Should().Be(new SyncJournalAction("noop", P));
    }
}
