using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>The shell on iOS: vault picker, then the mobile or iPad workspace.</summary>
public partial class MobileShellView : UserControl
{
    public MobileShellView() => AvaloniaXamlLoader.Load(this);
}
