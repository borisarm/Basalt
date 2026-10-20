using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class BookmarksView : UserControl
{
    public BookmarksView()
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
        };
    }

    private void Open(object? item)
    {
        if (DataContext is BookmarksViewModel vm && item is BookmarkItemViewModel bookmark)
        {
            vm.OpenCommand.Execute(bookmark);
        }
    }
}
