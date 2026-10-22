using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>Version history of a note with its diff (F-015.7).</summary>
public partial class FileHistoryView : UserControl
{
    public FileHistoryView() => AvaloniaXamlLoader.Load(this);
}
