using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Overlays;

namespace Basalt.UI.Views;

public partial class QuickSwitcherView : UserControl
{
    public QuickSwitcherView()
    {
        AvaloniaXamlLoader.Load(this);
        var input = this.FindControl<TextBox>("Input")!;
        var list = this.FindControl<ListBox>("List")!;
        input.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.DoubleTapped += (_, _) => (DataContext as QuickSwitcherViewModel)?.AcceptCommand.Execute(QuickSwitchAccept.Open);
        list.SelectionChanged += (_, _) =>
        {
            if (DataContext is QuickSwitcherViewModel vm && list.SelectedIndex >= 0 && list.SelectedIndex != vm.SelectedIndex)
            {
                vm.MoveSelection(list.SelectedIndex - vm.SelectedIndex);
            }
        };
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
        if (DataContext is not QuickSwitcherViewModel vm)
        {
            return;
        }

        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                break;
            case Key.Enter:
                vm.AcceptCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? QuickSwitchAccept.Create : mod ? QuickSwitchAccept.NewTab : QuickSwitchAccept.Open);
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
