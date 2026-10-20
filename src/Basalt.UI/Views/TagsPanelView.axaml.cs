using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class TagsPanelView : UserControl
{
    public TagsPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        var tree = this.FindControl<TreeView>("Tree")!;
        tree.DoubleTapped += (_, _) => Search(tree.SelectedItem);
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Search(tree.SelectedItem);
            }
        };
    }

    private void Search(object? item)
    {
        if (DataContext is TagsPanelViewModel vm && item is TagNodeViewModel tag)
        {
            vm.SearchTagCommand.Execute(tag);
        }
    }
}
