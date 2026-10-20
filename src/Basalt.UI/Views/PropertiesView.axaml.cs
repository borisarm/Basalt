using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;

namespace Basalt.UI.Views;

public partial class PropertiesView : UserControl
{
    public PropertiesView()
    {
        AvaloniaXamlLoader.Load(this);
        // Rows are immutable: an edited value is committed as a new row when the box loses focus or on Enter.
        AddHandler(LostFocusEvent, (_, e) => Commit(e.Source as TextBox), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit(e.Source as TextBox);
            }
        }, RoutingStrategies.Bubble);
    }

    private void Commit(TextBox? box)
    {
        if (box is { Tag: PropertyRow row } && box.Text != row.Text && DataContext is PropertiesViewModel vm)
        {
            vm.CommitCommand.Execute(row with { Text = box.Text ?? string.Empty });
        }
    }
}
