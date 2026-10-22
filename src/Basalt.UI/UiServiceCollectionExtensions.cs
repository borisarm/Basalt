using Avalonia.Controls.ApplicationLifetimes;
using Basalt.Application.Vaults;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Diff;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Shell;
using Basalt.UI.Services;
using Basalt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.UI;

/// <summary>DI registration of the Avalonia UI services that Presentation needs.</summary>
public static class UiServiceCollectionExtensions
{
    public static IServiceCollection AddBasaltUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.TryAddSingleton<NotificationService>();
        services.TryAddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.TryAddSingleton<IDialogService, DialogService>();
        services.TryAddSingleton<IThemeService, ThemeService>();
        services.TryAddSingleton<IWindowService, DesktopWindowService>();
        return services;
    }
}

/// <summary>Opens desktop windows; each window has its own shell (F-001.7).</summary>
public sealed class DesktopWindowService(
    Func<ShellViewModel> createShell,
    Func<VaultSessionInfo?, SettingsViewModel> createSettings,
    Func<Presentation.Sync.GitHubSignInViewModel> createSignIn,
    Func<VaultSessionInfo, Presentation.Sync.ConfigureSyncWizardViewModel> createConfigureSync,
    NotificationService notifications) : IWindowService
{
    /// <summary>Opens a window showing the picker, or the given vault.</summary>
    public async Task<MainWindow> OpenWindowAsync(VaultId? vault)
    {
        var shell = createShell();
        var window = new MainWindow(shell, notifications);
        window.Show();
        await shell.InitializeAsync(vault, CancellationToken.None).ConfigureAwait(true);
        return window;
    }

    public void OpenVaultWindow(VaultId vault) => _ = OpenWindowAsync(vault);

    public void OpenNewWindow() => _ = OpenWindowAsync(null);

    public void OpenSettings(VaultSessionInfo vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        new SettingsWindow(createSettings(vault)).Show();
    }

    public Task<bool> ShowSignInAsync(CancellationToken ct)
    {
        var window = new SignInWindow(createSignIn());
        window.Show();
        return window.Result;
    }

    public Task ShowDiffAsync(DiffRequest request, CancellationToken ct)
    {
        var window = new DiffWindow(request);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }

    public Task<bool> ShowConfigureSyncAsync(VaultSessionInfo vault, CancellationToken ct)
    {
        var window = new SyncSetupWindow(createConfigureSync(vault));
        window.Show();
        return window.Result;
    }

    /// <summary>Makes the first window the desktop lifetime's main window.</summary>
    public async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, VaultId? vault)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        desktop.MainWindow = await OpenWindowAsync(vault).ConfigureAwait(true);
    }
}
