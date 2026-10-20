using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Basalt.UI.Views;

/// <summary>A note in a tab: source editor or reading view, and the external-conflict banner.</summary>
public partial class NoteTabView : UserControl
{
    public NoteTabView() => AvaloniaXamlLoader.Load(this);
}
