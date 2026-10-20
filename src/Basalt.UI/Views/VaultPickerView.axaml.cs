using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>The vault picker (F-001).</summary>
public partial class VaultPickerView : UserControl
{
    public VaultPickerView() => AvaloniaXamlLoader.Load(this);
}
