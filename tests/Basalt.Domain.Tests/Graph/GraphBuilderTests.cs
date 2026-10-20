using Basalt.Domain.Graph;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Tests.Graph;

public sealed class GraphBuilderTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static GraphFile File(string path, params string[] tags) =>
        new(P(path), FileClassifier.Classify(P(path)), [.. tags.Select(t => Tag.Create(t).Value)]);

    private static ResolvedLink Link(string source, string raw, string? target, LinkKind kind = LinkKind.WikiLink) =>
        new(P(source), new Link(kind, raw, null, SubpathKind.None, null, new SourceSpan(0, 1, 0, 0), new SourceSpan(0, 1, 0, 0)), target is null ? null : P(target));

    private static LinkGraph Links(params ResolvedLink[] links) =>
        links.GroupBy(l => l.Source).Aggregate(LinkGraph.Empty, (graph, group) => graph.WithSource(group.Key, group));

    private static List<string> Ids(GraphModel graph) => [.. graph.Nodes.Select(n => n.Id)];

    private static List<string> Edges(GraphModel graph) =>
        [.. graph.Edges.Select(e => $"{graph.Nodes[e.Source].Id}->{graph.Nodes[e.Target].Id}:{e.Kind}")];

    private static readonly GraphFile[] Vault =
    [
        File("A.md", "trabajo"),
        File("B.md", "trabajo/cliente"),
        File("C.md"),
        File("Huérfana.md"),
        File("img.png"),
        File("doc.pdf"),
    ];

    private static readonly LinkGraph VaultLinks = Links(
        Link("A.md", "B", "B.md"),
        Link("A.md", "B", "B.md"),
        Link("A.md", "Inexistente", null),
        Link("A.md", "https://x.org", null, LinkKind.MarkdownLink),
        Link("A.md", "A", "A.md"),
        Link("B.md", "C", "C.md"),
        Link("B.md", "img.png", "img.png", LinkKind.Embed),
        Link("C.md", "inexistente", null));

    [Fact]
    [Trait("Spec", "F-012.2")]
    public void Global_ShowsNotesUnresolvedAndDeduplicatedEdges()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, GraphFilter.Default);

        Ids(graph).Should().Equal("A.md", "B.md", "C.md", "Huérfana.md", "unresolved:INEXISTENTE");
        graph.Nodes[4].Should().BeEquivalentTo(new { Kind = GraphNodeKind.Unresolved, Label = "Inexistente", Path = (VaultPath?)null }, o => o.ComparingByValue<VaultPath>());
        Edges(graph).Should().Equal("A.md->B.md:Link", "A.md->unresolved:INEXISTENTE:Link", "B.md->C.md:Link", "C.md->unresolved:INEXISTENTE:Link");
    }

    [Fact]
    [Trait("Spec", "F-012.2")]
    public void ExistingOnly_HidesUnresolved()
    {
        Ids(GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(ShowUnresolved: false))).Should().NotContain(id => id.StartsWith("unresolved:", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachments_AndEmbeds()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(ShowAttachments: true));

        Ids(graph).Should().Contain(["img.png", "doc.pdf"]);
        graph.Nodes[graph.IndexOf("img.png")].Kind.Should().Be(GraphNodeKind.Attachment);
        Edges(graph).Should().Contain("B.md->img.png:Embed");
    }

    [Fact]
    public void Tags_AddTagNodesAndEdges()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(ShowTags: true, ShowUnresolved: false));

        Ids(graph).Should().Contain(["tag:TRABAJO", "tag:TRABAJO/CLIENTE"]);
        graph.Nodes[graph.IndexOf("tag:TRABAJO")].Should().BeEquivalentTo(new { Kind = GraphNodeKind.Tag, Label = "#trabajo" });
        Edges(graph).Should().Contain(["A.md->tag:TRABAJO:Tag", "B.md->tag:TRABAJO/CLIENTE:Tag"]);
    }

    [Fact]
    public void Orphans_CanBeHidden()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(ShowOrphans: false, ShowAttachments: true));

        Ids(graph).Should().NotContain(["Huérfana.md", "doc.pdf"]).And.Contain(["A.md", "img.png"]);
    }

    [Fact]
    public void Include_RestrictsFiles_AndKeepsOnlyTheirUnresolvedTargets()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(Include: new HashSet<VaultPath> { P("B.md"), P("C.md") }));

        Ids(graph).Should().Equal("B.md", "C.md", "unresolved:INEXISTENTE");
        Edges(graph).Should().Equal("B.md->C.md:Link", "C.md->unresolved:INEXISTENTE:Link");
    }

    [Fact]
    public void Groups_AssignTheFirstMatchingGroup()
    {
        var graph = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(Groups: [new HashSet<VaultPath> { P("A.md") }, new HashSet<VaultPath> { P("A.md"), P("C.md") }]));

        graph.Nodes.Select(n => (n.Id, n.Group)).Should().Contain([("A.md", 0), ("B.md", -1), ("C.md", 1)]);
    }

    [Fact]
    [Trait("Spec", "F-012.1")]
    public void Local_DepthOne_ShowsDirectNeighboursOnly()
    {
        var files = new[] { File("A.md"), File("B.md"), File("C.md") };
        var links = Links(Link("A.md", "B", "B.md"), Link("B.md", "C", "C.md"));

        Ids(GraphBuilder.BuildLocal(files, links, P("A.md"), 1, LocalGraphDirection.Both, GraphFilter.Default)).Should().Equal("A.md", "B.md");
        Ids(GraphBuilder.BuildLocal(files, links, P("A.md"), 2, LocalGraphDirection.Both, GraphFilter.Default)).Should().Equal("A.md", "B.md", "C.md");
        Ids(GraphBuilder.BuildLocal(files, links, P("C.md"), 5, LocalGraphDirection.Outgoing, GraphFilter.Default)).Should().Equal("C.md");
        Ids(GraphBuilder.BuildLocal(files, links, P("C.md"), 5, LocalGraphDirection.Incoming, GraphFilter.Default)).Should().Equal("A.md", "B.md", "C.md");
        Edges(GraphBuilder.BuildLocal(files, links, P("A.md"), 1, LocalGraphDirection.Both, GraphFilter.Default)).Should().Equal("A.md->B.md:Link");
    }

    [Fact]
    public void Local_KeepsTheCenterEvenWhenOrphansAreHidden_AndUnknownCentersAreEmpty()
    {
        Ids(GraphBuilder.BuildLocal(Vault, VaultLinks, P("Huérfana.md"), 2, LocalGraphDirection.Both, new GraphFilter(ShowOrphans: false))).Should().Equal("Huérfana.md");
        GraphBuilder.BuildLocal(Vault, VaultLinks, P("Nada.md"), 2, LocalGraphDirection.Both, GraphFilter.Default).Nodes.Should().BeEmpty();
        GraphBuilder.BuildLocal(Vault, VaultLinks, P("A.md"), 0, LocalGraphDirection.Both, GraphFilter.Default).Nodes.Should().ContainSingle();
    }

    [Fact]
    public void IsDeterministic_RegardlessOfInputOrder()
    {
        var forward = GraphBuilder.Build(Vault, VaultLinks, new GraphFilter(ShowAttachments: true, ShowTags: true));
        var backward = GraphBuilder.Build(Vault.Reverse(), VaultLinks, new GraphFilter(ShowAttachments: true, ShowTags: true));

        Ids(backward).Should().Equal(Ids(forward));
        Edges(backward).Should().Equal(Edges(forward));
        GraphModel.Empty.IndexOf("x").Should().Be(-1);
    }
}
