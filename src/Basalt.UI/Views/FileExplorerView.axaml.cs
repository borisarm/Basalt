using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class FileExplorerView : UserControl
{
    public FileExplorerView()
    {
        AvaloniaXamlLoader.Load(this);
        var tree = this.FindControl<TreeView>("Tree")!;
        tree.DoubleTapped += (_, _) => Open(tree.SelectedItem);
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open(tree.SelectedItem);
            }
            else if (e.Key == Key.F2 && DataContext is FileExplorerViewModel vm)
            {
                vm.RenameCommand.Execute(vm.Selected);
            }
        };
    }

    private void Open(object? item)
    {
        if (DataContext is FileExplorerViewModel vm && item is FileTreeNodeViewModel { IsFolder: false } node)
        {
            vm.OpenCommand.Execute(node);
        }
    }
}
