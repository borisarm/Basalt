using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using Basalt.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Workspace;

/// <summary>Creates tabs of one kind (spec 07 §2: Open/Closed for new tab types).</summary>
public interface ITabFactory
{
    string Kind { get; }

    bool CanOpen(VaultPath path);

    TabViewModel Create();
}

/// <summary>A group of tabs in one pane.</summary>
public sealed partial class TabGroupViewModel(ViewModelServices services, IReadOnlyList<ITabFactory> factories) : ViewModelBase(services)
{
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TabViewModel? ActiveTab { get; set; }

    /// <summary>Share of the split.</summary>
    [ObservableProperty]
    public partial double Size { get; set; } = 1;

    /// <summary>Raised after a tab is closed, with what it was showing.</summary>
    public event EventHandler<OpenRequest>? TabClosed;

    /// <summary>
    /// Shows the file: activates a tab already showing it; otherwise navigates the active tab (unless pinned, of
    /// another kind or <see cref="OpenRequest.NewTab"/>) or opens a new tab next to it.
    /// </summary>
    /// <returns><c>entry.unsupported</c> when no tab type can show the file, or the tab's load error.</returns>
    public async Task<Result<Unit>> OpenAsync(OpenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.NewTab && Tabs.FirstOrDefault(t => t.Path == request.Path) is { } existing)
        {
            ActiveTab = existing;
            return Result.Success();
        }

        if (factories.FirstOrDefault(f => f.CanOpen(request.Path)) is not { } factory)
        {
            return new Error(ErrorCodes.EntryUnsupported, "No tab can show this file.").With("path", request.Path.ToString());
        }

        if (!request.NewTab && ActiveTab is { IsPinned: false } active && active.Kind == factory.Kind)
        {
            return await active.NavigateAsync(request, ct).ConfigureAwait(true);
        }

        var tab = factory.Create();
        var shown = await tab.ShowAsync(request, ct).ConfigureAwait(true);
        if (shown.IsFailure)
        {
            tab.Dispose();
            return shown;
        }

        Add(tab);
        return Result.Success();
    }

    /// <summary>
    /// Shows a tab that does not stand for a file (<c>graph</c>): activates the one already open of that kind, or
    /// opens it next to the active tab. <c>entry.unsupported</c> when no tab type has that kind.
    /// </summary>
    public async Task<Result<Unit>> OpenKindAsync(string kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (Tabs.FirstOrDefault(t => t.Kind == kind) is { } existing)
        {
            ActiveTab = existing;
            return Result.Success();
        }

        if (factories.FirstOrDefault(f => f.Kind == kind) is not { } factory)
        {
            return new Error(ErrorCodes.EntryUnsupported, "No tab of this kind.").With("kind", kind);
        }

        var tab = factory.Create();
        var shown = await tab.ShowAsync(ct).ConfigureAwait(true);
        if (shown.IsFailure)
        {
            tab.Dispose();
            return shown;
        }

        Add(tab);
        return Result.Success();
    }

    /// <summary>
    /// Opens a tab of <paramref name="kind"/> showing <paramref name="request"/> (for files whose default tab is another kind,
    /// such as a note's version history). <c>entry.unsupported</c> when no tab type has that kind.
    /// </summary>
    public async Task<Result<Unit>> OpenKindForPathAsync(string kind, OpenRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(request);
        if (factories.FirstOrDefault(f => f.Kind == kind) is not { } factory)
        {
            return new Error(ErrorCodes.EntryUnsupported, "No tab of this kind.").With("kind", kind);
        }

        var tab = factory.Create();
        var shown = await tab.ShowAsync(request, ct).ConfigureAwait(true);
        if (shown.IsFailure)
        {
            tab.Dispose();
            return shown;
        }

        Add(tab);
        return Result.Success();
    }

    private void Add(TabViewModel tab)
    {
        Tabs.Insert(ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1, tab);
        ActiveTab = tab;
    }

    /// <summary>Adds a restored tab (workspace persistence).</summary>
    public async Task<bool> RestoreAsync(WorkspaceTab saved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Path is null)
        {
            return await RestoreKindAsync(saved, ct).ConfigureAwait(true);
        }

        if (VaultPath.Create(saved.Path) is not { IsSuccess: true } path
            || factories.FirstOrDefault(f => f.Kind == saved.Kind && f.CanOpen(path.Value)) is not { } factory)
        {
            return false;
        }

        var tab = factory.Create();
        if ((await tab.ShowAsync(new OpenRequest(path.Value), ct).ConfigureAwait(true)).IsFailure)
        {
            tab.Dispose();
            return false;
        }

        tab.IsPinned = saved.Pinned;
        tab.RestoreViewState(saved.ViewState);
        Tabs.Add(tab);
        return true;
    }

    private async Task<bool> RestoreKindAsync(WorkspaceTab saved, CancellationToken ct)
    {
        if (factories.FirstOrDefault(f => f.Kind == saved.Kind) is not { } factory)
        {
            return false;
        }

        var tab = factory.Create();
        if ((await tab.ShowAsync(ct).ConfigureAwait(true)).IsFailure)
        {
            tab.Dispose();
            return false;
        }

        tab.IsPinned = saved.Pinned;
        tab.RestoreViewState(saved.ViewState);
        Tabs.Add(tab);
        return true;
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        foreach (var tab in Tabs.ToList())
        {
            await tab.FlushAsync(ct).ConfigureAwait(true);
        }
    }

    public WorkspacePane Capture() => new(
        [.. Tabs.Select(t => new WorkspaceTab(t.Kind, t.Path?.ToString(), t.IsPinned, t.ViewState))],
        ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab),
        Size);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var tab in Tabs)
            {
                tab.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task CloseTabAsync(TabViewModel? tab, CancellationToken ct)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        await tab.FlushAsync(ct).ConfigureAwait(true);
        var index = Tabs.IndexOf(tab);
        Tabs.RemoveAt(index);
        var current = tab.Current;
        tab.Dispose();

        // The layout hears first: an emptied secondary pane goes away before it could report "no active file".
        if (current is not null)
        {
            TabClosed?.Invoke(this, current);
        }

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Max(0, index - 1)];
        }
    }

    [RelayCommand]
    private static void TogglePin(TabViewModel? tab)
    {
        if (tab is not null)
        {
            tab.IsPinned = !tab.IsPinned;
        }
    }
}

