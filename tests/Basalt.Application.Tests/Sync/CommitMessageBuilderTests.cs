using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Tests.Sync;

public sealed class CommitMessageBuilderTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 25, 12, 30, 0, TimeSpan.Zero);

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static BlobSha S(char digit) => BlobSha.Parse(new string(digit, 40)).Value;

    private static Manifest M(params (string Path, char Sha)[] entries) =>
        Manifest.Create(entries.Select(entry => new ManifestEntry(P(entry.Path), S(entry.Sha), 10))).Value;

    private static MaterializedPlan Plan(Manifest target, params SyncConflict[] conflicts) =>
        new(target, new Dictionary<BlobSha, byte[]>(), conflicts);

    private static string Build(
        Manifest remote,
        Manifest target,
        IReadOnlyList<SyncConflict>? conflicts = null,
        CommitSettings? settings = null)
    {
        var list = conflicts ?? [];
        return CommitMessageBuilder.Build(
            settings ?? new CommitSettings(),
            "PC",
            "device-1",
            "12.0.0",
            "Notas",
            When,
            remote,
            Plan(target, [.. list]),
            list);
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public void Build_ListsChangesAndTrailers()
    {
        var remote = M(("a.md", 'a'), ("b.md", 'b'), ("c.md", 'c'));
        var target = M(("a.md", 'a'), ("b.md", 'd'), ("e (conflict).md", 'e'));
        var conflicts = new[] { new SyncConflict(P("e.md"), SyncConflictKind.ConflictCopy, P("e (conflict).md")) };

        var message = Build(remote, target, conflicts);

        message.Should().Be(
            "Sync from PC: 1 modificado, 1 eliminado, 1 conflicto\n\n"
            + "M b.md\nD c.md\nC e (conflict).md\n\n"
            + "Basalt-Device: PC\nBasalt-Device-Id: device-1\nBasalt-Version: 12.0.0");
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public void Build_UsesPluralFormsAndKeepsCopiesOutOfTheAddedCount()
    {
        var remote = M(("a.md", 'a'), ("b.md", 'b'), ("c.md", 'c'), ("d.md", 'd'));
        var target = M(("a.md", '1'), ("b.md", '2'), ("e (conflict).md", 'e'), ("f.md", 'f'));
        var conflicts = new[]
        {
            new SyncConflict(P("e.md"), SyncConflictKind.ConflictCopy, P("e (conflict).md")),
            new SyncConflict(P("f.md"), SyncConflictKind.Markers),
        };

        var message = Build(remote, target, conflicts);

        message.Should().Contain("2 modificados, 1 añadido, 2 eliminados, 2 conflictos");
        message.Should().Contain("M a.md\nM b.md\nD c.md\nD d.md\nC e (conflict).md\nA f.md\n");
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public void Build_WithNoChanges_OnlyWritesTheTrailers()
    {
        var manifest = M(("a.md", 'a'));

        var message = Build(manifest, manifest);

        message.Should().Be(
            "Sync from PC: sin cambios\n\n"
            + "Basalt-Device: PC\nBasalt-Device-Id: device-1\nBasalt-Version: 12.0.0");
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public void Build_ReplacesEveryTemplatePlaceholder()
    {
        var remote = M(("a.md", 'a'));
        var target = M(("a.md", 'a'), ("b.md", 'b'));

        var message = Build(remote, target, settings: new CommitSettings("{date} {vault} from {device}: {summary}"));

        message.Should().StartWith("2026-10-25 Notas from PC: 1 añadido\n\n");
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public void Build_WithExactlyTwentyChanges_DoesNotTruncate()
    {
        var target = M([.. Enumerable.Range(0, 20).Select(i => ($"n{i:D2}.md", (char)('a' + (i % 6))))]);

        var message = Build(Manifest.Empty, target);

        message.Should().Contain("A n19.md\n");
        message.Should().NotContain("más");
    }

    [Theory]
    [InlineData(25, 5)]
    [InlineData(21, 1)]
    [Trait("Spec", "F-011.1")]
    public void Build_TruncatesTheListingAfterTwentyPaths(int count, int hidden)
    {
        var target = M([.. Enumerable.Range(0, count).Select(i => ($"n{i:D2}.md", (char)('a' + (i % 6))))]);

        var message = Build(Manifest.Empty, target);

        message.Should().Contain("A n19.md\n");
        message.Should().NotContain("A n20.md");
        message.Should().Contain($"… y {hidden} más");
    }

    [Fact]
    public void Build_RejectsNullArguments()
    {
        var manifest = M(("a.md", 'a'));
        var plan = Plan(manifest);

        FluentActions.Invoking(() => CommitMessageBuilder.Build(null!, "PC", "id", "v", "n", When, manifest, plan, []))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => CommitMessageBuilder.Build(new CommitSettings(), "PC", "id", "v", "n", When, null!, plan, []))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => CommitMessageBuilder.Build(new CommitSettings(), "PC", "id", "v", "n", When, manifest, null!, []))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => CommitMessageBuilder.Build(new CommitSettings(), "PC", "id", "v", "n", When, manifest, plan, null!))
            .Should().Throw<ArgumentNullException>();
    }
}
