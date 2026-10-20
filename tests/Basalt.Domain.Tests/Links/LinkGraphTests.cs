using Basalt.Domain.Links;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Tests.Links;

public sealed class LinkGraphTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static ResolvedLink R(string source, string raw, string? target) =>
        new(P(source), WikiLinkSyntax.TryParse($"[[{raw}]]", 0, 0, 0)!, target is null ? null : P(target));

    [Fact]
    [Trait("Spec", "F-005.1")]
    public void IncomingAndOutgoing_AreTracked()
    {
        var graph = LinkGraph.Empty
            .WithSource(P("A.md"), [R("A.md", "C", "C.md")])
            .WithSource(P("B.md"), [R("B.md", "C", "C.md"), R("B.md", "Nada", null)]);

        graph.Incoming(P("C.md")).Select(l => l.Source.ToString()).Should().BeEquivalentTo(["A.md", "B.md"]);
        graph.Outgoing(P("B.md")).Should().HaveCount(2);
        graph.IncomingUnresolved("nada").Should().ContainSingle();
        graph.Unresolved.Keys.Should().Equal("NADA");
        graph.Sources.Should().HaveCount(2);
    }

    [Fact]
    public void WithSource_ReplacesPreviousLinks()
    {
        var graph = LinkGraph.Empty
            .WithSource(P("A.md"), [R("A.md", "C", "C.md"), R("A.md", "X", null)])
            .WithSource(P("A.md"), [R("A.md", "D", "D.md")]);

        graph.Incoming(P("C.md")).Should().BeEmpty();
        graph.Incoming(P("D.md")).Should().ContainSingle();
        graph.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void WithoutSource_AndEmptyLinks_RemoveTheSource()
    {
        var graph = LinkGraph.Empty.WithSource(P("A.md"), [R("A.md", "C", "C.md")]);

        graph.WithoutSource(P("A.md")).Incoming(P("C.md")).Should().BeEmpty();
        graph.WithSource(P("A.md"), []).Sources.Should().BeEmpty();
        graph.WithoutSource(P("missing.md")).Should().BeSameAs(graph);
    }

    [Fact]
    public void DuplicateLinksToTheSameTarget_AreKeptAndRemovedTogether()
    {
        var graph = LinkGraph.Empty.WithSource(P("A.md"), [R("A.md", "C", "C.md"), R("A.md", "C", "C.md")]);

        graph.Incoming(P("C.md")).Should().HaveCount(2);
        graph.WithoutSource(P("A.md")).Incoming(P("C.md")).Should().BeEmpty();
    }

    [Fact]
    public void RejectsNulls()
    {
        FluentActions.Invoking(() => LinkGraph.Empty.WithSource(P("a.md"), null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkGraph.Empty.IncomingUnresolved(null!)).Should().Throw<ArgumentNullException>();
    }
}