/// <summary>The central area: one level of split panes (F-013), each a <see cref="TabGroupViewModel"/>.</summary>
public sealed partial class PaneLayoutViewModel : ViewModelBase
{
    private readonly Func<TabGroupViewModel> _groupFactory;

    public PaneLayoutViewModel(ViewModelServices services, Func<TabGroupViewModel> groupFactory)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(groupFactory);
        _groupFactory = groupFactory;
        Groups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Columns));
            OnPropertyChanged(nameof(Rows));
        };
        ActiveGroup = AddGroup();
    }

    public ObservableCollection<TabGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial TabGroupViewModel ActiveGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns), nameof(Rows))]
    public partial SplitOrientation Orientation { get; set; }

    /// <summary>Columns of the split grid (panes side by side).</summary>
    public int Columns => Orientation == SplitOrientation.Vertical ? Math.Max(1, Groups.Count) : 1;

    /// <summary>Rows of the split grid (panes stacked).</summary>
    public int Rows => Orientation == SplitOrientation.Horizontal ? Math.Max(1, Groups.Count) : 1;

    /// <summary>Raised when any group closes a tab.</summary>
    public event EventHandler<OpenRequest>? TabClosed;

    public TabGroupViewModel AddGroup()
    {
        var group = _groupFactory();
        group.TabClosed += OnTabClosed;
        Groups.Add(group);
        foreach (var each in Groups)
        {
            each.Size = 1.0 / Groups.Count;
        }

        return group;
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        foreach (var group in Groups.ToList())
        {
            await group.FlushAllAsync(ct).ConfigureAwait(true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var group in Groups)
            {
                group.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>Splits the area: a new pane showing the active file.</summary>
    [RelayCommand]
    private async Task SplitAsync(SplitOrientation orientation, CancellationToken ct)
    {
        var current = ActiveGroup.ActiveTab?.Current;
        Orientation = orientation;
        var group = AddGroup();
        if (current is not null)
        {
            await group.OpenAsync(current with { NewTab = true }, ct).ConfigureAwait(true);
        }

        ActiveGroup = group;
    }

    private void OnTabClosed(object? sender, OpenRequest closed)
    {
        // An emptied secondary pane closes.
        if (sender is TabGroupViewModel group && group.Tabs.Count == 0 && Groups.Count > 1)
        {
            var index = Groups.IndexOf(group);
            Groups.Remove(group);
            group.TabClosed -= OnTabClosed;
            group.Dispose();
            foreach (var each in Groups)
            {
                each.Size = 1.0 / Groups.Count;
            }

            if (ActiveGroup == group)
            {
                ActiveGroup = Groups[Math.Max(0, index - 1)];
            }
        }

        TabClosed?.Invoke(this, closed);
    }
}

/// <summary>The use cases behind the workspace.</summary>
public sealed record WorkspaceUseCases(
    ICommandHandler<RecordRecentFile, Unit> RecordRecent,
    IQueryHandler<LoadWorkspace, WorkspaceState?> Load,
    ICommandHandler<SaveWorkspace, Unit> Save);

/// <summary>
/// One open vault (spec 07 §2): its panes and tabs, navigation (<see cref="IWorkspaceNavigator"/>), recent files,
/// reopening closed tabs and the per-device layout, restored on open and saved on close (F-013).
/// </summary>
public sealed partial class WorkspaceViewModel : ViewModelBase, IWorkspaceNavigator, IClosable, IWorkspaceCommandHost
{
    private readonly VaultSessionInfo _vault;
    private readonly WorkspaceUseCases _useCases;
    private readonly IWindowService _windows;
    private readonly Stack<OpenRequest> _closed = new();
    private WorkspacePanels? _panels;
    private TabGroupViewModel? _watchedGroup;
    private TabViewModel? _watchedTab;
    private VaultPath? _announced;

    public WorkspaceViewModel(
        ViewModelServices services,
        VaultSessionInfo vault,
        IReadOnlyList<ITabFactory> tabFactories,
        IWorkspaceMessenger messenger,
        WorkspaceUseCases useCases,
        IWindowService windows)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _useCases = useCases;
        _windows = windows;
        Messenger = messenger;
        Layout = new PaneLayoutViewModel(services, () => new TabGroupViewModel(services, tabFactories));
        Layout.TabClosed += (_, closed) =>
        {
            _closed.Push(closed);
            ReopenClosedTabCommand.NotifyCanExecuteChanged();
        };
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneLayoutViewModel.ActiveGroup))
            {
                Watch(Layout.ActiveGroup);
            }
        };
        Watch(Layout.ActiveGroup);
        messenger.Register<NavigateRequested>(this, (_, m) => _ = OpenFileAsync(m.Request, CancellationToken.None));
        messenger.Register<ExternalConflictDiffRequested>(this, (_, m) => _ = _windows.ShowDiffAsync(
            new Diff.DiffRequest(
                Localizer.Get("Conflict.DiffTitle"),
                Localizer.Get("Conflict.Mine"),
                Localizer.Get("Conflict.Disk"),
                m.Mine,
                m.Disk),
            CancellationToken.None));
        LeftSidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.ActivePanel))
            {
                OnPropertyChanged(nameof(LeftContent));
            }
        };
        RightSidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.ActivePanel))
            {
                OnPropertyChanged(nameof(RightContent));
            }
        };
    }

    public string Title => _vault.Name;

    public Domain.Vaults.VaultId VaultId => _vault.Id;

    /// <summary>The file of the active tab.</summary>
    public VaultPath? ActiveFile => _announced;

    /// <summary>Sync state of this vault shown in the status bar (F-015.4).</summary>
    public Sync.SyncStatusViewModel? Sync { get; set; }

    /// <summary>The side panels and overlays of this workspace (set by the composition root, disposed with it).</summary>
    public WorkspacePanels? Panels
    {
        get => _panels;
        set
        {
            _panels = value;
            OnPropertyChanged(nameof(LeftContent));
            OnPropertyChanged(nameof(RightContent));
        }
    }

    /// <summary>The ViewModel of the left sidebar's active panel.</summary>
    public object? LeftContent => PanelFor(LeftSidebar.ActivePanel);

    /// <summary>The ViewModel of the right sidebar's active panel.</summary>
    public object? RightContent => PanelFor(RightSidebar.ActivePanel);

    public object? PanelFor(string panelId) => _panels is not { } panels ? null : panelId switch
    {
        Basalt.Presentation.Panels.PanelIds.Files => panels.Explorer,
        Basalt.Presentation.Panels.PanelIds.Search => panels.Search,
        Basalt.Presentation.Panels.PanelIds.Bookmarks => panels.Bookmarks,
        Basalt.Presentation.Panels.PanelIds.Tags => panels.Tags,
        Basalt.Presentation.Panels.PanelIds.Backlinks => panels.Backlinks,
        Basalt.Presentation.Panels.PanelIds.Outgoing => panels.Outgoing,
        Basalt.Presentation.Panels.PanelIds.Outline => panels.Outline,
        Basalt.Presentation.Panels.PanelIds.LocalGraph => panels.LocalGraph,
        Basalt.Presentation.Panels.PanelIds.Properties => panels.Properties,
        _ => null,
    };

    public IWorkspaceMessenger Messenger { get; }

    public PaneLayoutViewModel Layout { get; }

    [ObservableProperty]
    public partial string? FocusedPanel { get; private set; }

    /// <summary>Explorer, search, bookmarks and tags (F-013).</summary>
    public SidebarViewModel LeftSidebar { get; } = new(Basalt.Presentation.Panels.PanelIds.Files, [Basalt.Presentation.Panels.PanelIds.Files, Basalt.Presentation.Panels.PanelIds.Search, Basalt.Presentation.Panels.PanelIds.Bookmarks, Basalt.Presentation.Panels.PanelIds.Tags]);

    /// <summary>Backlinks, outgoing links, outline, local graph and properties (F-013).</summary>
    public SidebarViewModel RightSidebar { get; } = new(Basalt.Presentation.Panels.PanelIds.Backlinks, [Basalt.Presentation.Panels.PanelIds.Backlinks, Basalt.Presentation.Panels.PanelIds.Outgoing, Basalt.Presentation.Panels.PanelIds.Outline, Basalt.Presentation.Panels.PanelIds.LocalGraph, Basalt.Presentation.Panels.PanelIds.Properties]);

    /// <summary>Restores the layout saved on this device.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var state = await RunAsync(token => _useCases.Load.HandleAsync(new LoadWorkspace(_vault.Id), token), notifyErrors: false).ConfigureAwait(true);
        if (state.IsFailure || state.Value is not { } saved)
        {
            return;
        }

        LeftSidebar.Restore(saved.Left);
        RightSidebar.Restore(saved.Right);
        if (saved.Panes.Count == 0)
        {
            return;
        }

        Layout.Orientation = saved.Orientation;
        var groups = new List<TabGroupViewModel>();
        foreach (var pane in saved.Panes)
        {
            var group = groups.Count == 0 ? Layout.Groups[0] : Layout.AddGroup();
            foreach (var tab in pane.Tabs)
            {
                await group.RestoreAsync(tab, ct).ConfigureAwait(true);
            }

            group.ActiveTab = group.Tabs.Count == 0 ? null : group.Tabs[Math.Clamp(pane.ActiveTab, 0, group.Tabs.Count - 1)];
            groups.Add(group);
        }

        foreach (var empty in groups.Skip(1).Where(g => g.Tabs.Count == 0).ToList())
        {
            Layout.Groups.Remove(empty);
            empty.Dispose();
            groups.Remove(empty);
        }

        foreach (var group in Layout.Groups)
        {
            group.Size = 1.0 / Layout.Groups.Count;
        }

        Layout.ActiveGroup = Layout.Groups[Math.Clamp(saved.ActivePane, 0, Layout.Groups.Count - 1)];
    }

    public async Task OpenAsync(OpenRequest request, CancellationToken ct) => await OpenFileAsync(request, ct).ConfigureAwait(true);

    /// <summary>Opens a file in the active pane and records it as recent.</summary>
    public async Task<Result<Unit>> OpenFileAsync(OpenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opened = await RunAsync(token => Layout.ActiveGroup.OpenAsync(request, token)).ConfigureAwait(true);
        if (opened.IsSuccess)
        {
            await _useCases.RecordRecent.HandleAsync(new RecordRecentFile(_vault.Id, request.Path), ct).ConfigureAwait(true);
        }

        return opened;
    }

    /// <summary>Shows a panel in the sidebar that has it (opening the sidebar).</summary>
    public void FocusPanel(string panelId)
    {
        FocusedPanel = panelId;
        foreach (var sidebar in new[] { LeftSidebar, RightSidebar }.Where(s => s.Panels.Contains(panelId)))
        {
            sidebar.Show(panelId);
        }
    }

    /// <summary><see cref="Application.Commands.ICommandHost"/>: commands open files like any navigation.</summary>
    Task Application.Commands.ICommandHost.OpenAsync(VaultPath path, bool newTab, CancellationToken ct) => OpenFileAsync(new OpenRequest(path, newTab), ct);

    public WorkspaceState CaptureState() => new(
        [.. Layout.Groups.Select(g => g.Capture())],
        Layout.Groups.IndexOf(Layout.ActiveGroup),
        Layout.Orientation,
        LeftSidebar.Capture(),
        RightSidebar.Capture());

    /// <summary>Saves every tab and the layout (window close, vault close).</summary>
    public async Task CloseAsync(CancellationToken ct)
    {
        await Layout.FlushAllAsync(ct).ConfigureAwait(true);
        await RunAsync(token => _useCases.Save.HandleAsync(new SaveWorkspace(_vault.Id, CaptureState()), token)).ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Layout.Dispose();
            Panels?.Dispose();
            Sync?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Follows the active group and tab so that panels hear about the active file (<see cref="ActiveFileChanged"/>).</summary>
    private void Watch(TabGroupViewModel group)
    {
        if (_watchedGroup is not null)
        {
            _watchedGroup.PropertyChanged -= OnGroupChanged;
        }

        _watchedGroup = group;
        group.PropertyChanged += OnGroupChanged;
        WatchTab(group.ActiveTab);
    }

    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabGroupViewModel.ActiveTab))
        {
            WatchTab(_watchedGroup?.ActiveTab);
        }
    }

    private void WatchTab(TabViewModel? tab)
    {
        if (_watchedTab is not null)
        {
            _watchedTab.PropertyChanged -= OnTabChanged;
        }

        _watchedTab = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnTabChanged;
        }

        Announce();
    }

    private void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Path))
        {
            Announce();
        }
    }

    private void Announce()
    {
        var path = _watchedTab?.Path;
        if (path != _announced)
        {
            _announced = path;
            Messenger.Send(new ActiveFileChanged(path));
        }
    }

    [RelayCommand]
    private Task ShowQuickSwitcherAsync() => Panels?.QuickSwitcher.ShowAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void ShowCommandPalette() => Panels?.CommandPalette.Show();

    [RelayCommand]
    private void ShowPanel(string panelId) => FocusPanel(panelId);

    /// <summary>Opens (or activates) the graph tab of the active pane (F-012).</summary>
    [RelayCommand]
    private Task<Result<Unit>> OpenGraphAsync(CancellationToken ct) =>
        RunAsync(token => Layout.ActiveGroup.OpenKindAsync(GraphTabViewModel.KindId, token));

    /// <summary>Opens (or activates) the conflict center (F-015.6).</summary>
    [RelayCommand]
    private Task<Result<Unit>> OpenConflictsAsync(CancellationToken ct) =>
        RunAsync(token => Layout.ActiveGroup.OpenKindAsync(Basalt.Presentation.Sync.ConflictCenterViewModel.KindId, token));

    /// <summary>Opens the version history of the active note (F-015.7).</summary>
    [RelayCommand]
    private Task<Result<Unit>> OpenHistoryAsync(CancellationToken ct) =>
        ActiveFile is { } path
            ? RunAsync(token => Layout.ActiveGroup.OpenKindForPathAsync(Basalt.Presentation.Sync.FileHistoryViewModel.KindId, new OpenRequest(path), token))
            : Task.FromResult(Result.Failure<Unit>(new Error(ErrorCodes.EntryUnsupported, "No active note.")));

    /// <summary>Opens the settings window of the app and this vault (F-014).</summary>
    [RelayCommand]
    private void OpenSettings() => _windows.OpenSettings(_vault);

    Task IWorkspaceCommandHost.ShowQuickSwitcherAsync() => ShowQuickSwitcherAsync();

    void IWorkspaceCommandHost.ShowCommandPalette() => ShowCommandPalette();

    void IWorkspaceCommandHost.ShowPanel(string panelId) => FocusPanel(panelId);

    Task IWorkspaceCommandHost.ToggleReadingModeAsync() =>
        Layout.ActiveGroup.ActiveTab is NoteTabViewModel note ? note.ToggleReadingModeCommand.ExecuteAsync(null) : Task.CompletedTask;

    Task IWorkspaceCommandHost.CloseActiveTabAsync(CancellationToken ct) =>
        Layout.ActiveGroup.ActiveTab is { } tab ? Layout.ActiveGroup.CloseTabCommand.ExecuteAsync(tab) : Task.CompletedTask;

    Task IWorkspaceCommandHost.ReopenClosedTabAsync(CancellationToken ct) => ReopenClosedTabAsync(ct);

    Task IWorkspaceCommandHost.GoBackAsync(CancellationToken ct) =>
        Layout.ActiveGroup.ActiveTab is { } tab ? tab.GoBackCommand.ExecuteAsync(null) : Task.CompletedTask;

    Task IWorkspaceCommandHost.GoForwardAsync(CancellationToken ct) =>
        Layout.ActiveGroup.ActiveTab is { } tab ? tab.GoForwardCommand.ExecuteAsync(null) : Task.CompletedTask;

    Task IWorkspaceCommandHost.OpenGraphAsync(CancellationToken ct) => OpenGraphAsync(ct);

    Task IWorkspaceCommandHost.OpenConflictsAsync(CancellationToken ct) => OpenConflictsAsync(ct);

    Task IWorkspaceCommandHost.OpenHistoryAsync(CancellationToken ct) => OpenHistoryAsync(ct);

    Task IWorkspaceCommandHost.OpenSettingsAsync()
    {
        OpenSettings();
        return Task.CompletedTask;
    }

    /// <summary>A key gesture typed in the workspace: runs its command (spec 07 §9). True when a command answered.</summary>
    public Task<bool> HandleGestureAsync(Application.Commands.Gesture gesture, CancellationToken ct) =>
        Panels?.CommandPalette.ExecuteGestureAsync(gesture, ct) ?? Task.FromResult(false);

    private bool CanReopenClosedTab() => _closed.Count > 0;

    [RelayCommand(CanExecute = nameof(CanReopenClosedTab))]
    private async Task ReopenClosedTabAsync(CancellationToken ct)
    {
        if (_closed.TryPop(out var request))
        {
            ReopenClosedTabCommand.NotifyCanExecuteChanged();
            await OpenFileAsync(request with { NewTab = true }, ct).ConfigureAwait(true);
        }
    }
}

