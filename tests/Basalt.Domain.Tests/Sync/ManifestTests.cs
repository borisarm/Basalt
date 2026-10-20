using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class ManifestTests
{
    private static readonly BlobSha ShaA = BlobSha.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391").Value;
    private static readonly BlobSha ShaB = BlobSha.Parse("ce013625030ba8dba906f756967f9e9ca394464a").Value;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static ManifestEntry Entry(string path, BlobSha? sha = null, long size = 0) => new(P(path), sha ?? ShaA, size);

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Empty_IsEmpty()
    {
        Manifest.Empty.IsEmpty.Should().BeTrue();
        Manifest.Empty.Count.Should().Be(0);
        Manifest.Empty.Entries.Should().BeEmpty();
        Manifest.Empty.Get(P("x.md")).Should().BeNull();
        Manifest.Empty.Should().Be(Manifest.Empty);
        Manifest.Empty.Equals(null).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Create_SortsEntriesByPath()
    {
        var manifest = Manifest.Create([Entry("z.md"), Entry("A.md"), Entry("carpeta/b.md")]).Value;

        manifest.Entries.Select(e => e.Path.ToString()).Should().Equal("A.md", "carpeta/b.md", "z.md");
        manifest.Count.Should().Be(3);
        manifest.Get(P("carpeta/b.md"))!.Value.Sha.Should().Be(ShaA);
        manifest.Contains(P("A.md")).Should().BeTrue();
        manifest.Contains(P("missing.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Create_RejectsCaseFoldCollisions()
    {
        var result = Manifest.Create([Entry("Nota.md"), Entry("nota.md")]);

        result.Error.Code.Should().Be(DomainErrors.PathCaseCollision);
        result.Error.Metadata!["path"].Should().Be("nota.md");
        result.Error.Metadata["other"].Should().Be("Nota.md");
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Create_RejectsDuplicatePath()
    {
        Manifest.Create([Entry("a.md"), Entry("a.md", ShaB)]).Error.Code.Should().Be(DomainErrors.PathCaseCollision);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Add_ReplacesTheSamePath_AndRejectsCaseFoldCollisions()
    {
        var manifest = Manifest.Create([Entry("a.md")]).Value;

        var updated = manifest.Add(Entry("a.md", ShaB, 5)).Value;
        updated.Get(P("a.md"))!.Value.Sha.Should().Be(ShaB);
        updated.Get(P("a.md"))!.Value.Size.Should().Be(5);
        manifest.Get(P("a.md"))!.Value.Sha.Should().Be(ShaA, "manifests are immutable");

        manifest.Add(Entry("A.md")).Error.Code.Should().Be(DomainErrors.PathCaseCollision);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Remove_IsIdempotent()
    {
        var manifest = Manifest.Create([Entry("a.md"), Entry("b.md")]).Value;

        var removed = manifest.Remove(P("a.md"));
        removed.Count.Should().Be(1);
        removed.Contains(P("a.md")).Should().BeFalse();
        removed.Remove(P("a.md")).Should().Be(removed);
        manifest.Count.Should().Be(2);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Create_ValidatesArguments()
    {
        FluentActions.Invoking(() => Manifest.Create(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Equality_IgnoresInputOrder()
    {
        var first = Manifest.Create([Entry("a.md"), Entry("b.md", ShaB, 3)]).Value;
        var second = Manifest.Create([Entry("b.md", ShaB, 3), Entry("a.md")]).Value;

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
        first.Should().NotBe(Manifest.Create([Entry("a.md"), Entry("b.md", ShaB, 4)]).Value);
        first.Should().NotBe(Manifest.Create([Entry("a.md")]).Value);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Entry_ValidatesSizeAndMode()
    {
        FluentActions.Invoking(() => new ManifestEntry(P("a.md"), ShaA, -1)).Should().Throw<ArgumentOutOfRangeException>();
        Entry("a.md").Mode.Should().Be(ManifestEntry.FileMode);
        ManifestEntry.FileMode.Should().Be("100644");
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void SyncBase_StartsEmpty()
    {
        SyncBase.Empty.LastSyncedCommit.Should().BeNull();
        SyncBase.Empty.LastRemoteTree.Should().BeNull();
        SyncBase.Empty.Manifest.Should().Be(Manifest.Empty);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void SyncBase_IsAValue()
    {
        var commit = CommitSha.Parse("8b5c2337fa39e475d69e8160144d8c05fb22cf4f").Value;
        var tree = TreeSha.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904").Value;
        var manifest = Manifest.Create([Entry("a.md")]).Value;

        var first = new SyncBase(commit, tree, manifest);
        var second = new SyncBase(commit, tree, Manifest.Create([Entry("a.md")]).Value);

        first.Should().Be(second);
        first.Should().NotBe(SyncBase.Empty);
        FluentActions.Invoking(() => new SyncBase(commit, tree, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Get_ReturnsTheEntryCopiedFromTheManifest()
    {
        var entry = Entry("café/nota.md", ShaB, 42);

        var manifest = Manifest.Create([entry]).Value;

        manifest.Get(P("café/nota.md")).Should().Be(entry);
    }
}
