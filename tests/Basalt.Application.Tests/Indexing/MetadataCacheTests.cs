using Basalt.Application.Indexing;
using Basalt.Application.Ports.Index;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;
using Basalt.Testing.Documents;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Application.Tests.Indexing;

public sealed class MetadataCacheTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static IndexedNoteSummary Note(string path, string text, string[]? aliases = null, string[]? tags = null) =>
        new(
            P(path),
            FileClassifier.Classify(P(path)),
            new IndexStamp(text.Length, DateTimeOffset.UnixEpoch, null),
            NoteMetadata.Empty with
            {
                Links = WikiLinkScanner.Scan(text).Where(l => !l.IsEmbed).ToList(),
                Embeds = WikiLinkScanner.Scan(text).Where(l => l.IsEmbed).ToList(),
                Aliases = aliases ?? [],
                Tags = (tags ?? []).Select(t => new TagOccurrence(Tag.Create(t).Value, TagSource.Text, 0)).ToList(),
            });

    [Fact]
    [Trait("Spec", "F-005.1")]
    public void Load_ResolvesLinksAndBuildsBacklinks()
    {
        var cache = new MetadataCache();

        cache.Load([Note("A.md", "[[C]]"), Note("B.md", "[[C]] [[Nada]]"), Note("C.md", "")]);

        var snapshot = cache.Current;
        snapshot.Graph.Incoming(P("C.md")).Select(l => l.Source.ToString()).Should().BeEquivalentTo(["A.md", "B.md"]);
        snapshot.Graph.IncomingUnresolved("Nada").Should().ContainSingle();
        snapshot.FindByName("c.md").Should().Equal(P("C.md"));
        snapshot.FindByPath(P("c.md")).Should().Equal(P("C.md"));
    }

    [Fact]
    public void AddingTheTargetOfAnUnresolvedLink_ResolvesIt()
    {
        var cache = new MetadataCache();
        cache.Load([Note("A.md", "[[Idea]]")]);

        var touched = cache.Apply([Note("Ideas/Idea.md", "")], []);

        cache.Current.Graph.Incoming(P("Ideas/Idea.md")).Should().ContainSingle();
        cache.Current.Graph.Unresolved.Should().BeEmpty();
        touched.Should().Contain([P("A.md"), P("Ideas/Idea.md")]);
    }

    [Fact]
    public void RemovingATarget_MakesLinksUnresolved()
    {
        var cache = new MetadataCache();
        cache.Load([Note("A.md", "[[B]]"), Note("B.md", "")]);

        cache.Apply([], [P("B.md")]);

        cache.Current.Graph.IncomingUnresolved("B").Should().ContainSingle();
        cache.Current.Files.Should().ContainSingle();
    }

    [Fact]
    public void Rename_MovesBacklinksWhenTheLinkStillResolves()
    {
        var cache = new MetadataCache();
        cache.Load([Note("A.md", "[[B]]"), Note("B.md", "")]);

        var touched = cache.Rename(P("B.md"), P("Dir/B.md"));
        cache.Rename(P("missing.md"), P("x.md")).Should().BeEmpty();

        cache.Current.Graph.Incoming(P("Dir/B.md")).Should().ContainSingle();
        touched.Should().Contain([P("B.md"), P("Dir/B.md")]);
    }

    [Fact]
    public void AliasesAndTags_AreIndexed()
    {
        var cache = new MetadataCache();
        cache.Load([Note("A.md", "", ["Proyecto Uno"], ["trabajo", "Trabajo/cliente"])]);

        cache.Current.ByAlias["PROYECTO UNO"].Should().Equal(P("A.md"));
        cache.Current.ByTag["TRABAJO"].Should().Equal(P("A.md"));
        cache.Current.ByTag.Keys.Should().Contain("TRABAJO/CLIENTE");

        cache.Apply([Note("A.md", "")], []);

        cache.Current.ByAlias.Should().BeEmpty();
        cache.Current.ByTag.Should().BeEmpty();
    }

    [Fact]
    public void Snapshots_AreImmutable()
    {
        var cache = new MetadataCache();
        cache.Load([Note("A.md", "")]);
        var before = cache.Current;

        cache.Apply([Note("B.md", "")], []);

        before.Files.Should().ContainSingle();
        cache.Current.Files.Should().HaveCount(2);
    }

    [Fact]
    public void RejectsNulls()
    {
        var cache = new MetadataCache();

        FluentActions.Invoking(() => MetadataCache.Build(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => cache.Apply(null!, [])).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => cache.Apply([], null!)).Should().Throw<ArgumentNullException>();
    }

    // Property (spec 09 §6): an incrementally maintained cache equals one rebuilt from scratch.
    private static readonly string[] Paths = ["A.md", "B.md", "x/Nota.md", "y/Nota.md", "Dir/C.md", "Nota.md", "img.png", "Dir/x/A.md"];
    private static readonly string[] Targets = ["A", "B", "Nota", "x/Nota", "C", "img.png", "Missing", "./C", "../A", "nota"];

    private sealed record Op(string Kind, string Path, string Other, string Text);

    private static Gen<Op> OpGen() =>
        from kind in Gen.Elements("upsert", "upsert", "remove", "rename")
        from path in Gen.Elements(Paths)
        from other in Gen.Elements(Paths)
        from links in Gen.ListOf(Gen.Elements(Targets), 3)
        select new Op(kind, path, other, string.Concat(links.Select(l => $"[[{l}]] ")));

    [Fact]
    [Trait("Spec", "IDX-3")]
    public void Property_IncrementalEqualsRebuilt()
    {
        Prop.ForAll(Gen.ListOf(OpGen(), 15).ToArbitrary(), ops =>
        {
            var cache = new MetadataCache();
            var files = new Dictionary<string, IndexedNoteSummary>(StringComparer.Ordinal);
            foreach (var op in ops)
            {
                switch (op.Kind)
                {
                    case "upsert":
                        files[op.Path] = Note(op.Path, op.Text);
                        cache.Apply([files[op.Path]], []);
                        break;
                    case "remove":
                        files.Remove(op.Path);
                        cache.Apply([], [P(op.Path)]);
                        break;
                    default:
                        if (files.Remove(op.Path, out var moved) && !files.ContainsKey(op.Other))
                        {
                            files[op.Other] = moved with { Path = P(op.Other), Kind = FileClassifier.Classify(P(op.Other)) };
                            cache.Rename(P(op.Path), P(op.Other));
                        }
                        else if (moved is not null)
                        {
                            files[op.Path] = moved;
                        }

                        break;
                }
            }

            var rebuilt = MetadataCache.Build(files.Values);
            return Describe(cache.Current) == Describe(rebuilt);
        }).QuickCheckThrowOnFailure();
    }

    private static string Describe(MetadataSnapshot snapshot) =>
        string.Join("\n", snapshot.Files.Keys.Order().Select(source =>
            $"{source} -> {string.Join(",", snapshot.Graph.Outgoing(source).Select(l => l.Target?.ToString() ?? "?" + l.Link.RawTarget))}"))
        + "\nunresolved:" + string.Join(",", snapshot.Graph.Unresolved.Keys.Order(StringComparer.Ordinal).Select(k => $"{k}={snapshot.Graph.Unresolved[k].Length}"));
}
