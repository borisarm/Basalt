using Basalt.Application.Abstractions;
using Basalt.Application.Graph;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Presentation.Common;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using NSubstitute;

namespace Basalt.Presentation.Tests.Panels;

public sealed class LinkPanelsTests : PanelTestBase
{
    private readonly IQueryHandler<GetBacklinks, Backlinks> _backlinks = Substitute.For<IQueryHandler<GetBacklinks, Backlinks>>();
    private readonly ICommandHandler<LinkUnlinkedMention, Unit> _link = Substitute.For<ICommandHandler<LinkUnlinkedMention, Unit>>();
    private readonly IQueryHandler<GetOutgoingLinks, IReadOnlyList<OutgoingLink>> _outgoing = Substitute.For<IQueryHandler<GetOutgoingLinks, IReadOnlyList<OutgoingLink>>>();

    private static Link L(string target) => new(LinkKind.WikiLink, target, null, SubpathKind.None, null, new SourceSpan(4, 5, 1, 4), new SourceSpan(6, 1, 1, 6));

    [Fact]
    [Trait("Spec", "F-005.1")]
    public async Task Backlinks_FollowTheActiveNote_AndRefreshOnMetadataChanges()
    {
        _backlinks.HandleAsync(Arg.Any<GetBacklinks>(), Arg.Any<CancellationToken>()).Returns(new Backlinks(
            [new BacklinkGroup(P("A.md"), [new LinkedMention(L("C"), new LinkContext(1, "ver [[C]] hoy", new TextRange(4, 5)))])],
            [new UnlinkedMentionGroup(P("B.md"), [new UnlinkedMention(new TextRange(9, 1), "C", new LinkContext(0, "hablé de c", new TextRange(9, 1)))])]));
        var vm = new BacklinksViewModel(Services, Context, _backlinks, _link);

        Activate("C.md");
        await vm.WhenRefreshedAsync();
        await Events.PublishAsync(new MetadataChanged(Vault, [P("D.md")]), Ct);
        await Events.WaitForIdleAsync(Ct);
        await vm.WhenRefreshedAsync();

        vm.Linked.Single().Should().BeEquivalentTo(new { Title = "A", Count = 1 });
        vm.Linked[0].Items.Single().Context.Should().Be("ver [[C]] hoy");
        vm.Unlinked.Single().Title.Should().Be("B");
        await _backlinks.Received(2).HandleAsync(new GetBacklinks(Vault, P("C.md")), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-005.2")]
    public async Task LinkingAnUnlinkedMention()
    {
        _backlinks.HandleAsync(Arg.Any<GetBacklinks>(), Arg.Any<CancellationToken>()).Returns(new Backlinks(
            [], [new UnlinkedMentionGroup(P("B.md"), [new UnlinkedMention(new TextRange(9, 1), "C", new LinkContext(0, "hablé de c", new TextRange(9, 1)))])]));
        _link.HandleAsync(Arg.Any<LinkUnlinkedMention>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new BacklinksViewModel(Services, Context, _backlinks, _link);
        Activate("C.md");
        await vm.WhenRefreshedAsync();

        await vm.LinkMentionCommand.ExecuteAsync(vm.Unlinked[0].Items[0]);

        await _link.Received(1).HandleAsync(new LinkUnlinkedMention(Vault, P("B.md"), new TextRange(9, 1), P("C.md")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoActiveNote_ClearsThePanels()
    {
        var vm = new BacklinksViewModel(Services, Context, _backlinks, _link);
        var outgoing = new OutgoingLinksViewModel(Services, Context, _outgoing);

        Activate(null);
        await vm.WhenRefreshedAsync();

        vm.Linked.Should().BeEmpty();
        outgoing.Links.Should().BeEmpty();
        await _backlinks.DidNotReceiveWithAnyArgs().HandleAsync(default!, Ct);
    }

    [Fact]
    public async Task Outgoing_ShowsResolution_AndOpensResolvedTargets()
    {
        _outgoing.HandleAsync(Arg.Any<GetOutgoingLinks>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<OutgoingLink>>(
        [
            new OutgoingLink(L("B"), new LinkResolution.Resolved(P("B.md"))),
            new OutgoingLink(L("Nada"), new LinkResolution.Unresolved("Nada")),
        ]));
        var vm = new OutgoingLinksViewModel(Services, Context, _outgoing);

        Activate("A.md");
        await vm.WhenRefreshedAsync();
        await vm.OpenCommand.ExecuteAsync(vm.Links[0]);
        await vm.OpenCommand.ExecuteAsync(vm.Links[1]);

        vm.Links.Select(l => (l.Target, l.IsResolved)).Should().Equal(("B", true), ("Nada", false));
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("B.md")), Arg.Any<CancellationToken>());
    }
}

public sealed class OutlinePropertiesGraphTests : PanelTestBase
{
    private readonly IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>> _outline = Substitute.For<IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>>>();
    private readonly IQueryHandler<ReadNote, NoteDocument> _read = Substitute.For<IQueryHandler<ReadNote, NoteDocument>>();
    private readonly ICommandHandler<SetFrontmatterProperty, Unit> _set = Substitute.For<ICommandHandler<SetFrontmatterProperty, Unit>>();
    private readonly ICommandHandler<RemoveFrontmatterProperty, Unit> _remove = Substitute.For<ICommandHandler<RemoveFrontmatterProperty, Unit>>();
    private readonly IQueryHandler<GetLocalGraph, GraphModel> _graph = Substitute.For<IQueryHandler<GetLocalGraph, GraphModel>>();

    private static Heading H(int level, string text, int line) => Heading.Create(level, text, line).Value;

    [Fact]
    public async Task Outline_TracksTheCaretSection_AndNavigates()
    {
        _outline.HandleAsync(Arg.Any<GetOutline>(), Arg.Any<CancellationToken>()).Returns(Result.Success(OutlineBuilder.Build([H(1, "A", 0), H(2, "A1", 4), H(1, "B", 10)])));
        var vm = new OutlineViewModel(Services, Context, _outline);
        Activate("n.md");
        await vm.WhenRefreshedAsync();

        Messenger.Send(new CaretLineChanged(P("n.md"), 6));
        vm.Current!.Text.Should().Be("A1");
        Messenger.Send(new CaretLineChanged(P("n.md"), 12));
        vm.Current!.Text.Should().Be("B");
        Messenger.Send(new CaretLineChanged(P("otra.md"), 0));
        vm.Current!.Text.Should().Be("B");

        await vm.OpenCommand.ExecuteAsync(vm.Roots[0].Children[0]);
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("n.md"), Line: 4), Arg.Any<CancellationToken>());
        vm.Roots.Select(r => r.Text).Should().Equal("A", "B");
    }

    [Fact]
    [Trait("Spec", "F-010.1")]
    public async Task Properties_ShowTypedValues_AndEditThem()
    {
        var frontmatter = new Frontmatter(StructuredValue.MapValue.Of(
            ("status", new StructuredValue.TextValue("draft")),
            ("rating", new StructuredValue.NumberValue("4")),
            ("done", new StructuredValue.BoolValue(true)),
            ("tags", new StructuredValue.ListValue([new StructuredValue.TextValue("a"), new StructuredValue.TextValue("b")]))), new SourceSpan(0, 10, 0, 0));
        _read.HandleAsync(Arg.Any<ReadNote>(), Arg.Any<CancellationToken>()).Returns(new NoteDocument(P("n.md"), NoteContent.FromText("x"), NoteMetadata.Empty with { Frontmatter = frontmatter }, GitBlobHasher.Compute([])));
        _set.HandleAsync(Arg.Any<SetFrontmatterProperty>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _remove.HandleAsync(Arg.Any<RemoveFrontmatterProperty>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new PropertiesViewModel(Services, Context, new PropertiesUseCases(_read, _set, _remove));
        Activate("n.md");
        await vm.WhenRefreshedAsync();

        vm.Rows.Select(r => (r.Key, r.Kind, r.Text)).Should().Equal(
            ("status", ValueKind.Text, "draft"), ("rating", ValueKind.Number, "4"), ("done", ValueKind.Bool, "true"), ("tags", ValueKind.List, "a, b"));

        await vm.CommitCommand.ExecuteAsync(vm.Rows[0] with { Text = "done" });
        await vm.CommitCommand.ExecuteAsync(vm.Rows[1] with { Text = "5" });
        await vm.CommitCommand.ExecuteAsync(vm.Rows[2] with { Text = "false" });
        await vm.CommitCommand.ExecuteAsync(vm.Rows[3] with { Text = "x, y" });
        await vm.RemoveCommand.ExecuteAsync(vm.Rows[0]);

        await _set.Received(1).HandleAsync(new SetFrontmatterProperty(Vault, P("n.md"), "status", new StructuredValue.TextValue("done")), Arg.Any<CancellationToken>());
        await _set.Received(1).HandleAsync(new SetFrontmatterProperty(Vault, P("n.md"), "rating", new StructuredValue.NumberValue("5")), Arg.Any<CancellationToken>());
        await _set.Received(1).HandleAsync(new SetFrontmatterProperty(Vault, P("n.md"), "done", new StructuredValue.BoolValue(false)), Arg.Any<CancellationToken>());
        await _set.Received(1).HandleAsync(Arg.Is<SetFrontmatterProperty>(s => s.Key == "tags" && ((StructuredValue.ListValue)s.Value).Items.Count == 2), Arg.Any<CancellationToken>());
        await _remove.Received(1).HandleAsync(new RemoveFrontmatterProperty(Vault, P("n.md"), "status"), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-010.2")]
    public async Task Properties_InvalidYaml_IsReported()
    {
        var invalid = new Frontmatter(StructuredValue.MapValue.Of(), new SourceSpan(0, 10, 0, 0), "bad yaml");
        _read.HandleAsync(Arg.Any<ReadNote>(), Arg.Any<CancellationToken>()).Returns(new NoteDocument(P("n.md"), NoteContent.FromText("x"), NoteMetadata.Empty with { Frontmatter = invalid }, GitBlobHasher.Compute([])));
        var vm = new PropertiesViewModel(Services, Context, new PropertiesUseCases(_read, _set, _remove));

        Activate("n.md");
        await vm.WhenRefreshedAsync();

        vm.Rows.Should().BeEmpty();
        vm.Warning.Should().Be("The properties are not valid YAML; edit them in source mode.");
    }

    [Fact]
    [Trait("Spec", "F-012.1")]
    public async Task LocalGraph_LoadsWithTheChosenDepth_AndLaysOut()
    {
        var model = new GraphModel(
            [new GraphNode("A.md", GraphNodeKind.Note, "A", P("A.md")), new GraphNode("B.md", GraphNodeKind.Note, "B", P("B.md"))],
            [new GraphEdge(0, 1, GraphEdgeKind.Link)]);
        _graph.HandleAsync(Arg.Any<GetLocalGraph>(), Arg.Any<CancellationToken>()).Returns(model);
        var vm = new LocalGraphViewModel(Services, Context, _graph, new FakeRandom());
        Activate("A.md");
        await vm.WhenRefreshedAsync();

        vm.Depth = 2;
        await vm.WhenRefreshedAsync();

        await _graph.Received(1).HandleAsync(Arg.Is<GetLocalGraph>(g => g.Center == P("A.md") && g.Depth == 1), Arg.Any<CancellationToken>());
        await _graph.Received(1).HandleAsync(Arg.Is<GetLocalGraph>(g => g.Depth == 2), Arg.Any<CancellationToken>());
        vm.Graph.Should().BeSameAs(model);
        vm.Layout!.Count.Should().Be(2);
        vm.Depth = 9;
        vm.Depth.Should().Be(GetGraphHandler.MaxDepth);

        await vm.OpenNodeCommand.ExecuteAsync(model.Nodes[1]);
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("B.md")), Arg.Any<CancellationToken>());
    }
}
