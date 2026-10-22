using Avalonia.Controls;

namespace Basalt.UI.Views;

/// <summary>
/// Picks the workspace layout (spec 07 §4): the iPhone navigation stack on narrow screens, the desktop-style columns on
/// an iPad in landscape.
/// </summary>
public sealed class AdaptiveWorkspaceView : UserControl
{
    private const double WideThreshold = 900;

    private bool? _wide;

    public AdaptiveWorkspaceView()
    {
        DataContextChanged += (_, _) =>
        {
            _wide = null;
            Apply();
        };
        SizeChanged += (_, _) => Apply();
        Apply();
    }

    private void Apply()
    {
        var wide = Bounds.Width >= WideThreshold;
        if (_wide == wide && Content is not null)
        {
            return;
        }

        _wide = wide;
        Content = wide
            ? new WorkspaceView { DataContext = DataContext }
            : new MobileWorkspaceView { DataContext = DataContext };
    }
}
