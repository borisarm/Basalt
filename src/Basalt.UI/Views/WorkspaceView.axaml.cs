using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Workspace;

namespace Basalt.UI.Views;

/// <summary>
/// An open vault (spec 07 §3). Code-behind only keeps the sidebar columns in step with their ViewModels: widths come
/// from the saved layout and go back after the user drags a splitter (a gesture, spec 07 §1).
/// </summary>
public partial class WorkspaceView : UserControl
{
    private const int LeftColumn = 0;
    private const int RightColumn = 4;

    private readonly Grid _columns;
    private WorkspaceViewModel? _workspace;

    public WorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
        _columns = this.FindControl<Grid>("Columns")!;
        this.FindControl<GridSplitter>("LeftSplitter")!.DragCompleted += (_, _) => Remember(LeftColumn);
        this.FindControl<GridSplitter>("RightSplitter")!.DragCompleted += (_, _) => Remember(RightColumn);
        DataContextChanged += (_, _) => Attach(DataContext as WorkspaceViewModel);
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
