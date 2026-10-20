using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class ChangeSetTests
{
    private static readonly BlobSha ShaA = BlobSha.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391").Value;
    private static readonly BlobSha ShaB = BlobSha.Parse("ce013625030ba8dba906f756967f9e9ca394464a").Value;
    private static readonly BlobSha ShaC = BlobSha.Parse("d670460b4b4aece5915caf5c68d12f560a9fe3e4").Value;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static Manifest M(params (string Path, BlobSha Sha)[] entries) =>
        Manifest.Create(entries.Select(e => new ManifestEntry(P(e.Path), e.Sha, 1))).Value;

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_DetectsAddedModifiedAndDeleted()
    {
        var before = M(("a.md", ShaA), ("b.md", ShaA), ("c.md", ShaA));
        var after = M(("a.md", ShaB), ("b.md", ShaA), ("d.md", ShaC));

        var change = ChangeSet.Between(before, after);

        change.Added.Select(e => e.Path.ToString()).Should().Equal("d.md");
        change.Modified.Select(e => e.Path.ToString()).Should().Equal("a.md");
        change.Modified.Single().Sha.Should().Be(ShaB);
        change.Deleted.Select(p => p.ToString()).Should().Equal("c.md");
        change.Renamed.Should().BeEmpty();
        change.IsEmpty.Should().BeFalse();
        change.Count.Should().Be(3);
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_ReportsRenamesBySha_WithoutRemovingTheAddAndDelete()
    {
        var before = M(("viejo.md", ShaB));
        var after = M(("nuevo.md", ShaB));

        var change = ChangeSet.Between(before, after);

        change.Renamed.Should().ContainSingle();
        change.Renamed[0].From.Should().Be(P("viejo.md"));
        change.Renamed[0].To.Should().Be(P("nuevo.md"));
        change.Renamed[0].Sha.Should().Be(ShaB);
        change.Added.Should().ContainSingle().Which.Path.Should().Be(P("nuevo.md"));
        change.Deleted.Should().ContainSingle().Which.Should().Be(P("viejo.md"));
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_PairsRepeatedShasDeterministically()
    {
        var before = M(("b.md", ShaB), ("a.md", ShaB));
        var after = M(("z.md", ShaB), ("y.md", ShaB));

        var change = ChangeSet.Between(before, after);

        change.Renamed.Select(r => (r.From.ToString(), r.To.ToString()))
            .Should().Equal(("a.md", "y.md"), ("b.md", "z.md"));
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_ModifiedWithTheSameShaIsNotAChange()
    {
        var before = M(("a.md", ShaA));
        var after = Manifest.Create([new ManifestEntry(P("a.md"), ShaA, 99)]).Value;

        ChangeSet.Between(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_EmptyManifests_IsEmpty()
    {
        var change = ChangeSet.Between(Manifest.Empty, Manifest.Empty);

        change.Added.Should().BeEmpty();
        change.Modified.Should().BeEmpty();
        change.Deleted.Should().BeEmpty();
        change.Renamed.Should().BeEmpty();
        change.Count.Should().Be(0);
        change.IsEmpty.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_OrdersResultsByPath()
    {
        var before = M(("z.md", ShaA), ("y.md", ShaA), ("x.md", ShaA));
        var after = M(("d.md", ShaC), ("c.md", ShaC), ("b.md", ShaC));

        var change = ChangeSet.Between(before, after);

        change.Added.Select(e => e.Path.ToString()).Should().Equal("b.md", "c.md", "d.md");
        change.Deleted.Select(p => p.ToString()).Should().Equal("x.md", "y.md", "z.md");
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Between_ValidatesArguments()
    {
        FluentActions.Invoking(() => ChangeSet.Between(null!, Manifest.Empty)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ChangeSet.Between(Manifest.Empty, null!)).Should().Throw<ArgumentNullException>();
    }
}
