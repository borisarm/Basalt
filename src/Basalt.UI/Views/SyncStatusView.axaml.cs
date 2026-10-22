using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>Sync indicator and actions of the status bar (F-015.4).</summary>
public partial class SyncStatusView : UserControl
{
    public SyncStatusView() => AvaloniaXamlLoader.Load(this);
}
