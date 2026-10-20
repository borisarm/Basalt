using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>An open vault.</summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => AvaloniaXamlLoader.Load(this);
}
