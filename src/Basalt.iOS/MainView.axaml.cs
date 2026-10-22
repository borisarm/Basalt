using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Shell;
using Basalt.UI.Services;

namespace Basalt.iOS;

/// <summary>Root view of the single-view iOS app (spec 07 §4).</summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MainView(ShellViewModel shell, NotificationService notifications)
        : this()
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(notifications);
        DataContext = shell;
        this.FindControl<ItemsControl>("Toasts")!.ItemsSource = notifications.Visible;
    }
}
