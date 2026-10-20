using System.Text;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Fakes;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class RemoteSnapshotProviderTests
{
    private static readonly CommitAuthor Author = new("Basalt Test", "test@basalt.local");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static InMemoryRemoteRepository Repository() =>
        new(new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"));

    private static async Task<(CommitSha Commit, TreeSha Tree)> PublishAsync(
        InMemoryRemoteRepository repository,
        string path,
        string content,
        RemoteHead? parent = null)
    {
        var blob = (await repository.CreateBlobAsync(Encoding.UTF8.GetBytes(content), Ct)).Value;
        var tree = (await repository.CreateTreeAsync(parent?.Tree, [new TreeChange.Upsert(P(path), blob)], Ct)).Value;
        var commit = (await repository.CreateCommitAsync(
            "add " + path,
            tree,
            parent is { } previous ? [previous.Commit] : [],
            Author,
            Ct)).Value;
        (await repository.UpdateBranchAsync(commit, parent?.Commit, Ct)).IsSuccess.Should().BeTrue();
        return (commit, tree);
    }

    [Fact]
    public async Task Get_OfAnEmptyRepository_ReturnsAnEmptyManifest()
    {
        var provider = new RemoteSnapshotProvider(Repository());

        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Head.Should().BeNull();
        snapshot.Manifest.Should().Be(Manifest.Empty);
        snapshot.ETag.Should().BeNull();
        snapshot.Quarantined.Should().BeEmpty();
        snapshot.NotModified.Should().BeFalse();
    }

    [Fact]
    public async Task Get_ReturnsTheRemoteManifestAndItsBlobSizes()
    {
        var repository = Repository();
        await PublishAsync(repository, "A.md", "A");
        await PublishAsync(repository, "dir/B.md", "BB", (await repository.GetHeadAsync(null, Ct)).Value.Head);
        var provider = new RemoteSnapshotProvider(repository);

        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Head.Should().NotBeNull();
        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("A.md", "dir/B.md");
        snapshot.Manifest.Get(P("A.md"))!.Value.Size.Should().Be(1);
        snapshot.Manifest.Get(P("dir/B.md"))!.Value.Size.Should().Be(2);
        snapshot.ETag.Should().NotBeNull();
        snapshot.NotModified.Should().BeFalse();
    }

    [Fact]
    public async Task Get_WhenTheETagIsUnchanged_ReturnsNotModifiedWithoutReadingTheTree()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        var tree = new RemoteTreeSnapshot([new RemoteTreeFile(P("A.md"), BlobSha.Parse("3333333333333333333333333333333333333333").Value, 1)], []);
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(head, "\"etag\"", NotModified: false)));
        repository.GetHeadAsync("\"etag\"", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(null, "\"etag\"", NotModified: true)));
        repository.GetTreeAsync(head.Tree, Arg.Any<CancellationToken>())
            .Returns(Result.Success(tree));
        var provider = new RemoteSnapshotProvider(repository);

        var first = (await provider.GetAsync(Ct)).Value;
        var second = (await provider.GetAsync(Ct)).Value;

        first.NotModified.Should().BeFalse();
        second.NotModified.Should().BeTrue();
        second.Manifest.Should().Be(first.Manifest);
        second.Head.Should().Be(head);
        await repository.Received(1).GetTreeAsync(head.Tree, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WithANewCommitOnTheSameTree_ReusesTheManifest()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var treeSha = TreeSha.Parse("2222222222222222222222222222222222222222").Value;
        var firstHead = new RemoteHead(CommitSha.Parse("1111111111111111111111111111111111111111").Value, treeSha);
        var secondHead = firstHead with { Commit = CommitSha.Parse("4444444444444444444444444444444444444444").Value };
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(firstHead, "\"one\"", NotModified: false)));
        repository.GetHeadAsync("\"one\"", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(secondHead, "\"two\"", NotModified: false)));
        repository.GetTreeAsync(treeSha, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteTreeSnapshot([], [])));
        var provider = new RemoteSnapshotProvider(repository);

        await provider.GetAsync(Ct);
        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Head.Should().Be(secondHead);
        snapshot.ETag.Should().Be("\"two\"");
        snapshot.NotModified.Should().BeFalse();
        await repository.Received(1).GetTreeAsync(treeSha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_QuarantinesCaseCollisionsKeepingTheFirstOrdinalPath()
    {
        var repository = Repository();
        var head = await PublishAsync(repository, "Nota.md", "upper");
        var blob = (await repository.CreateBlobAsync(Encoding.UTF8.GetBytes("lower"), Ct)).Value;
        var tree = (await repository.CreateTreeAsync(head.Tree, [new TreeChange.Upsert(P("nota.md"), blob)], Ct)).Value;
        var commit = (await repository.CreateCommitAsync("both", tree, [head.Commit], Author, Ct)).Value;
        (await repository.UpdateBranchAsync(commit, head.Commit, Ct)).IsSuccess.Should().BeTrue();
        var provider = new RemoteSnapshotProvider(repository);

        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("Nota.md");
        snapshot.Quarantined.Should().ContainSingle(issue =>
            issue.Path == "nota.md" && issue.Reason == RemoteQuarantineReasons.CaseCollision);
    }

    [Fact]
    public async Task Get_KeepsTheInvalidPathsReportedByTheRepository()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(head, "\"etag\"", NotModified: false)));
        repository.GetTreeAsync(head.Tree, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteTreeSnapshot([], [new RemotePathIssue("aux.md", RemoteQuarantineReasons.InvalidPath)])));
        var provider = new RemoteSnapshotProvider(repository);

        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Manifest.IsEmpty.Should().BeTrue();
        snapshot.Quarantined.Should().ContainSingle(issue =>
            issue.Path == "aux.md" && issue.Reason == RemoteQuarantineReasons.InvalidPath);
    }

    [Fact]
    public async Task Get_PropagatesHeadAndTreeErrors()
    {
        var headError = new Error(RemoteErrors.Unauthorized, "no token");
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RemoteHeadCheck>(headError));

        var failedHead = await new RemoteSnapshotProvider(repository).GetAsync(Ct);

        failedHead.Error.Code.Should().Be(RemoteErrors.Unauthorized);

        var treeError = new Error(RemoteErrors.NotFound, "no tree");
        var head = new RemoteHead(
            CommitSha.Parse("1111111111111111111111111111111111111111").Value,
            TreeSha.Parse("2222222222222222222222222222222222222222").Value);
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(head, "\"etag\"", NotModified: false)));
        repository.GetTreeAsync(head.Tree, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RemoteTreeSnapshot>(treeError));

        var failedTree = await new RemoteSnapshotProvider(repository).GetAsync(Ct);

        failedTree.Error.Code.Should().Be(RemoteErrors.NotFound);
    }

    [Fact]
    public async Task TreeCache_KeepsTheThreeMostRecentlyUsedTrees()
    {
        var repository = Repository();
        var provider = new RemoteSnapshotProvider(repository);
        var trees = new List<TreeSha>();
        RemoteHead? head = null;
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            var published = await PublishAsync(repository, name + ".md", name, head);
            head = new RemoteHead(published.Commit, published.Tree);
            trees.Add(published.Tree);
            (await provider.GetAsync(Ct)).Value.Head!.Commit.Should().Be(published.Commit);
        }

        provider.CachedTrees.Select(entry => entry.Tree).Should().Equal(trees[^3], trees[^2], trees[^1]);
        provider.CachedTrees.Select(entry => entry.Tree).Should().NotContain(trees[0]);
    }

    [Fact]
    public async Task SeededTrees_AvoidReadingThemAgain()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var treeSha = TreeSha.Parse("2222222222222222222222222222222222222222").Value;
        var head = new RemoteHead(CommitSha.Parse("1111111111111111111111111111111111111111").Value, treeSha);
        var snapshot = new RemoteTreeSnapshot(
            [new RemoteTreeFile(P("A.md"), BlobSha.Parse("3333333333333333333333333333333333333333").Value, 1)],
            []);
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteHeadCheck(head, "\"etag\"", NotModified: false)));
        var provider = new RemoteSnapshotProvider(repository, [new RemoteTreeCacheEntry(treeSha, snapshot)]);

        var result = (await provider.GetAsync(Ct)).Value;

        result.Manifest.Count.Should().Be(1);
        await repository.DidNotReceive().GetTreeAsync(Arg.Any<TreeSha>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WhenNotModifiedArrivesWithoutACachedHead_FallsBackToAnUnconditionalRequest()
    {
        var repository = Substitute.For<IRemoteRepository>();
        var treeSha = TreeSha.Parse("2222222222222222222222222222222222222222").Value;
        var head = new RemoteHead(CommitSha.Parse("1111111111111111111111111111111111111111").Value, treeSha);
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(new RemoteHeadCheck(null, "\"etag\"", NotModified: true)),
                Result.Success(new RemoteHeadCheck(head, "\"etag\"", NotModified: false)));
        repository.GetTreeAsync(treeSha, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RemoteTreeSnapshot([], [])));
        var provider = new RemoteSnapshotProvider(repository);

        var snapshot = (await provider.GetAsync(Ct)).Value;

        snapshot.Head.Should().Be(head);
        snapshot.NotModified.Should().BeFalse();
    }
}
