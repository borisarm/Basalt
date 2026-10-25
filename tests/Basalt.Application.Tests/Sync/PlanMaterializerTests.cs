using System.Text;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Fakes;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class PlanMaterializerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Device = "Laptop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobSha Sha(string text) => GitBlobHasher.Compute(Utf8(text));

    private static BlobSha Sha(byte[] content) => GitBlobHasher.Compute(content);

    private static Manifest ManifestOf(params (string Path, byte[] Content)[] files) =>
        Manifest.Create(files.Select(file => new ManifestEntry(P(file.Path), Sha(file.Content), file.Content.LongLength))).Value;

    private static async Task<InMemoryVaultFileSystem> LocalAsync(params (string Path, byte[] Content)[] files)
    {
        var fileSystem = new InMemoryVaultFileSystem();
        foreach (var (path, content) in files)
        {
            (await fileSystem.WriteAllBytesAsync(P(path), content, null, Ct)).IsSuccess.Should().BeTrue();
        }

        return fileSystem;
    }

    private static async Task<InMemoryRemoteRepository> RemoteAsync(params (string Path, byte[] Content)[] files)
    {
        var repository = new InMemoryRemoteRepository(
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"));
        var changes = new List<TreeChange>();
        foreach (var (path, content) in files)
        {
            var blob = (await repository.CreateBlobAsync(content, Ct)).Value;
            changes.Add(new TreeChange.Upsert(P(path), blob));
        }

        var tree = (await repository.CreateTreeAsync(null, changes, Ct)).Value;
        var commit = (await repository.CreateCommitAsync(
            "initial",
            tree,
            [],
            new CommitAuthor("Basalt Test", "test@basalt.local"),
            Ct)).Value;
        (await repository.UpdateBranchAsync(commit, null, Ct)).IsSuccess.Should().BeTrue();
        return repository;
    }

    private static async Task<RemoteSnapshot> SnapshotAsync(IRemoteRepository repository) =>
        (await new RemoteSnapshotProvider(repository).GetAsync(Ct)).Value;

    private static PlanMaterializer Materializer(IVaultFileSystem fileSystem, IRemoteRepository repository) =>
        new(fileSystem, repository);

    [Fact]
    public async Task Download_StagesTheVerifiedBlobAndTargetsItsSha()
    {
        var content = Utf8("remote");
        var repository = await RemoteAsync(("a.md", content));
        var remote = await SnapshotAsync(repository);
        var plan = SyncPlanner.Plan(SyncBase.Empty, Manifest.Empty, remote.Manifest, SyncPolicy.Default);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(content));
        result.Value.Target.Get(P("a.md"))!.Value.Size.Should().Be(content.Length);
        result.Value.Contents[Sha(content)].Should().Equal(content);
        result.Value.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_TargetsTheLocalEntryWithoutDownloadingAnything()
    {
        var content = Utf8("local");
        var fileSystem = await LocalAsync(("a.md", content));
        var local = ManifestOf(("a.md", content));
        var repository = Substitute.For<IRemoteRepository>();
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);
        var plan = SyncPlanner.Plan(SyncBase.Empty, local, Manifest.Empty, SyncPolicy.Default);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(content));
        result.Value.Contents.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeText_CleanMergeStagesTheResultAndTargetsItsNewSha()
    {
        var baseText = "one\ntwo\nthree\n";
        var localText = "LOCAL\ntwo\nthree\n";
        var remoteText = "one\ntwo\nREMOTE\n";
        var repository = await RemoteAsync(("a.md", Utf8(remoteText)));
        (await repository.CreateBlobAsync(Utf8(baseText), Ct)).IsSuccess.Should().BeTrue();
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", Utf8(localText)));
        var local = ManifestOf(("a.md", Utf8(localText)));
        var baseline = new SyncBase(null, null, ManifestOf(("a.md", Utf8(baseText))));
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, SyncPolicy.Default);
        plan.Actions.Should().ContainSingle(action => action is SyncAction.MergeText);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        var merged = "LOCAL\ntwo\nREMOTE\n";
        result.IsSuccess.Should().BeTrue();
        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(merged));
        result.Value.Contents[Sha(merged)].Should().Equal(Utf8(merged));
        result.Value.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeText_ConflictCopyKeepsTheRemoteVersionAndUploadsTheLocalCopy()
    {
        var baseText = "base\n";
        var localText = "local\n";
        var remoteText = "remote\n";
        var repository = await RemoteAsync(("a.md", Utf8(remoteText)));
        (await repository.CreateBlobAsync(Utf8(baseText), Ct)).IsSuccess.Should().BeTrue();
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", Utf8(localText)));
        var local = ManifestOf(("a.md", Utf8(localText)));
        var baseline = new SyncBase(null, null, ManifestOf(("a.md", Utf8(baseText))));
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, SyncPolicy.Default);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        var conflict = result.Value.Conflicts.Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(SyncConflictKind.ConflictCopy);
        conflict.Path.Should().Be(P("a.md"));
        conflict.CopyPath.Should().NotBeNull();
        conflict.CopyPath!.Value.Name.Should().Be("a (conflict 2026-10-21 120000 Laptop).md");
        conflict.Regions.Should().NotBeNull();
        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(remoteText));
        result.Value.Target.Get(conflict.CopyPath.Value)!.Value.Sha.Should().Be(Sha(localText));
        result.Value.Contents[Sha(localText)].Should().Equal(Utf8(localText));
    }

    [Fact]
    public async Task MergeText_MarkerPolicyStagesTheTextWithConflictMarkers()
    {
        var repository = await RemoteAsync(("a.md", Utf8("remote\n")));
        (await repository.CreateBlobAsync(Utf8("base\n"), Ct)).IsSuccess.Should().BeTrue();
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", Utf8("local\n")));
        var local = ManifestOf(("a.md", Utf8("local\n")));
        var baseline = new SyncBase(null, null, ManifestOf(("a.md", Utf8("base\n"))));
        var policy = SyncPolicy.Default with { TextConflicts = TextConflictPolicy.AutoMergeOrMarkers };
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, policy);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, policy, Now, Device, Ct);

        var conflict = result.Value.Conflicts.Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(SyncConflictKind.Markers);
        conflict.CopyPath.Should().BeNull();
        conflict.Regions.Should().NotBeEmpty();
        var text = Encoding.UTF8.GetString(result.Value.Contents[result.Value.Target.Get(P("a.md"))!.Value.Sha]);
        text.Should().Contain("<<<<<<< Laptop").And.Contain(">>>>>>> GitHub");
    }

    [Fact]
    public async Task MergeText_FallsBackToTheBinaryPolicyWhenTheBytesAreNotText()
    {
        var localContent = new byte[] { (byte)'a', 0, (byte)'b' };
        var repository = await RemoteAsync(("note.md", Utf8("remote")));
        (await repository.CreateBlobAsync(Utf8("base"), Ct)).IsSuccess.Should().BeTrue();
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("note.md", localContent));
        var local = ManifestOf(("note.md", localContent));
        var baseline = new SyncBase(null, null, ManifestOf(("note.md", Utf8("base"))));
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, SyncPolicy.Default);
        plan.Actions.Should().ContainSingle(action => action is SyncAction.MergeText);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        var conflict = result.Value.Conflicts.Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(SyncConflictKind.ConflictCopy);
        result.Value.Target.Get(P("note.md"))!.Value.Sha.Should().Be(Sha(Utf8("remote")));
        result.Value.Target.Get(conflict.CopyPath!.Value)!.Value.Sha.Should().Be(Sha(localContent));
    }

    [Fact]
    public async Task MergeText_WhenTheBaseBlobIsGone_UsesAnEmptyBase()
    {
        var missingBase = BlobSha.Parse("0000000000000000000000000000000000000000").Value;
        var localContent = Utf8("local\n");
        var repository = await RemoteAsync(("a.md", Utf8("remote\n")));
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", localContent));
        var local = ManifestOf(("a.md", localContent));
        var baseline = new SyncBase(null, null, Manifest.Create([
            new ManifestEntry(P("a.md"), missingBase, 0),
        ]).Value);
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, SyncPolicy.Default);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Conflicts.Should().ContainSingle();
    }

    [Fact]
    public async Task FileFolderConflict_CopiesTheLocalFileAndLeavesTheRemoteFolder()
    {
        var localContent = Utf8("file");
        var fileSystem = await LocalAsync(("a.md", localContent));
        var local = ManifestOf(("a.md", localContent));
        var repository = await RemoteAsync(("a.md/inner.md", Utf8("inner")));
        var remote = await SnapshotAsync(repository);
        var plan = SyncPlanner.Plan(SyncBase.Empty, local, remote.Manifest, SyncPolicy.Default);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        var conflict = result.Value.Conflicts.Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(SyncConflictKind.FileFolder);
        result.Value.Target.Get(P("a.md/inner.md")).Should().NotBeNull();
        result.Value.Target.Get(conflict.CopyPath!.Value)!.Value.Sha.Should().Be(Sha(localContent));
        result.Value.Target.Contains(P("a.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenTheBlobDoesNotMatchItsSha_FailsWithShaMismatch()
    {
        var expected = Sha("expected");
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetBlobAsync(expected, Arg.Any<CancellationToken>())
            .Returns(Result.Success(Utf8("other")));
        var remoteEntry = new ManifestEntry(P("a.md"), expected, 8);
        var manifest = Manifest.Create([remoteEntry]).Value;
        var remote = new RemoteSnapshot(null, manifest, null, [], NotModified: false);
        var plan = new SyncPlan([new SyncAction.Download(P("a.md"), expected)], manifest);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(SyncErrors.ShaMismatch);
        result.Error.Message.Should().Be("A downloaded blob does not match its SHA.");
        result.Error.Metadata!.Keys.Should().BeEquivalentTo(["expected", "actual"]);
    }

    [Fact]
    public async Task MergeText_WhenTheLocalFileDisappeared_FailsWithLocalFileMissing()
    {
        var baseText = "base\n";
        var localText = "local\n";
        var remoteText = "remote\n";
        var repository = await RemoteAsync(("a.md", Utf8(remoteText)));
        (await repository.CreateBlobAsync(Utf8(baseText), Ct)).IsSuccess.Should().BeTrue();
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", Utf8(localText)));
        var local = ManifestOf(("a.md", Utf8(localText)));
        var baseline = new SyncBase(null, null, ManifestOf(("a.md", Utf8(baseText))));
        var plan = SyncPlanner.Plan(baseline, local, remote.Manifest, SyncPolicy.Default);
        await fileSystem.DeleteAsync(P("a.md"), Ct);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(SyncErrors.LocalFileMissing);
        result.Error.Message.Should().Be("The local file disappeared during the sync.");
        result.Error.Metadata!["path"].Should().Be("a.md");
        result.Error.Metadata!["cause"].Should().Be(Basalt.Application.Errors.ErrorCodes.FsNotFound);
    }

    [Fact]
    public async Task Downloads_AboveTheThreshold_UseOneArchiveInsteadOfManyBlobs()
    {
        var files = Enumerable.Range(0, 201)
            .Select(index => (Path: "n/" + index + ".md", Content: Utf8("file " + index)))
            .ToArray();
        var manifest = ManifestOf(files);
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, manifest, "\"etag\"", [], NotModified: false);
        var plan = new SyncPlan(
            manifest.Entries.Select(entry => (SyncAction)new SyncAction.Download(entry.Path, entry.Sha)).ToList(),
            manifest);
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetArchiveAsync(head.Commit, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<VaultPath, byte[]>>(
                files.ToDictionary(file => P(file.Path), file => file.Content)));

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Value.Target.Count.Should().Be(201);
        result.Value.Contents.Should().HaveCount(201);
        await repository.Received(1).GetArchiveAsync(head.Commit, Arg.Any<CancellationToken>());
        await repository.DidNotReceive().GetBlobAsync(Arg.Any<BlobSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Downloads_WithAnIncompleteArchive_FallBackToTheBlobApi()
    {
        var files = Enumerable.Range(0, 201)
            .Select(index => (Path: "n/" + index + ".md", Content: Utf8("file " + index)))
            .ToArray();
        var manifest = ManifestOf(files);
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, manifest, "\"etag\"", [], NotModified: false);
        var plan = new SyncPlan(
            manifest.Entries.Select(entry => (SyncAction)new SyncAction.Download(entry.Path, entry.Sha)).ToList(),
            manifest);
        var missing = files[0];
        var archive = files.Skip(1).ToDictionary(file => P(file.Path), file => file.Content);
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetArchiveAsync(head.Commit, Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<VaultPath, byte[]>>(archive));
        repository.GetBlobAsync(Sha(missing.Content), Arg.Any<CancellationToken>())
            .Returns(Result.Success(missing.Content));

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Value.Contents.Should().HaveCount(201);
        await repository.Received(1).GetBlobAsync(Sha(missing.Content), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergeText_PreferLocalKeepsTheLocalVersionWithoutABase()
    {
        var localContent = Utf8("local\n");
        var remoteContent = Utf8("remote\n");
        var repository = await RemoteAsync(("a.md", remoteContent));
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", localContent));
        var local = ManifestOf(("a.md", localContent));
        var policy = SyncPolicy.Default with { TextConflicts = TextConflictPolicy.PreferLocal };
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.md"), null, Sha(localContent), Sha(remoteContent))],
            remote.Manifest);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, policy, Now, Device, Ct);

        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(localContent));
        result.Value.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeText_PreferRemoteKeepsTheRemoteVersionAndReusesTheBaseWhenItMatchesLocal()
    {
        var localContent = Utf8("local\n");
        var remoteContent = Utf8("remote\n");
        var repository = await RemoteAsync(("a.md", remoteContent));
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", localContent));
        var local = ManifestOf(("a.md", localContent));
        var policy = SyncPolicy.Default with { TextConflicts = TextConflictPolicy.PreferRemote };
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.md"), Sha(localContent), Sha(localContent), Sha(remoteContent))],
            remote.Manifest);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, policy, Now, Device, Ct);

        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(remoteContent));
    }

    [Fact]
    public async Task MergeText_WhenTheBaseMatchesRemote_UsesTheRemoteBytesAsTheBase()
    {
        var localContent = Utf8("local\n");
        var remoteContent = Utf8("remote\n");
        var repository = await RemoteAsync(("a.md", remoteContent));
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.md", localContent));
        var local = ManifestOf(("a.md", localContent));
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.md"), Sha(remoteContent), Sha(localContent), Sha(remoteContent))],
            remote.Manifest);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Value.Target.Get(P("a.md"))!.Value.Sha.Should().Be(Sha(localContent));
    }

    [Fact]
    public async Task MergeText_BinaryPreferencesChooseTheRequestedSide()
    {
        var localContent = new byte[] { 0, 1, 2 };
        var remoteContent = new byte[] { 3, 4, 5 };
        var repository = await RemoteAsync(("a.png", remoteContent));
        var remote = await SnapshotAsync(repository);
        var fileSystem = await LocalAsync(("a.png", localContent));
        var local = ManifestOf(("a.png", localContent));
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.png"), null, Sha(localContent), Sha(remoteContent))],
            remote.Manifest);

        var preferLocal = await Materializer(fileSystem, repository).MaterializeAsync(
            plan,
            local,
            remote,
            SyncPolicy.Default with { BinaryConflicts = BinaryConflictPolicy.PreferLocal },
            Now,
            Device,
            Ct);
        var preferRemote = await Materializer(fileSystem, repository).MaterializeAsync(
            plan,
            local,
            remote,
            SyncPolicy.Default with { BinaryConflicts = BinaryConflictPolicy.PreferRemote },
            Now,
            Device,
            Ct);

        preferLocal.Value.Target.Get(P("a.png"))!.Value.Sha.Should().Be(Sha(localContent));
        preferRemote.Value.Target.Get(P("a.png"))!.Value.Sha.Should().Be(Sha(remoteContent));
    }

    [Fact]
    public async Task CreateConflictCopy_FromTheRemoteSideStagesTheRemoteBlob()
    {
        var remoteContent = Utf8("remote");
        var repository = await RemoteAsync(("a.md", remoteContent));
        var remote = await SnapshotAsync(repository);
        var plan = new SyncPlan(
            [
                new SyncAction.DeleteRemote(P("a.md")),
                new SyncAction.CreateConflictCopy(P("a.md"), ConflictSide.Remote, Sha(remoteContent)),
            ],
            Manifest.Empty);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        var conflict = result.Value.Conflicts.Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(SyncConflictKind.FileFolder);
        result.Value.Target.Get(conflict.CopyPath!.Value)!.Value.Sha.Should().Be(Sha(remoteContent));
    }

    [Fact]
    public async Task DeleteAndNoOpActions_OnlyChangeTheTargetManifest()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);
        var plan = new SyncPlan(
            [
                new SyncAction.DeleteLocal(P("a.md"), Sha("a")),
                new SyncAction.DeleteRemote(P("b.md")),
                new SyncAction.NoOp(P("c.md")),
            ],
            Manifest.Empty);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Target.IsEmpty.Should().BeTrue();
        result.Value.Contents.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WithoutTheLocalEntry_LeavesTheTargetUnchanged()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);
        var plan = new SyncPlan([new SyncAction.Upload(P("a.md"), Sha("a"))], Manifest.Empty);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Target.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownAction_FailsWithValidationError()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);
        var plan = new SyncPlan([new UnknownAction(P("a.md"))], Manifest.Empty);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(RemoteErrors.ValidationFailed);
    }

    [Fact]
    public async Task MergeText_WhenTheLocalFileIsGone_FailsWithLocalFileMissing()
    {
        var remoteContent = Utf8("remote\n");
        var repository = await RemoteAsync(("a.md", remoteContent));
        var remote = await SnapshotAsync(repository);
        var local = ManifestOf(("a.md", Utf8("local\n")));
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.md"), null, Sha("local\n"), Sha(remoteContent))],
            remote.Manifest);

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, local, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(SyncErrors.LocalFileMissing);
    }

    [Fact]
    public async Task MergeText_WhenTheBaseCannotBeRead_Fails()
    {
        var localContent = Utf8("local\n");
        var remoteContent = Utf8("remote\n");
        var baseSha = Sha("base\n");
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetBlobAsync(Sha(remoteContent), Arg.Any<CancellationToken>())
            .Returns(Result.Success(remoteContent));
        repository.GetBlobAsync(baseSha, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<byte[]>(new Error(RemoteErrors.ServerError, "boom")));
        var remoteManifest = Manifest.Create([new ManifestEntry(P("a.md"), Sha(remoteContent), remoteContent.LongLength)]).Value;
        var remote = new RemoteSnapshot(null, remoteManifest, null, [], NotModified: false);
        var fileSystem = await LocalAsync(("a.md", localContent));
        var plan = new SyncPlan(
            [new SyncAction.MergeText(P("a.md"), baseSha, Sha(localContent), Sha(remoteContent))],
            remoteManifest);

        var result = await Materializer(fileSystem, repository)
            .MaterializeAsync(plan, ManifestOf(("a.md", localContent)), remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(RemoteErrors.ServerError);
    }

    [Fact]
    public async Task Downloads_WhenTheArchiveFails_PropagatesTheError()
    {
        var files = Enumerable.Range(0, 201)
            .Select(index => (Path: "n/" + index + ".md", Content: Utf8("file " + index)))
            .ToArray();
        var manifest = ManifestOf(files);
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, manifest, "\"etag\"", [], NotModified: false);
        var plan = new SyncPlan(
            manifest.Entries.Select(entry => (SyncAction)new SyncAction.Download(entry.Path, entry.Sha)).ToList(),
            manifest);
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetArchiveAsync(head.Commit, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyDictionary<VaultPath, byte[]>>(
                new Error(RemoteErrors.Unauthorized, "no token")));

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Error.Code.Should().Be(RemoteErrors.Unauthorized);
    }

    private sealed record UnknownAction(VaultPath Path) : SyncAction(Path);

    [Fact]
    public async Task Downloads_AtOrBelowTheThreshold_DoNotRequestAnArchive()
    {
        var content = Utf8("one");
        var entry = new ManifestEntry(P("a.md"), Sha(content), content.LongLength);
        var manifest = Manifest.Create([entry]).Value;
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, manifest, "\"etag\"", [], NotModified: false);
        var plan = new SyncPlan([new SyncAction.Download(P("a.md"), entry.Sha)], manifest);
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetBlobAsync(entry.Sha, Arg.Any<CancellationToken>()).Returns(Result.Success(content));

        var result = await Materializer(new InMemoryVaultFileSystem(), repository)
            .MaterializeAsync(plan, Manifest.Empty, remote, SyncPolicy.Default, Now, Device, Ct);

        result.Value.Contents[entry.Sha].Should().Equal(content);
        await repository.DidNotReceive().GetArchiveAsync(Arg.Any<CommitSha>(), Arg.Any<CancellationToken>());
    }
}
