using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class OutlineView : UserControl
{
    public OutlineView()
    {
        AvaloniaXamlLoader.Load(this);
        var tree = this.FindControl<TreeView>("Tree")!;
        tree.Tapped += (_, _) =>
        {
            if (DataContext is OutlineViewModel vm && tree.SelectedItem is OutlineItemViewModel heading)
            {
                vm.OpenCommand.Execute(heading);
            }
        };
    }
}
