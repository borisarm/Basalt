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

public sealed class RemotePublisherTests
{
    private static readonly CommitAuthor Author = new("Basalt Test", "test@basalt.local");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobSha Sha(string text) => GitBlobHasher.Compute(Utf8(text));

    private static BlobSha Sha(byte[] content) => GitBlobHasher.Compute(content);

    private static Manifest ManifestOf(params (string Path, byte[] Content)[] files) =>
        Manifest.Create(files.Select(file => new ManifestEntry(P(file.Path), Sha(file.Content), file.Content.LongLength))).Value;

    private static MaterializedPlan Plan(Manifest target, params (string Path, byte[] Content)[] staged) =>
        new(target, staged.ToDictionary(file => Sha(file.Content), file => file.Content), []);

    private static PublishOptions Options(int attempts = 5) =>
        new("Sync from Laptop", Author, attempts, _ => TimeSpan.Zero);

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
        var commit = (await repository.CreateCommitAsync("initial", tree, [], Author, Ct)).Value;
        (await repository.UpdateBranchAsync(commit, null, Ct)).IsSuccess.Should().BeTrue();
        return repository;
    }

    private static async Task<InMemoryRemoteRepository> InitializedAsync()
    {
        var repository = new InMemoryRemoteRepository(
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"));
        (await repository.CreateInitialCommitAsync(P(".basalt/vault.json"), Utf8("{}"), "init", Ct))
            .IsSuccess.Should().BeTrue();
        return repository;
    }

    private static async Task<RemoteSnapshot> SnapshotAsync(IRemoteRepository repository) =>
        (await new RemoteSnapshotProvider(repository).GetAsync(Ct)).Value;

    private static RemotePublisher Publisher(IVaultFileSystem fileSystem, IRemoteRepository repository) =>
        new(fileSystem, repository, TimeProvider.System);

    [Fact]
    public async Task Publish_WhenTheTargetAlreadyMatchesTheRemote_DoesNothing()
    {
        var repository = await RemoteAsync(("a.md", Utf8("A")));
        var remote = await SnapshotAsync(repository);
        var commits = repository.CommitWrites;
        var trees = repository.TreeWrites;
        var request = new PublishRequest(Plan(remote.Manifest), remote, Manifest.Empty);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(request, Options(), null, Ct);

        result.Value.Published.Should().BeFalse();
        result.Value.Head.Should().Be(remote.Head);
        repository.CommitWrites.Should().Be(commits);
        repository.TreeWrites.Should().Be(trees);
    }

    [Fact]
    public async Task Publish_ToAnEmptyRepository_InitializesItWithTheVaultConfigAndUploadsTheRest()
    {
        var fileSystem = await LocalAsync((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var repository = new InMemoryRemoteRepository(
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"));
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var request = new PublishRequest(Plan(target), new RemoteSnapshot(null, Manifest.Empty, null, [], false), Manifest.Empty);

        var result = await Publisher(fileSystem, repository).PublishAsync(request, Options(), null, Ct);

        result.Value.Published.Should().BeTrue();
        repository.InitialCommitWrites.Should().Be(1);
        repository.CommitWrites.Should().Be(1);
        repository.BlobWrites.Should().Be(0, "both files are UTF-8 text sent inline");
        var published = await SnapshotAsync(repository);
        published.Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", "a.md");
    }

    [Fact]
    public async Task Publish_SendsTextInlineAndBinaryFilesThroughBlobs()
    {
        var repository = await InitializedAsync();
        var remote = await SnapshotAsync(repository);
        var binary = new byte[] { 1, 2, 3 };
        var target = ManifestOf(
            (".basalt/vault.json", Utf8("{}")),
            ("a.md", Utf8("A")),
            ("image.png", binary));
        var plan = Plan(
            target,
            (".basalt/vault.json", Utf8("{}")),
            ("a.md", Utf8("A")),
            ("image.png", binary));

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Value.Published.Should().BeTrue();
        repository.BlobWrites.Should().Be(1);
        (await SnapshotAsync(repository)).Manifest.Count.Should().Be(3);
    }

    [Fact]
    public async Task Publish_ChunksLargeChangeSets()
    {
        var repository = await InitializedAsync();
        var remote = await SnapshotAsync(repository);
        var files = Enumerable.Range(0, 1_001)
            .Select(index => ("n/" + index + ".md", Utf8("file " + index)))
            .ToArray();
        var target = ManifestOf([(".basalt/vault.json", Utf8("{}")), .. files]);
        var plan = Plan(target, [(".basalt/vault.json", Utf8("{}")), .. files]);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Value.Published.Should().BeTrue();
        repository.TreeWrites.Should().BeGreaterThan(1);
        (await SnapshotAsync(repository)).Manifest.Count.Should().Be(1_002);
    }

    [Fact]
    public async Task Publish_WhenTheCreatedTreeDoesNotMatch_FailsWithoutCommit()
    {
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, Manifest.Empty, "\"etag\"", [], NotModified: false);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var repository = Substitute.For<IRemoteRepository>();
        repository.CreateTreeAsync(head.Tree, Arg.Any<IReadOnlyList<TreeChange>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TreeSha.Parse("3333333333333333333333333333333333333333").Value));

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be(SyncErrors.TreeMismatch);
        await repository.DidNotReceive().CreateCommitAsync(
            Arg.Any<string>(),
            Arg.Any<TreeSha>(),
            Arg.Any<IReadOnlyList<CommitSha>>(),
            Arg.Any<CommitAuthor>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_RetriesThroughTheRefreshCallbackAfterANonFastForward()
    {
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, Manifest.Empty, "\"etag\"", [], NotModified: false);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var expectedTree = GitTreeHasher.Compute(target.Entries.ToDictionary(entry => entry.Path, entry => entry.Sha));
        var commit = CommitSha.Parse("4444444444444444444444444444444444444444").Value;
        var repository = Substitute.For<IRemoteRepository>();
        repository.CreateTreeAsync(Arg.Any<TreeSha?>(), Arg.Any<IReadOnlyList<TreeChange>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedTree));
        repository.CreateCommitAsync(
            Arg.Any<string>(),
            expectedTree,
            Arg.Any<IReadOnlyList<CommitSha>>(),
            Arg.Any<CommitAuthor>(),
            Arg.Any<CancellationToken>()).Returns(Result.Success(commit));
        repository.UpdateBranchAsync(commit, head.Commit, Arg.Any<CancellationToken>())
            .Returns(
                Result.Failure<Unit>(new Error(RemoteErrors.NonFastForward, "moved")),
                Result.Success());
        var request = new PublishRequest(plan, remote, Manifest.Empty);
        var refreshes = 0;

        var result = await Publisher(new InMemoryVaultFileSystem(), repository).PublishAsync(
            request,
            Options(),
            _ =>
            {
                refreshes++;
                return Task.FromResult(Result.Success(request));
            },
            Ct);

        result.Value.Published.Should().BeTrue();
        result.Value.Attempts.Should().Be(2);
        refreshes.Should().Be(1);
    }

    [Fact]
    public async Task Publish_ExhaustsTheAttemptsWhenTheBranchKeepsMoving()
    {
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, Manifest.Empty, "\"etag\"", [], NotModified: false);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var expectedTree = GitTreeHasher.Compute(target.Entries.ToDictionary(entry => entry.Path, entry => entry.Sha));
        var repository = Substitute.For<IRemoteRepository>();
        repository.CreateTreeAsync(Arg.Any<TreeSha?>(), Arg.Any<IReadOnlyList<TreeChange>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedTree));
        repository.CreateCommitAsync(
            Arg.Any<string>(),
            Arg.Any<TreeSha>(),
            Arg.Any<IReadOnlyList<CommitSha>>(),
            Arg.Any<CommitAuthor>(),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(CommitSha.Parse("4444444444444444444444444444444444444444").Value));
        repository.UpdateBranchAsync(Arg.Any<CommitSha>(), Arg.Any<CommitSha?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Unit>(new Error(RemoteErrors.NonFastForward, "moved")));
        var request = new PublishRequest(plan, remote, Manifest.Empty);
        var refreshes = 0;

        var result = await Publisher(new InMemoryVaultFileSystem(), repository).PublishAsync(
            request,
            Options(attempts: 2),
            _ =>
            {
                refreshes++;
                return Task.FromResult(Result.Success(request));
            },
            Ct);

        result.Error.Code.Should().Be(RemoteErrors.NonFastForward);
        refreshes.Should().Be(1);
    }

    [Fact]
    public async Task Publish_DeletesRemoteFilesThatAreNotInTheTarget()
    {
        var repository = await RemoteAsync(
            (".basalt/vault.json", Utf8("{}")),
            ("old.md", Utf8("old")));
        var remote = await SnapshotAsync(repository);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")));
        var request = new PublishRequest(Plan(target), remote, Manifest.Empty);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(request, Options(), null, Ct);

        result.Value.Published.Should().BeTrue();
        (await SnapshotAsync(repository)).Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json");
    }

    [Fact]
    public async Task Publish_KeepsProtectedRemotePathsAndVerifiesTheMergedTree()
    {
        var repository = await RemoteAsync(
            (".basalt/vault.json", Utf8("{}")),
            ("secret.txt", Utf8("remote secret")));
        var remote = await SnapshotAsync(repository);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("A.md", Utf8("A")));
        var request = new PublishRequest(
            Plan(target, ("A.md", Utf8("A"))),
            remote,
            Manifest.Empty,
            [P("secret.txt")]);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(request, Options(), null, Ct);

        result.Value.Published.Should().BeTrue();
        (await SnapshotAsync(repository)).Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", "A.md", "secret.txt");
    }

    [Fact]
    public async Task Publish_WhenALocalFileIsMissing_FailsWithLocalFileMissing()
    {
        var repository = await InitializedAsync();
        var remote = await SnapshotAsync(repository);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var request = new PublishRequest(Plan(target), remote, Manifest.Empty);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(request, Options(), null, Ct);

        result.Error.Code.Should().Be(SyncErrors.LocalFileMissing);
    }

    [Fact]
    public async Task Publish_WhenTheLocalContentDoesNotMatchItsSha_Fails()
    {
        var repository = await InitializedAsync();
        var remote = await SnapshotAsync(repository);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("expected")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")));
        var fileSystem = await LocalAsync(("a.md", Utf8("changed")));
        var request = new PublishRequest(plan, remote, Manifest.Empty);

        var result = await Publisher(fileSystem, repository).PublishAsync(request, Options(), null, Ct);

        result.Error.Code.Should().Be(SyncErrors.ShaMismatch);
    }

    [Fact]
    public async Task Publish_WhenInitializationFails_PropagatesTheError()
    {
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")));
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(null, null, NotModified: false)));
        repository.CreateInitialCommitAsync(
            Arg.Any<VaultPath>(),
            Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(Result.Failure(new Error(RemoteErrors.Unauthorized, "no token")));
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be(RemoteErrors.Unauthorized);
    }

    [Fact]
    public async Task Publish_ToAnEmptyRepositoryWithoutTheVaultConfig_Fails()
    {
        var target = ManifestOf(("a.md", Utf8("A")));
        var plan = Plan(target, ("a.md", Utf8("A")));
        var repository = Substitute.For<IRemoteRepository>();
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], NotModified: false);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be(SyncErrors.LocalFileMissing);
        await repository.DidNotReceive().CreateInitialCommitAsync(
            Arg.Any<VaultPath>(),
            Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
    [Fact]
    public void Publisher_RejectsNullDependencies()
    {
        FluentActions.Invoking(() => new RemotePublisher(null!, Substitute.For<IRemoteRepository>()))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new RemotePublisher(Substitute.For<IVaultFileSystem>(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Publish_RejectsNullArgumentsAndAnInvalidAttemptCount()
    {
        var publisher = new RemotePublisher(Substitute.For<IVaultFileSystem>(), Substitute.For<IRemoteRepository>());
        var request = new PublishRequest(Plan(Manifest.Empty), new RemoteSnapshot(null, Manifest.Empty, null, [], false), Manifest.Empty);

        await FluentActions.Awaiting(() => publisher.PublishAsync(null!, Options(), null, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => publisher.PublishAsync(request, null!, null, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => publisher.PublishAsync(request, Options(attempts: 0), null, Ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Publish_WhenTheRefreshFails_ReturnsItsError()
    {
        var head = new RemoteHead(CommitSha.Parse("1111111111111111111111111111111111111111").Value, TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, Manifest.Empty, "\"etag\"", [], false);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var expectedTree = GitTreeHasher.Compute(target.Entries.ToDictionary(entry => entry.Path, entry => entry.Sha));
        var repository = Substitute.For<IRemoteRepository>();
        repository.CreateTreeAsync(Arg.Any<TreeSha?>(), Arg.Any<IReadOnlyList<TreeChange>>(), Arg.Any<CancellationToken>()).Returns(Result.Success(expectedTree));
        repository.CreateCommitAsync(Arg.Any<string>(), Arg.Any<TreeSha>(), Arg.Any<IReadOnlyList<CommitSha>>(), Arg.Any<CommitAuthor>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CommitSha.Parse("4444444444444444444444444444444444444444").Value));
        repository.UpdateBranchAsync(Arg.Any<CommitSha>(), Arg.Any<CommitSha?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Unit>(new Error(RemoteErrors.NonFastForward, "moved")));
        var failure = new Error("refresh.failed", "nope");

        var result = await Publisher(new InMemoryVaultFileSystem(), repository).PublishAsync(
            new PublishRequest(plan, remote, Manifest.Empty),
            Options(),
            _ => Task.FromResult(Result<PublishRequest>.Failure(failure)),
            Ct);

        result.Error.Should().Be(failure);
    }

    [Fact]
    public async Task Publish_WithA_MaxAttemptsOfOne_DoesNotRefresh()
    {
        var head = new RemoteHead(CommitSha.Parse("1111111111111111111111111111111111111111").Value, TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var remote = new RemoteSnapshot(head, Manifest.Empty, "\"etag\"", [], false);
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("a.md", Utf8("A")));
        var expectedTree = GitTreeHasher.Compute(target.Entries.ToDictionary(entry => entry.Path, entry => entry.Sha));
        var repository = Substitute.For<IRemoteRepository>();
        repository.CreateTreeAsync(Arg.Any<TreeSha?>(), Arg.Any<IReadOnlyList<TreeChange>>(), Arg.Any<CancellationToken>()).Returns(Result.Success(expectedTree));
        repository.CreateCommitAsync(Arg.Any<string>(), Arg.Any<TreeSha>(), Arg.Any<IReadOnlyList<CommitSha>>(), Arg.Any<CommitAuthor>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CommitSha.Parse("4444444444444444444444444444444444444444").Value));
        repository.UpdateBranchAsync(Arg.Any<CommitSha>(), Arg.Any<CommitSha?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Unit>(new Error(RemoteErrors.NonFastForward, "moved")));
        var refreshes = 0;

        var result = await Publisher(new InMemoryVaultFileSystem(), repository).PublishAsync(
            new PublishRequest(plan, remote, Manifest.Empty),
            Options(attempts: 1),
            _ => { refreshes++; return Task.FromResult(Result.Success(new PublishRequest(plan, remote, Manifest.Empty))); },
            Ct);

        result.Error.Code.Should().Be(RemoteErrors.NonFastForward);
        refreshes.Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenTheBlobShaDoesNotMatch_Fails()
    {
        var repository = await InitializedAsync();
        var remote = await SnapshotAsync(repository);
        var binary = new byte[] { 1, 2, 3 };
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")), ("image.png", binary));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")), ("image.png", binary));
        var writer = Substitute.For<IRemoteRepository>();
        writer.GetTreeAsync(Arg.Any<TreeSha>(), Arg.Any<CancellationToken>()).Returns(ci => repository.GetTreeAsync(ci.Arg<TreeSha>(), ci.Arg<CancellationToken>()));
        writer.GetBlobAsync(Arg.Any<BlobSha>(), Arg.Any<CancellationToken>()).Returns(ci => repository.GetBlobAsync(ci.Arg<BlobSha>(), ci.Arg<CancellationToken>()));
        writer.CreateBlobAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(BlobSha.Parse(new string('f', 40)).Value));

        var result = await Publisher(new InMemoryVaultFileSystem(), writer)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be(SyncErrors.ShaMismatch);
    }

    [Fact]
    public async Task Publish_WhenTheHeadCannotBeReadAfterInitialization_Fails()
    {
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")));
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RemoteHeadCheck>(new Error("remote.io", "down")));
        repository.CreateInitialCommitAsync(Arg.Any<VaultPath>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], false);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be("remote.io");
    }

    [Fact]
    public async Task Publish_WhenTheRepositoryStaysEmptyAfterInitialization_Fails()
    {
        var target = ManifestOf((".basalt/vault.json", Utf8("{}")));
        var plan = Plan(target, (".basalt/vault.json", Utf8("{}")));
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(null, null, false)));
        repository.CreateInitialCommitAsync(Arg.Any<VaultPath>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var remote = new RemoteSnapshot(null, Manifest.Empty, null, [], false);

        var result = await Publisher(new InMemoryVaultFileSystem(), repository)
            .PublishAsync(new PublishRequest(plan, remote, Manifest.Empty), Options(), null, Ct);

        result.Error.Code.Should().Be(RemoteErrors.RepositoryEmpty);
    }
}


