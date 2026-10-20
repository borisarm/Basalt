using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Shell;
using Basalt.UI.Services;

namespace Basalt.UI.Views;

/// <summary>A desktop window hosting one shell (vault picker or a vault's workspace) and the notifications overlay.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MainWindow(ShellViewModel shell, NotificationService notifications)
        : this()
    {
        ArgumentNullException.ThrowIfNull(notifications);
        DataContext = shell;
        this.FindControl<ItemsControl>("Toasts")!.ItemsSource = notifications.Visible;
        Closing += async (_, e) =>
        {
            // Unsaved edits are written before the window goes away (F-003: never lose an edit on close).
            if (shell.ActiveVault is not null && !_closingConfirmed)
            {
                e.Cancel = true;
                await shell.CloseVaultCommand.ExecuteAsync(null).ConfigureAwait(true);
                _closingConfirmed = true;
                Close();
            }
        };
    }

    private bool _closingConfirmed;

    public ShellViewModel? Shell => DataContext as ShellViewModel;
}
