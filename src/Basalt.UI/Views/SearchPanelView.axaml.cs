using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class SearchPanelView : UserControl
{
    public SearchPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        var results = this.FindControl<ListBox>("Results")!;
        results.DoubleTapped += (_, _) => Open(results.SelectedItem);
        results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open(results.SelectedItem);
            }
        };
    }

    private void Open(object? item)
    {
        if (DataContext is SearchPanelViewModel vm && item is SearchResultGroupViewModel result)
        {
            vm.OpenCommand.Execute(result);
        }
    }
}
