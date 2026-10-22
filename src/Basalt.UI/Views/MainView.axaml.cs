using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Application.Ports.Platform;
using Basalt.Presentation.Shell;
using Basalt.UI.Services;

namespace Basalt.UI.Views;

/// <summary>Root view of the single-view iOS app (spec 07 §4).</summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MainView(ShellViewModel shell, NotificationService notifications, IFontScaleProvider? fontScale = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(notifications);
        DataContext = shell;
        this.FindControl<ItemsControl>("Toasts")!.ItemsSource = notifications.Visible;
        if (fontScale is not null)
        {
            ApplyScale(fontScale.Scale);
            fontScale.Changed += (_, scale) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyScale(scale));
        }
    }

    private void ApplyScale(double scale) => FontSize = 16 * scale;
}
