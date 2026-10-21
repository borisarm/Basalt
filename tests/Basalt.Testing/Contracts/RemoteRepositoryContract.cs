using System.Text;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IRemoteRepository"/> (spec 03 §1.4, 06 §9) shared by fakes and the GitHub adapter.</summary>
public abstract class RemoteRepositoryContract : ContractTest<IRemoteRepository>
{
    private static readonly CommitAuthor Author = new("Basalt Test", "test@basalt.local");

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task EmptyRepository_HasNoHead()
    {
        var head = await Sut.GetHeadAsync(null, Ct);

        head.IsSuccess.Should().BeTrue();
        head.Value.Head.Should().BeNull();
        head.Value.ETag.Should().BeNull();
        head.Value.NotModified.Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task Commit_RoundTripsBlobsTreeAndHead()
    {
        var (blobs, tree, commit) = await CommitAsync(Sut, new Dictionary<string, string>
        {
            ["a.md"] = "# A\n",
            ["dir/b.md"] = "B\n",
        }, "Initial commit");

        (await Sut.UpdateBranchAsync(commit, null, Ct)).IsSuccess.Should().BeTrue();

        var check = (await Sut.GetHeadAsync(null, Ct)).Value;
        check.Head!.Commit.Should().Be(commit);
        check.Head.Tree.Should().Be(tree);
        check.ETag.Should().NotBeNullOrEmpty();

        var treeSnapshot = (await Sut.GetTreeAsync(tree, Ct)).Value;
        treeSnapshot.Files.Should().HaveCount(2);
        treeSnapshot.Files.Single(file => file.Path == P("a.md")).Sha.Should().Be(blobs["a.md"]);
        treeSnapshot.Files.Single(file => file.Path == P("a.md")).Size.Should().Be(4);
        treeSnapshot.Quarantined.Should().BeEmpty();
        Encoding.UTF8.GetString((await Sut.GetBlobAsync(blobs["a.md"], Ct)).Value).Should().Be("# A\n");
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task ConditionalHead_ReturnsNotModifiedUntilTheBranchMoves()
    {
        var (_, tree, first) = await CommitAsync(Sut, new Dictionary<string, string> { ["a.md"] = "A" }, "First");
        (await Sut.UpdateBranchAsync(first, null, Ct)).IsSuccess.Should().BeTrue();
        var etag = (await Sut.GetHeadAsync(null, Ct)).Value.ETag!;

        var unchanged = (await Sut.GetHeadAsync(etag, Ct)).Value;

        unchanged.NotModified.Should().BeTrue();
        unchanged.Head.Should().BeNull();
        unchanged.ETag.Should().Be(etag);

        var second = (await Sut.CreateCommitAsync("Second", tree, [first], Author, Ct)).Value;
        (await Sut.UpdateBranchAsync(second, first, Ct)).IsSuccess.Should().BeTrue();
        var moved = (await Sut.GetHeadAsync(etag, Ct)).Value;

        moved.NotModified.Should().BeFalse();
        moved.Head!.Commit.Should().Be(second);
        moved.ETag.Should().NotBe(etag);
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task Archive_ContainsEveryFileOfTheCommit()
    {
        var (_, tree, commit) = await CommitAsync(Sut, new Dictionary<string, string>
        {
            ["a.md"] = "A\n",
            ["dir/b.txt"] = "B\n",
        }, "Initial");

        var archive = await Sut.GetArchiveAsync(commit, Ct);

        archive.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(archive.Value[P("a.md")]).Should().Be("A\n");
        Encoding.UTF8.GetString(archive.Value[P("dir/b.txt")]).Should().Be("B\n");
        var missing = await Sut.GetArchiveAsync(ParseCommit("0000000000000000000000000000000000000000"), Ct);
        missing.Error.Code.Should().Be(RemoteErrors.NotFound);
        (await Sut.GetTreeAsync(tree, Ct)).Value.Files.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task CreateTree_AcceptsInlineTextWithoutAPriorBlob()
    {
        var content = "# inline\n";
        var sha = GitBlobHasher.Compute(Encoding.UTF8.GetBytes(content));

        var tree = (await Sut.CreateTreeAsync(null, [new TreeChange.Upsert(P("a.md"), sha, content)], Ct)).Value;
        var snapshot = (await Sut.GetTreeAsync(tree, Ct)).Value;

        snapshot.Files.Single().Sha.Should().Be(sha);
        Encoding.UTF8.GetString((await Sut.GetBlobAsync(sha, Ct)).Value).Should().Be(content);
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task EmptyRepository_CanBeInitializedThroughTheContentsApi()
    {
        var content = Encoding.UTF8.GetBytes("{}");

        var created = await Sut.CreateInitialCommitAsync(P(".basalt/vault.json"), content, "init", Ct);

        created.IsSuccess.Should().BeTrue(created.IsSuccess ? string.Empty : created.Error.Code + ": " + created.Error.Message);
        var head = (await Sut.GetHeadAsync(null, Ct)).Value.Head;
        head.Should().NotBeNull();
        var snapshot = (await Sut.GetTreeAsync(head!.Tree, Ct)).Value;
        snapshot.Files.Single(file => file.Path == P(".basalt/vault.json")).Sha
            .Should().Be(GitBlobHasher.Compute(content));
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task CreateTree_AppliesUpsertsAndDeletesOnBaseTree()
    {
        var (_, baseTree, commit) = await CommitAsync(Sut, new Dictionary<string, string>
        {
            ["a.md"] = "A",
            ["b.md"] = "B",
        }, "Initial");
        await Sut.UpdateBranchAsync(commit, null, Ct);

        var c = (await Sut.CreateBlobAsync(Encoding.UTF8.GetBytes("C"), Ct)).Value;
        var tree = (await Sut.CreateTreeAsync(
            baseTree,
            [new TreeChange.Upsert(P("dir/c.md"), c), new TreeChange.Delete(P("a.md"))],
            Ct)).Value;

        var treeSnapshot = (await Sut.GetTreeAsync(tree, Ct)).Value;
        treeSnapshot.Files.Select(file => file.Path.ToString()).Should().BeEquivalentTo("b.md", "dir/c.md");
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task CreateTree_NullBase_CreatesAnEmptyTree()
    {
        var tree = (await Sut.CreateTreeAsync(null, [], Ct)).Value;

        (await Sut.GetTreeAsync(tree, Ct)).Value.Files.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task UpdateBranch_RejectsNonFastForwardAndStaleExpectedHead()
    {
        var (_, tree, first) = await CommitAsync(Sut, new Dictionary<string, string> { ["a.md"] = "A" }, "First");
        await Sut.UpdateBranchAsync(first, null, Ct);

        var second = (await Sut.CreateCommitAsync("Second", tree, [first], Author, Ct)).Value;
        var orphan = (await Sut.CreateCommitAsync("Orphan", tree, [], Author, Ct)).Value;

        (await Sut.UpdateBranchAsync(orphan, first, Ct)).Error.Code.Should().Be(RemoteErrors.NonFastForward);
        (await Sut.UpdateBranchAsync(first, second, Ct)).Error.Code.Should().Be(RemoteErrors.NonFastForward);
        (await Sut.GetHeadAsync(null, Ct)).Value.Head!.Commit.Should().Be(first);

        (await Sut.UpdateBranchAsync(second, first, Ct)).IsSuccess.Should().BeTrue();
        (await Sut.GetHeadAsync(null, Ct)).Value.Head!.Commit.Should().Be(second);
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task History_And_FileAtCommit_FollowTheFirstParentChain()
    {
        var (_, tree1, first) = await CommitAsync(Sut, new Dictionary<string, string> { ["a.md"] = "one" }, "First");
        await Sut.UpdateBranchAsync(first, null, Ct);
        var tree2 = (await Sut.CreateTreeAsync(tree1, [new TreeChange.Upsert(P("a.md"), (await Sut.CreateBlobAsync(Encoding.UTF8.GetBytes("two"), Ct)).Value)], Ct)).Value;
        var second = (await Sut.CreateCommitAsync("Second", tree2, [first], Author, Ct)).Value;
        await Sut.UpdateBranchAsync(second, first, Ct);

        var history = (await Sut.GetFileHistoryAsync(P("a.md"), 10, Ct)).Value;
        history.Select(entry => entry.Sha).Should().Equal(second, first);
        history[1].Message.Should().Be("First");
        Encoding.UTF8.GetString((await Sut.GetFileAtCommitAsync(P("a.md"), first, Ct)).Value!).Should().Be("one");
        (await Sut.GetFileAtCommitAsync(P("a.md"), second, Ct)).Value.Should().Equal(Encoding.UTF8.GetBytes("two"));
        (await Sut.GetFileAtCommitAsync(P("missing.md"), second, Ct)).Value.Should().BeNull();
        (await Sut.GetFileHistoryAsync(P("a.md"), 1, Ct)).Value.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task MissingObjects_ReturnRemoteNotFound()
    {
        var missingBlob = await Sut.GetBlobAsync(ParseBlob("0000000000000000000000000000000000000000"), Ct);
        var missingTree = await Sut.GetTreeAsync(ParseTree("0000000000000000000000000000000000000000"), Ct);
        var missingCommit = await Sut.GetFileAtCommitAsync(P("a.md"), ParseCommit("0000000000000000000000000000000000000000"), Ct);

        missingBlob.Error.Code.Should().Be(RemoteErrors.NotFound);
        missingTree.Error.Code.Should().Be(RemoteErrors.NotFound);
        missingCommit.Error.Code.Should().Be(RemoteErrors.NotFound);
    }

    [Fact]
    [Trait("Spec", "REMOTE-PORT")]
    public async Task Commit_ValidatesItsInputs()
    {
        var tree = (await Sut.CreateTreeAsync(null, [], Ct)).Value;

        (await Sut.CreateCommitAsync("bad\rmessage", tree, [], Author, Ct)).Error.Code
            .Should().Be(RemoteErrors.ValidationFailed);
        (await Sut.CreateCommitAsync("ok", tree, [], new CommitAuthor("", "a@b"), Ct)).Error.Code
            .Should().Be(RemoteErrors.ValidationFailed);
    }

    private static async Task<(Dictionary<string, BlobSha> Blobs, TreeSha Tree, CommitSha Commit)> CommitAsync(
        IRemoteRepository repository,
        IReadOnlyDictionary<string, string> files,
        string message)
    {
        var blobs = new Dictionary<string, BlobSha>(StringComparer.Ordinal);
        var changes = new List<TreeChange>();
        foreach (var (path, content) in files)
        {
            var blob = (await repository.CreateBlobAsync(Encoding.UTF8.GetBytes(content), Ct)).Value;
            blobs[path] = blob;
            changes.Add(new TreeChange.Upsert(P(path), blob));
        }

        var tree = (await repository.CreateTreeAsync(null, changes, Ct)).Value;
        var commit = (await repository.CreateCommitAsync(message, tree, [], Author, Ct)).Value;
        return (blobs, tree, commit);
    }

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static BlobSha ParseBlob(string sha) => BlobSha.Parse(sha).Value;

    private static TreeSha ParseTree(string sha) => TreeSha.Parse(sha).Value;

    private static CommitSha ParseCommit(string sha) => CommitSha.Parse(sha).Value;
}