/// <summary>Side panels and overlays of a workspace (spec 07 §2: left and right sidebars, quick switcher, palette).</summary>
public sealed record WorkspacePanels(
    Panels.FileExplorerViewModel Explorer,
    Panels.SearchPanelViewModel Search,
    Panels.BookmarksViewModel Bookmarks,
    Panels.TagsPanelViewModel Tags,
    Panels.BacklinksViewModel Backlinks,
    Panels.OutgoingLinksViewModel Outgoing,
    Panels.OutlineViewModel Outline,
    Panels.PropertiesViewModel Properties,
    Panels.LocalGraphViewModel LocalGraph,
    Overlays.QuickSwitcherViewModel QuickSwitcher,
    Overlays.CommandPaletteViewModel CommandPalette) : IDisposable
{
    public void Dispose()
    {
        foreach (var panel in new ViewModelBase[] { Explorer, Search, Bookmarks, Tags, Backlinks, Outgoing, Outline, Properties, LocalGraph, QuickSwitcher, CommandPalette })
        {
            panel.Dispose();
        }
    }
}

/// <summary>A collapsible, resizable sidebar showing one of its panels (F-013).</summary>
public sealed partial class SidebarViewModel(string defaultPanel, IReadOnlyList<string> panels) : ObservableObject
{
    public const double MinWidth = 180;
    public const double MaxWidth = 600;
    public const double DefaultWidth = 280;

    public IReadOnlyList<string> Panels { get; } = panels;

    [ObservableProperty]
    public partial bool IsOpen { get; set; } = true;

    [ObservableProperty]
    public partial string ActivePanel { get; set; } = defaultPanel;

    [ObservableProperty]
    public partial double Width { get; set; } = DefaultWidth;

    public void Show(string panel)
    {
        ActivePanel = panel;
        IsOpen = true;
    }

    public WorkspaceSidebar Capture() => new(IsOpen, Width, ActivePanel);

    public void Restore(WorkspaceSidebar? saved)
    {
        if (saved is null)
        {
            return;
        }

        IsOpen = saved.Open;
        Width = saved.Width;
        if (saved.ActivePanel is { } panel && Panels.Contains(panel))
        {
            ActivePanel = panel;
        }
    }

    partial void OnWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinWidth, MaxWidth);
        if (clamped != value)
        {
            Width = clamped;
        }
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;
}
