using Basalt.Application.Abstractions;
using Basalt.Application.Events;
using Basalt.Application.Graph;
using Basalt.Application.Notes;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class GraphTabViewModelTests : IAsyncDisposable
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private readonly FakeTimeProvider _time = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly IQueryHandler<GetGraph, GraphModel> _graph = Substitute.For<IQueryHandler<GetGraph, GraphModel>>();
    private readonly IWorkspaceNavigator _navigator = Substitute.For<IWorkspaceNavigator>();
    private readonly GraphModel _model = Model();
    private readonly GraphTabViewModel _tab;

    public GraphTabViewModelTests()
    {
        _graph.HandleAsync(Arg.Any<GetGraph>(), Arg.Any<CancellationToken>()).Returns(_model);
        _tab = new GraphTabViewModel(Services, Vault, _graph, _navigator, new FakeRandom(1), _events);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static GraphModel Model() => new(
        [new GraphNode("A.md", GraphNodeKind.Note, "A", P("A.md")), new GraphNode("B.md", GraphNodeKind.Note, "B", P("B.md"))],
        [new GraphEdge(0, 1, GraphEdgeKind.Link)]);

    private ViewModelServices Services => new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time);

    public async ValueTask DisposeAsync()
    {
        _tab.Dispose();
        await _events.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task Show_LoadsTheWholeGraph_AndLaysItOut()
    {
        var shown = await _tab.ShowAsync(Ct);

        shown.IsSuccess.Should().BeTrue();
        _tab.Kind.Should().Be("graph");
        _tab.Kind.Should().Be(GraphTabViewModel.KindId);
        _tab.Path.Should().BeNull("the graph tab does not stand for a file");
        _tab.Title.Should().Be("Graph");
        _tab.Graph.Should().BeSameAs(_model);
        _tab.Layout!.Count.Should().Be(2);
        await _graph.Received(1).HandleAsync(Arg.Is<GetGraph>(g => g.VaultId == Vault && g.Options == GraphOptions.Default), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task Filters_ChangeTheQuery()
    {
        await _tab.ShowAsync(Ct);

        _tab.ShowAttachments = true;
        await _tab.WhenRefreshedAsync();
        _tab.Filter = "  #proyecto  ";
        await _tab.WhenRefreshedAsync();
        _tab.ShowTags = true;
        await _tab.WhenRefreshedAsync();

        await _graph.Received(1).HandleAsync(Arg.Is<GetGraph>(g => g.Options == new GraphOptions { ShowAttachments = true }), Arg.Any<CancellationToken>());
        await _graph.Received(1).HandleAsync(Arg.Is<GetGraph>(g => g.Options == new GraphOptions { ShowAttachments = true, Filter = "#proyecto" }), Arg.Any<CancellationToken>());
        await _graph.Received(1).HandleAsync(Arg.Is<GetGraph>(g => g.Options == new GraphOptions { ShowAttachments = true, ShowTags = true, Filter = "#proyecto" }), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task ColorGroups_AreOneSearchQueryPerLine()
    {
        await _tab.ShowAsync(Ct);

        _tab.ColorGroupsText = "tag:#a\n\n  tag:#b  \n";
        await _tab.WhenRefreshedAsync();

        _tab.Options.ColorGroups.Should().Equal("tag:#a", "tag:#b");
        await _graph.Received(1).HandleAsync(Arg.Is<GetGraph>(g => g.Options.ColorGroups != null && g.Options.ColorGroups.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task Forces_RebuildTheLayout_WithinTheirRanges()
    {
        await _tab.ShowAsync(Ct);
        var initial = _tab.Layout;

        _tab.Repulsion = 900;
        _tab.Layout.Should().NotBeSameAs(initial);
        _tab.Layout!.Count.Should().Be(2);

        _tab.Repulsion = -100;
        _tab.Repulsion.Should().Be(GraphTabViewModel.MinRepulsion);
        _tab.LinkDistance = 9000;
        _tab.LinkDistance.Should().Be(GraphTabViewModel.MaxLinkDistance);
        _tab.LinkStrength = 5;
        _tab.LinkStrength.Should().Be(GraphTabViewModel.MaxLinkStrength);
        _tab.Gravity = -1;
        _tab.Gravity.Should().Be(GraphTabViewModel.MinGravity);
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task OpenNode_OpensTheNote_AndCtrlOpensANewTab()
    {
        await _tab.ShowAsync(Ct);

        await _tab.OpenNodeCommand.ExecuteAsync(_model.Nodes[1]);
        await _navigator.Received(1).OpenAsync(new OpenRequest(P("B.md")), Arg.Any<CancellationToken>());

        await _tab.OpenNodeNewTabCommand.ExecuteAsync(_model.Nodes[0]);
        await _navigator.Received(1).OpenAsync(new OpenRequest(P("A.md"), NewTab: true), Arg.Any<CancellationToken>());

        await _tab.OpenNodeCommand.ExecuteAsync(new GraphNode("tag:x", GraphNodeKind.Tag, "x", null));
        await _navigator.Received(2).OpenAsync(Arg.Any<OpenRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task MetadataChanges_RefreshTheGraph()
    {
        await _tab.ShowAsync(Ct);

        await _events.PublishAsync(new MetadataChanged(Vault, [P("A.md")]), Ct);
        await _events.WaitForIdleAsync(Ct);
        await _tab.WhenRefreshedAsync();

        await _graph.Received(2).HandleAsync(Arg.Any<GetGraph>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task InvalidFilters_LeaveTheGraphAndNotify()
    {
        await _tab.ShowAsync(Ct);
        _graph.HandleAsync(Arg.Any<GetGraph>(), Arg.Any<CancellationToken>())
            .Returns(new Error(DomainErrors.SearchInvalidRegex, "Bad query."));

        _tab.Filter = "*";
        await _tab.WhenRefreshedAsync();

        _tab.Graph.Should().BeSameAs(_model);
        _notifications.Shown.Should().ContainSingle();
    }
}
