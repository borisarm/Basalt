using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class WorkspaceViewModelTests : IAsyncLifetime
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private readonly FakeTimeProvider _time = new();
    private readonly FakeNotes _notes = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly ICommandHandler<RecordRecentFile, Unit> _recent = Substitute.For<ICommandHandler<RecordRecentFile, Unit>>();
    private readonly IQueryHandler<LoadWorkspace, WorkspaceState?> _load = Substitute.For<IQueryHandler<LoadWorkspace, WorkspaceState?>>();
    private readonly ICommandHandler<SaveWorkspace, Unit> _save = Substitute.For<ICommandHandler<SaveWorkspace, Unit>>();
    private WorkspaceViewModel _workspace = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private ViewModelServices Services => new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time);

    private sealed class NoteTabFactory(Func<NoteTabViewModel> create) : ITabFactory
    {
        public string Kind => "note";

        public bool CanOpen(VaultPath path) => FileClassifier.Classify(path) == FileKind.Note;

        public TabViewModel Create() => create();
    }

    public ValueTask InitializeAsync()
    {
        foreach (var name in new[] { "A.md", "B.md", "C.md" })
        {
            _notes.Write(P(name), name);
        }

        _recent.HandleAsync(Arg.Any<RecordRecentFile>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _save.HandleAsync(Arg.Any<SaveWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _load.HandleAsync(Arg.Any<LoadWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success<WorkspaceState?>(null));
        _workspace = Create();
        return ValueTask.CompletedTask;
    }

    private WorkspaceViewModel Create()
    {
        var messenger = new WorkspaceMessenger();
        var factory = new NoteTabFactory(() => new NoteTabViewModel(Services, new NoteTabUseCases(_notes, _notes), _events, messenger, Vault));
        return new WorkspaceViewModel(
            Services,
            new VaultSessionInfo(Vault, "Notas", "C:/v", VaultSettings.Default, null),
            [factory],
            messenger,
            new WorkspaceUseCases(_recent, _load, _save));
    }

    public async ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        await _events.DisposeAsync();
    }

    private List<string> Tabs(TabGroupViewModel? group = null) => [.. (group ?? _workspace.Layout.ActiveGroup).Tabs.Select(t => t.Title)];

    [Fact]
    public async Task Open_ReusesTheActiveTab_UnlessPinnedOrNewTab()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("B.md")), Ct);
        Tabs().Should().Equal("B");

        _workspace.Layout.ActiveGroup.ActiveTab!.IsPinned = true;
        await _workspace.OpenAsync(new OpenRequest(P("C.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("A.md"), NewTab: true), Ct);

        Tabs().Should().Equal("B", "C", "A");
        _workspace.Layout.ActiveGroup.ActiveTab!.Title.Should().Be("A");
        await _recent.Received(4).HandleAsync(Arg.Any<RecordRecentFile>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Open_ActivatesAnExistingTab()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("B.md"), NewTab: true), Ct);

        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);

        Tabs().Should().Equal("A", "B");
        _workspace.Layout.ActiveGroup.ActiveTab!.Title.Should().Be("A");
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task TabHistory_GoesBackAndForward()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("B.md")), Ct);
        var tab = _workspace.Layout.ActiveGroup.ActiveTab!;

        await tab.GoBackCommand.ExecuteAsync(null);
        tab.Title.Should().Be("A");
        tab.GoBackCommand.CanExecute(null).Should().BeFalse();

        await tab.GoForwardCommand.ExecuteAsync(null);
        tab.Title.Should().Be("B");
        tab.GoForwardCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task UnsupportedFiles_AndMissingNotes_AreErrors()
    {
        (await _workspace.OpenFileAsync(new OpenRequest(P("img.png")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryUnsupported);
        (await _workspace.OpenFileAsync(new OpenRequest(P("x.md"), NewTab: true), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);

        Tabs().Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task ClosingTabs_FlushesThem_AndTheLastClosedCanBeReopened()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("B.md"), NewTab: true), Ct);
        var group = _workspace.Layout.ActiveGroup;
        var editor = new FakeEditor();
        var b = (NoteTabViewModel)group.ActiveTab!;
        b.AttachEditor(editor);
        editor.Type(b, "B editado");

        await group.CloseTabCommand.ExecuteAsync(b);

        _notes.Read(P("B.md")).Should().Be("B editado");
        Tabs().Should().Equal("A");
        group.ActiveTab!.Title.Should().Be("A");

        await _workspace.ReopenClosedTabCommand.ExecuteAsync(null);
        Tabs().Should().Equal("A", "B");
        _workspace.ReopenClosedTabCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task Split_OpensTheActiveNoteInANewPane()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);

        await _workspace.Layout.SplitCommand.ExecuteAsync(SplitOrientation.Horizontal);

        _workspace.Layout.Groups.Should().HaveCount(2);
        _workspace.Layout.Orientation.Should().Be(SplitOrientation.Horizontal);
        Tabs(_workspace.Layout.Groups[1]).Should().Equal("A");
        _workspace.Layout.ActiveGroup.Should().BeSameAs(_workspace.Layout.Groups[1]);

        await _workspace.Layout.ActiveGroup.CloseTabCommand.ExecuteAsync(_workspace.Layout.ActiveGroup.ActiveTab);
        _workspace.Layout.Groups.Should().ContainSingle("an empty secondary pane closes");
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task State_IsSavedOnClose_AndRestoredOnOpen()
    {
        await _workspace.OpenAsync(new OpenRequest(P("A.md")), Ct);
        await _workspace.OpenAsync(new OpenRequest(P("B.md"), NewTab: true), Ct);
        _workspace.Layout.ActiveGroup.Tabs[0].IsPinned = true;
        await _workspace.Layout.SplitCommand.ExecuteAsync(SplitOrientation.Vertical);
        WorkspaceState? saved = null;
        await _save.HandleAsync(Arg.Do<SaveWorkspace>(s => saved = s.State), Arg.Any<CancellationToken>());

        await _workspace.CloseAsync(Ct);

        saved.Should().BeEquivalentTo(new WorkspaceState(
            [
                new WorkspacePane([new WorkspaceTab("note", "A.md", Pinned: true), new WorkspaceTab("note", "B.md")], ActiveTab: 1, Size: 0.5),
                new WorkspacePane([new WorkspaceTab("note", "B.md")], ActiveTab: 0, Size: 0.5),
            ],
            ActivePane: 1));

        _load.HandleAsync(Arg.Any<LoadWorkspace>(), Arg.Any<CancellationToken>()).Returns(Result.Success<WorkspaceState?>(saved! with
        {
            Panes = [.. saved!.Panes, new WorkspacePane([new WorkspaceTab("note", "borrada.md"), new WorkspaceTab("unknown", "x")])],
        }));
        using var restored = Create();
        await restored.InitializeAsync(Ct);

        restored.Layout.Groups.Select(g => g.Tabs.Select(t => t.Title)).Should().BeEquivalentTo<IEnumerable<string>>([["A", "B"], ["B"]], o => o.WithStrictOrdering());
        restored.Layout.Groups[0].Tabs[0].IsPinned.Should().BeTrue();
        restored.Layout.Groups[0].ActiveTab!.Title.Should().Be("B");
        restored.Layout.ActiveGroup.Should().BeSameAs(restored.Layout.Groups[1]);
    }

    [Fact]
    public void FocusPanel_IsExposed()
    {
        _workspace.FocusPanel("search");

        _workspace.FocusedPanel.Should().Be("search");
        _workspace.Title.Should().Be("Notas");
    }
}
