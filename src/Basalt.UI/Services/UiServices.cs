using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Basalt.Application.Settings;
using Basalt.Presentation.Common;
using Basalt.Presentation.Settings;

namespace Basalt.UI.Services;

/// <summary><see cref="IUiDispatcher"/> over Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task InvokeAsync(Func<Task> action) => Dispatcher.UIThread.InvokeAsync(action);
}

/// <summary>
/// In-window notifications: the service keeps the visible toasts; windows show <see cref="Visible"/> in an overlay.
/// Toasts close after a few seconds unless they carry an action.
/// </summary>
public sealed class NotificationService(TimeProvider time) : INotificationService
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    public ObservableCollection<Notification> Visible { get; } = [];

    public void Show(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Dispatcher.UIThread.Post(() => Visible.Add(notification));
        if (notification.Action is null)
        {
            ITimer? timer = null;
            timer = time.CreateTimer(_ =>
            {
                Dispatcher.UIThread.Post(() => Visible.Remove(notification));
                timer?.Dispose();
            }, null, Duration, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dismiss(Notification notification) => Visible.Remove(notification);
}

/// <summary>Theme, accent colour and interface size of the running app (F-014 appearance).</summary>
public sealed class ThemeService : IThemeService
{
    public void Apply(ThemeMode theme, string? accentColor, double interfaceFontSize)
    {
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (accentColor is not null && Color.TryParse(accentColor, out var accent))
        {
            app.Resources["BasaltAccentColor"] = accent;
            app.Resources["SystemAccentColor"] = accent;
        }

        app.Resources["BasaltFontSizeBody"] = interfaceFontSize;
    }
}
