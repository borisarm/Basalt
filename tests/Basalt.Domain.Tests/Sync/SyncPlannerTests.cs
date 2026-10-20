using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Generators;

namespace Basalt.Domain.Tests.Sync;

public sealed class SyncPlannerTests
{
    private static readonly BlobSha A = BlobSha.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391").Value;
    private static readonly BlobSha B = BlobSha.Parse("ce013625030ba8dba906f756967f9e9ca394464a").Value;
    private static readonly BlobSha C = BlobSha.Parse("d670460b4b4aece5915caf5c68d12f560a9fe3e4").Value;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static ManifestEntry E(string path, BlobSha sha) => new(P(path), sha, 1);

    private static Manifest M(params (string Path, BlobSha Sha)[] entries) =>
        Manifest.Create(entries.Select(entry => E(entry.Path, entry.Sha))).Value;

    private static SyncPlan Plan(Manifest? baseline, Manifest local, Manifest remote, SyncPolicy? policy = null) =>
        SyncPlanner.Plan(new SyncBase(null, null, baseline ?? Manifest.Empty), local, remote, policy ?? SyncPolicy.Default);

    [Fact]
    [Trait("Spec", "SYNC-ROW-1")]
    public void Row1_AllEqual_DoesNothing()
    {
        var manifest = M(("a.md", A));

        var plan = Plan(manifest, manifest, manifest);

        plan.IsEmpty.Should().BeTrue();
        plan.ExpectedNewBase.Should().Be(manifest);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-2")]
    public void Row2_LocalModified_Uploads()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", B)), M(("a.md", A)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Upload(P("a.md"), B));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-3")]
    public void Row3_RemoteModified_Downloads()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", A)), M(("a.md", B)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Download(P("a.md"), B));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-4")]
    public void Row4_SameChangeOnBothSides_UpdatesTheBaseOnly()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", B)), M(("a.md", B)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.NoOp(P("a.md")));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-5")]
    public void Row5_TextEditedOnBothSides_Merges()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", B)), M(("a.md", C)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.MergeText(P("a.md"), A, B, C));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(C);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-6")]
    public void Row6_BinaryEditedOnBothSides_KeepsBoth()
    {
        var plan = Plan(M(("a.png", A)), M(("a.png", B)), M(("a.png", C)));

        plan.Actions.Should().HaveCount(2);
        plan.Actions.Should().Contain(new SyncAction.Download(P("a.png"), C));
        plan.Actions.Should().Contain(new SyncAction.CreateConflictCopy(P("a.png"), ConflictSide.Local, B));
        plan.ExpectedNewBase.Get(P("a.png"))!.Value.Sha.Should().Be(C);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-7")]
    public void Row7_DeletedLocallyUnchangedRemotely_DeletesRemote()
    {
        var plan = Plan(M(("a.md", A)), Manifest.Empty, M(("a.md", A)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.DeleteRemote(P("a.md")));
        plan.ExpectedNewBase.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-8")]
    public void Row8_DeletedRemotelyUnchangedLocally_DeletesLocal()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", A)), Manifest.Empty);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.DeleteLocal(P("a.md"), A));
        plan.ExpectedNewBase.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-9")]
    public void Row9_DeletedOnBothSides_OnlyUpdatesTheBase()
    {
        var plan = Plan(M(("a.md", A)), Manifest.Empty, Manifest.Empty);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.NoOp(P("a.md")));
        plan.ExpectedNewBase.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-10")]
    public void Row10_ModifiedLocallyDeletedRemotely_KeepsTheModification()
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", B)), Manifest.Empty);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Upload(P("a.md"), B));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-10")]
    public void Row10_PreferDelete_DeletesTheLocalModification()
    {
        var policy = new SyncPolicy(DeleteVsModify: DeleteConflictPolicy.PreferDelete);

        var plan = Plan(M(("a.md", A)), M(("a.md", B)), Manifest.Empty, policy);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.DeleteLocal(P("a.md"), B));
        plan.ExpectedNewBase.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-11")]
    public void Row11_DeletedLocallyModifiedRemotely_RestoresTheRemoteVersion()
    {
        var plan = Plan(M(("a.md", A)), Manifest.Empty, M(("a.md", B)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Download(P("a.md"), B));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-11")]
    public void Row11_PreferDelete_DeletesTheRemoteModification()
    {
        var policy = new SyncPolicy(DeleteVsModify: DeleteConflictPolicy.PreferDelete);

        var plan = Plan(M(("a.md", A)), Manifest.Empty, M(("a.md", B)), policy);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.DeleteRemote(P("a.md")));
        plan.ExpectedNewBase.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-12")]
    public void Row12_AddedLocally_Uploads()
    {
        var plan = Plan(null, M(("a.md", A)), Manifest.Empty);

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Upload(P("a.md"), A));
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-13")]
    public void Row13_AddedRemotely_Downloads()
    {
        var plan = Plan(null, Manifest.Empty, M(("a.md", A)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.Download(P("a.md"), A));
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-14")]
    public void Row14_AddedOnBothSidesWithTheSameBlob_OnlyUpdatesTheBase()
    {
        var plan = Plan(null, M(("a.md", A)), M(("a.md", A)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.NoOp(P("a.md")));
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-15")]
    public void Row15_AddedOnBothSidesWithDifferentText_MergesAgainstAnEmptyBase()
    {
        var plan = Plan(null, M(("a.md", A)), M(("a.md", B)));

        plan.Actions.Should().ContainSingle().Which.Should().Be(new SyncAction.MergeText(P("a.md"), null, A, B));
        plan.ExpectedNewBase.Get(P("a.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-15")]
    public void Row15_AddedOnBothSidesWithDifferentBinary_KeepsBoth()
    {
        var plan = Plan(null, M(("a.png", A)), M(("a.png", B)));

        plan.Actions.Should().HaveCount(2);
        plan.Actions.Should().Contain(new SyncAction.Download(P("a.png"), B));
        plan.Actions.Should().Contain(new SyncAction.CreateConflictCopy(P("a.png"), ConflictSide.Local, A));
    }

    [Theory]
    [InlineData(TextConflictPolicy.PreferLocal, typeof(SyncAction.Upload))]
    [InlineData(TextConflictPolicy.PreferRemote, typeof(SyncAction.Download))]
    [Trait("Spec", "SYNC-ROW-5")]
    public void Row5_TextPreference_PicksTheWinningSide(TextConflictPolicy text, Type expected)
    {
        var policy = new SyncPolicy(TextConflicts: text);

        var plan = Plan(M(("a.md", A)), M(("a.md", B)), M(("a.md", C)), policy);

        plan.Actions.Should().ContainSingle().Which.GetType().Should().Be(expected);
    }

    [Theory]
    [InlineData(TextConflictPolicy.AutoMergeOrMarkers)]
    [InlineData(TextConflictPolicy.AutoMergeOrConflictCopy)]
    [Trait("Spec", "SYNC-ROW-5")]
    public void Row5_TextAutoMergePolicies_Merge(TextConflictPolicy text)
    {
        var plan = Plan(M(("a.md", A)), M(("a.md", B)), M(("a.md", C)), new SyncPolicy(TextConflicts: text));

        plan.Actions.Should().ContainSingle().Which.Should().BeOfType<SyncAction.MergeText>();
    }

    [Theory]
    [InlineData(BinaryConflictPolicy.PreferLocal, typeof(SyncAction.Upload))]
    [InlineData(BinaryConflictPolicy.PreferRemote, typeof(SyncAction.Download))]
    [Trait("Spec", "SYNC-ROW-6")]
    public void Row6_BinaryPreference_PicksTheWinningSide(BinaryConflictPolicy binary, Type expected)
    {
        var policy = new SyncPolicy(BinaryConflicts: binary);

        var plan = Plan(M(("a.png", A)), M(("a.png", B)), M(("a.png", C)), policy);

        plan.Actions.Should().ContainSingle().Which.GetType().Should().Be(expected);
    }

    [Fact]
    [Trait("Spec", "SYNC-7.3")]
    public void FileAgainstFolder_RenamesTheLocalFileToAConflictCopy()
    {
        var plan = Plan(null, M(("a", A)), M(("a/b.md", B)));

        plan.Actions.Should().Contain(new SyncAction.CreateConflictCopy(P("a"), ConflictSide.Local, A));
        plan.Actions.Should().Contain(new SyncAction.DeleteLocal(P("a"), A));
        plan.Actions.Should().Contain(new SyncAction.Download(P("a/b.md"), B));
        plan.ExpectedNewBase.Contains(P("a")).Should().BeFalse();
        plan.ExpectedNewBase.Get(P("a/b.md"))!.Value.Sha.Should().Be(B);
    }

    [Fact]
    [Trait("Spec", "SYNC-7.3")]
    public void FileAgainstFolder_RenamesTheRemoteFileToAConflictCopy()
    {
        var plan = Plan(null, M(("a/b.md", A)), M(("a", B)));

        plan.Actions.Should().Contain(new SyncAction.CreateConflictCopy(P("a"), ConflictSide.Remote, B));
        plan.Actions.Should().Contain(new SyncAction.DeleteRemote(P("a")));
        plan.Actions.Should().Contain(new SyncAction.Upload(P("a/b.md"), A));
        plan.ExpectedNewBase.Contains(P("a")).Should().BeFalse();
        plan.ExpectedNewBase.Get(P("a/b.md"))!.Value.Sha.Should().Be(A);
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-5")]
    public void Actions_AreOrderedByPath()
    {
        var plan = Plan(
            Manifest.Empty,
            M(("z.md", A), ("a.md", A), ("m.md", A)),
            Manifest.Empty);

        plan.Actions.Select(action => action.Path.ToString()).Should().Equal("a.md", "m.md", "z.md");
    }

    [Fact]
    [Trait("Spec", "SYNC-ROW-2")]
    public void Plan_ValidatesArguments()
    {
        FluentActions.Invoking(() => SyncPlanner.Plan(null!, Manifest.Empty, Manifest.Empty, SyncPolicy.Default))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SyncPlanner.Plan(SyncBase.Empty, null!, Manifest.Empty, SyncPolicy.Default))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SyncPlanner.Plan(SyncBase.Empty, Manifest.Empty, null!, SyncPolicy.Default))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SyncPlanner.Plan(SyncBase.Empty, Manifest.Empty, Manifest.Empty, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static readonly VaultPath[] PropertyPaths = [P("a.md"), P("b.png"), P("dir/c.md"), P("d.md")];

    private static Manifest Generated(int[] values)
    {
        var entries = new List<ManifestEntry>();
        for (var i = 0; i < Math.Min(values.Length, PropertyPaths.Length); i++)
        {
            entries.Add(values[i] switch
            {
                1 => new ManifestEntry(PropertyPaths[i], A, 1),
                2 => new ManifestEntry(PropertyPaths[i], B, 1),
                3 => new ManifestEntry(PropertyPaths[i], C, 1),
                _ => default,
            });
        }

        entries.RemoveAll(entry => entry.Path.IsRoot);
        return Manifest.Create(entries).Value;
    }

    private static bool Referenced(SyncPlan plan, VaultPath path, BlobSha sha) =>
        plan.Actions.Any(action => action.Path == path && action switch
        {
            SyncAction.Upload upload => upload.LocalSha == sha,
            SyncAction.Download download => download.RemoteSha == sha,
            SyncAction.DeleteLocal delete => delete.ExpectedLocalSha == sha,
            SyncAction.MergeText merge => merge.BaseSha == sha || merge.LocalSha == sha || merge.RemoteSha == sha,
            SyncAction.CreateConflictCopy copy => copy.Sha == sha,
            _ => false,
        });

    [BasaltProperty]
    [Trait("Spec", "SYNC-P1")]
    public bool P1_EveryChangedShaEndsInTheBaseOrAnAction(int[] b, int[] l, int[] r)
    {
        var baseline = Generated(b);
        var local = Generated(l);
        var remote = Generated(r);
        var plan = SyncPlanner.Plan(new SyncBase(null, null, baseline), local, remote, SyncPolicy.Default);

        foreach (var path in local.Entries.Select(e => e.Path).Concat(remote.Entries.Select(e => e.Path)).Distinct())
        {
            var baseEntry = baseline.Get(path);
            var localEntry = local.Get(path);
            var remoteEntry = remote.Get(path);
            if (localEntry is { } le && le.Sha != baseEntry?.Sha
                && plan.ExpectedNewBase.Get(path)?.Sha != le.Sha
                && !Referenced(plan, path, le.Sha))
            {
                return false;
            }

            if (remoteEntry is { } re && re.Sha != baseEntry?.Sha
                && plan.ExpectedNewBase.Get(path)?.Sha != re.Sha
                && !Referenced(plan, path, re.Sha))
            {
                return false;
            }
        }

        return true;
    }

    [BasaltProperty]
    [Trait("Spec", "SYNC-P2")]
    public bool P2_PlanningAgainstTheExpectedBaseIsEmpty(int[] b, int[] l, int[] r)
    {
        var plan = SyncPlanner.Plan(new SyncBase(null, null, Generated(b)), Generated(l), Generated(r), SyncPolicy.Default);
        var settled = plan.ExpectedNewBase;

        return SyncPlanner.Plan(new SyncBase(null, null, settled), settled, settled, SyncPolicy.Default).IsEmpty;
    }

    [BasaltProperty]
    [Trait("Spec", "SYNC-P3")]
    public bool P3_TwoDevicesConvergeWithPreferRemote(int[] b, int[] l1, int[] l2)
    {
        var policy = new SyncPolicy(TextConflicts: TextConflictPolicy.PreferRemote, BinaryConflicts: BinaryConflictPolicy.PreferRemote, DeleteVsModify: DeleteConflictPolicy.PreferDelete);
        var baseline = Generated(b);
        var remote = baseline;
        var first = SyncPlanner.Plan(new SyncBase(null, null, baseline), Generated(l1), remote, policy).ExpectedNewBase;
        var second = SyncPlanner.Plan(new SyncBase(null, null, baseline), Generated(l2), first, policy).ExpectedNewBase;
        var third = SyncPlanner.Plan(new SyncBase(null, null, first), first, second, policy).ExpectedNewBase;

        return third == second;
    }

    [BasaltProperty]
    [Trait("Spec", "SYNC-P4")]
    public bool P4_ExpectedBaseOnlyContainsLocalOrRemoteShas(int[] b, int[] l, int[] r)
    {
        var local = Generated(l);
        var remote = Generated(r);
        var plan = SyncPlanner.Plan(new SyncBase(null, null, Generated(b)), local, remote, SyncPolicy.Default);

        return plan.ExpectedNewBase.Entries.All(entry =>
            local.Get(entry.Path)?.Sha == entry.Sha || remote.Get(entry.Path)?.Sha == entry.Sha);
    }

    [BasaltProperty]
    [Trait("Spec", "SYNC-P5")]
    public bool P5_DeleteLocalAlwaysMatchesTheLocalEntry(int[] b, int[] l, int[] r)
    {
        var local = Generated(l);
        var plan = SyncPlanner.Plan(new SyncBase(null, null, Generated(b)), local, Generated(r), SyncPolicy.Default);

        return plan.Actions.OfType<SyncAction.DeleteLocal>().All(action => local.Get(action.Path)?.Sha == action.ExpectedLocalSha);
    }
}
