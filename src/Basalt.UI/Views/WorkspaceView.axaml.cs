using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Workspace;
using Basalt.UI.Input;

namespace Basalt.UI.Views;

/// <summary>
/// An open vault (spec 07 §3). Code-behind keeps the sidebar columns in step with their ViewModels (widths come from
/// the saved layout and go back after the user drags a splitter, a gesture, spec 07 §1) and turns key presses into
/// neutral gestures for the command shortcuts (spec 07 §9).
/// </summary>
public partial class WorkspaceView : UserControl
{
    private const int LeftColumn = 0;
    private const int RightColumn = 4;

    private readonly Grid _columns;
    private WorkspaceViewModel? _workspace;
    private TopLevel? _top;

    public WorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
        _columns = this.FindControl<Grid>("Columns")!;
        this.FindControl<GridSplitter>("LeftSplitter")!.DragCompleted += (_, _) => Remember(LeftColumn);
        this.FindControl<GridSplitter>("RightSplitter")!.DragCompleted += (_, _) => Remember(RightColumn);
        DataContextChanged += (_, _) => Attach(DataContext as WorkspaceViewModel);

        // Shortcuts are listened for on the window, not on this view: with nothing focused (a vault just opened) key
        // presses are raised on the window itself and never reach a handler further down.
        AttachedToVisualTree += (_, _) =>
        {
            _top = TopLevel.GetTopLevel(this);
            _top?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _top?.RemoveHandler(KeyDownEvent, OnKeyDown);
            _top = null;
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _workspace is null || GestureMapper.From(e.Key, e.KeyModifiers) is not { } gesture)
        {
            return;
        }

        // A gesture without a command completes synchronously with false; anything else is ours.
        var handled = _workspace.HandleGestureAsync(gesture, CancellationToken.None);
        e.Handled = !handled.IsCompletedSuccessfully || handled.Result;
    }

    private void Attach(WorkspaceViewModel? workspace)
    {
        if (_workspace is not null)
        {
            _workspace.LeftSidebar.PropertyChanged -= OnSidebarChanged;
            _workspace.RightSidebar.PropertyChanged -= OnSidebarChanged;
        }

        _workspace = workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.LeftSidebar.PropertyChanged += OnSidebarChanged;
        workspace.RightSidebar.PropertyChanged += OnSidebarChanged;
        ApplyColumns();
    }

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e) => ApplyColumns();

    private void ApplyColumns()
    {
        if (_workspace is null)
        {
            return;
        }

        _columns.ColumnDefinitions[LeftColumn].Width = new GridLength(_workspace.LeftSidebar.IsOpen ? _workspace.LeftSidebar.Width : 0);
        _columns.ColumnDefinitions[LeftColumn + 1].Width = new GridLength(_workspace.LeftSidebar.IsOpen ? 4 : 0);
        _columns.ColumnDefinitions[RightColumn].Width = new GridLength(_workspace.RightSidebar.IsOpen ? _workspace.RightSidebar.Width : 0);
        _columns.ColumnDefinitions[RightColumn - 1].Width = new GridLength(_workspace.RightSidebar.IsOpen ? 4 : 0);
    }

    private void Remember(int column)
    {
        if (_workspace is null)
        {
            return;
        }

        var sidebar = column == LeftColumn ? _workspace.LeftSidebar : _workspace.RightSidebar;
        sidebar.Width = _columns.ColumnDefinitions[column].ActualWidth;
        ApplyColumns();
    }
}
