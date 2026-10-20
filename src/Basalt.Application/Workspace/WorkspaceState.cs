namespace Basalt.Application.Workspace;

public enum SplitOrientation
{
    /// <summary>Panes side by side.</summary>
    Vertical,

    /// <summary>Panes stacked.</summary>
    Horizontal,
}

/// <summary>A tab: its kind (<c>note</c>, <c>graph</c>, <c>image</c>…), file and view-specific state (scroll, mode…).</summary>
public sealed record WorkspaceTab(string Kind, string? Path = null, bool Pinned = false, string? ViewState = null);

/// <summary>A group of tabs in the central area; <see cref="Size"/> is its share of the split.</summary>
public sealed record WorkspacePane(IReadOnlyList<WorkspaceTab> Tabs, int ActiveTab = 0, double Size = 1);

public sealed record WorkspaceSidebar(bool Open = true, double Width = 280, string? ActivePanel = null);

/// <summary>The layout of a vault on this device (F-013), stored in <c>.basalt/workspace.{deviceId}.json</c> (never synchronised).</summary>
public sealed record WorkspaceState(
    IReadOnlyList<WorkspacePane> Panes,
    int ActivePane = 0,
    SplitOrientation Orientation = SplitOrientation.Vertical,
    WorkspaceSidebar? Left = null,
    WorkspaceSidebar? Right = null)
{
    public static WorkspaceState Empty { get; } = new([]);
}
