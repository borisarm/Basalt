using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Graph;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

/// <summary>Opening, activating and restoring the graph tab (spec 07 §2, F-012).</summary>
public sealed class GraphTabOpeningTests : IAsyncDisposable
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private readonly FakeTimeProvider _time = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly ICommandHandler<RecordRecentFile, Unit> _recent = Substitute.For<ICommandHandler<RecordRecentFile, Unit>>();
    private readonly IQueryHandler<LoadWorkspace, WorkspaceState?> _load = Substitute.For<IQueryHandler<LoadWorkspace, WorkspaceState?>>();
    private readonly ICommandHandler<SaveWorkspace, Unit> _save = Substitute.For<ICommandHandler<SaveWorkspace, Unit>>();
    private readonly IQueryHandler<GetGraph, GraphModel> _graph = Substitute.For<IQueryHandler<GetGraph, GraphModel>>();
    private WorkspaceViewModel _workspace = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private ViewModelServices Services => new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time);

    private sealed class TestTab(ViewModelServices services) : TabViewModel(services)
    {
        public override string Kind => "note";

        protected override Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct)
        {
            Path = request.Path;
            Title = request.Path.NameWithoutExtension;
            return Task.FromResult(Result.Success());
        }
    }

    private WorkspaceViewModel Create()
    {
        var messenger = new WorkspaceMessenger();
        var services = Services;
        WorkspaceViewModel? workspace = null;
        workspace = new WorkspaceViewModel(
            services,
            new VaultSessionInfo(Vault, "Notas", "C:/v", VaultSettings.Default, null),
            [
                new DelegateTabFactory("note", _ => true, () => new TestTab(services)),
                new DelegateTabFactory(GraphTabViewModel.KindId, _ => false,
                    () => new GraphTabViewModel(services, Vault, _graph, workspace!, new FakeRandom(1), _events)),
            ],
            messenger,
            new WorkspaceUseCases(_recent, _load, _save),
            Substitute.For<IWindowService>());
        return workspace;
    }

    public GraphTabOpeningTests()
    {
        _recent.HandleAsync(Arg.Any<RecordRecentFile>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _save.HandleAsync(Arg.Any<SaveWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _load.HandleAsync(Arg.Any<LoadWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success<WorkspaceState?>(null));
        _graph.HandleAsync(Arg.Any<GetGraph>(), Arg.Any<CancellationToken>())
            .Returns(new GraphModel([new GraphNode("A.md", GraphNodeKind.Note, "A", P("A.md"))], []));
        _workspace = Create();
    }

    public async ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        await _events.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task OpenKind_OpensTheGraphTab_WithoutAFile()
    {
        var opened = await _workspace.Layout.ActiveGroup.OpenKindAsync(GraphTabViewModel.KindId, Ct);

        opened.IsSuccess.Should().BeTrue();
        var tab = _workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<GraphTabViewModel>().Subject;
        tab.Kind.Should().Be(GraphTabViewModel.KindId);
        tab.Path.Should().BeNull();
        _workspace.Layout.ActiveGroup.Tabs.Should().ContainSingle();
        await _recent.DidNotReceive().HandleAsync(Arg.Any<RecordRecentFile>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task OpenKind_ActivatesTheExistingGraphTab()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.Layout.ActiveGroup.OpenKindAsync(GraphTabViewModel.KindId, Ct);

        await _workspace.Layout.ActiveGroup.OpenKindAsync(GraphTabViewModel.KindId, Ct);

        _workspace.Layout.ActiveGroup.Tabs.OfType<GraphTabViewModel>().Should().ContainSingle();
        _workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<GraphTabViewModel>();
        await _graph.Received(1).HandleAsync(Arg.Any<GetGraph>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task OpenKind_WithAnUnknownKind_Fails()
    {
        var opened = await _workspace.Layout.ActiveGroup.OpenKindAsync("nope", Ct);

        opened.IsFailure.Should().BeTrue();
        opened.Error.Code.Should().Be(ErrorCodes.EntryUnsupported);
        _workspace.Layout.ActiveGroup.Tabs.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task OpenGraphCommand_OpensTheGraphTab()
    {
        await _workspace.OpenGraphCommand.ExecuteAsync(null);

        _workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<GraphTabViewModel>();
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task GraphTab_IsSavedAndRestored()
    {
        await _workspace.Layout.ActiveGroup.OpenKindAsync(GraphTabViewModel.KindId, Ct);
        WorkspaceState? saved = null;
        _save.HandleAsync(Arg.Any<SaveWorkspace>(), Arg.Any<CancellationToken>())
            .Returns(call => { saved = call.Arg<SaveWorkspace>().State; return Result.Success(); });
        await _workspace.CloseAsync(Ct);
        saved!.Panes[0].Tabs.Should().ContainSingle(t => t.Kind == GraphTabViewModel.KindId && t.Path == null);

        _load.HandleAsync(Arg.Any<LoadWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success<WorkspaceState?>(saved));
        using var restored = Create();
        await restored.InitializeAsync(Ct);

        restored.Layout.ActiveGroup.Tabs.Should().ContainSingle(t => t.Kind == GraphTabViewModel.KindId && t.Path == null);
    }
}
