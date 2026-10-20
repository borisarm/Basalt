using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Overlays;

namespace Basalt.UI.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        AvaloniaXamlLoader.Load(this);
        var input = this.FindControl<TextBox>("Input")!;
        var list = this.FindControl<ListBox>("List")!;
        input.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.DoubleTapped += (_, _) => (DataContext as CommandPaletteViewModel)?.ExecuteCommand.Execute(null);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => input.Focus());
            }
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when vm.Items.Count > 0:
                vm.SelectedIndex = (vm.SelectedIndex + 1) % vm.Items.Count;
                break;
            case Key.Up when vm.Items.Count > 0:
                vm.SelectedIndex = (vm.SelectedIndex - 1 + vm.Items.Count) % vm.Items.Count;
                break;
            case Key.Enter:
                vm.ExecuteCommand.Execute(null);
                break;
            case Key.Escape:
                vm.CloseCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
