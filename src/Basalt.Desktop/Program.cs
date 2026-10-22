using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Basalt.Application.Commands;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Composition;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Shell;
using Basalt.UI;
using Basalt.UI.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Basalt.Desktop;

/// <summary>Desktop head (Windows and macOS): composition root and Avalonia start-up.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var services = BuildServices(AppDataArgument(args));
        ApplySettings(services);
        BasaltApp.Services = services;
        BasaltApp.Started += app =>
        {
            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var settings = services.GetRequiredService<IAppSettingsStore>().GetAsync(CancellationToken.None).GetAwaiter().GetResult();
                var windows = (DesktopWindowService)services.GetRequiredService<IWindowService>();
                _ = windows.StartAsync(desktop, settings.OpenLastVault ? settings.LastVaultId : null);
            }
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary><c>--appdata &lt;folder&gt;</c>: keeps all app data in that folder (portable installs, testing).</summary>
    private static string? AppDataArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--appdata");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    /// <summary>Used by the Avalonia previewer and by <see cref="Main"/>.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BasaltApp>().UsePlatformDetect().WithInterFont().LogToTrace();

    /// <summary>The head's container (validated on build, spec 09 §8).</summary>
    public static ServiceProvider BuildServices(string? appDataOverride = null)
    {
        var platform = OperatingSystem.IsMacOS() ? CommandPlatform.MacOS : CommandPlatform.Windows;
        var services = new ServiceCollection()
            .AddLogging()
            .AddBasaltCore(appDataOverride)
            .AddBasaltPresentation(platform, new ShellOptions(MultipleWindows: true))
            .AddBasaltUi();
        services.AddSingleton<Func<ShellViewModel>>(sp => sp.GetRequiredService<ShellFactory>().Create);
        services.AddSingleton<Func<VaultSessionInfo?, SettingsViewModel>>(sp => sp.GetRequiredService<ShellFactory>().CreateSettings);
        services.AddSingleton<Func<Presentation.Sync.GitHubSignInViewModel>>(sp => sp.GetRequiredService<ShellFactory>().CreateSignIn);

        // Rolling log files kept 7 days (spec 08 §1); tokens and note content are never logged (spec 13 §6).
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new SerilogLoggerFactory(
            new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(sp.GetRequiredService<IPlatformPaths>().LogsDirectory, "basalt-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger(),
            dispose: true)));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Language and appearance from <c>settings.json</c> before the first window.</summary>
    private static void ApplySettings(IServiceProvider services)
    {
        var settings = services.GetRequiredService<IAppSettingsStore>().GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        var localizer = services.GetRequiredService<ILocalizer>();
        if (settings.Language is { } language)
        {
            localizer.Culture = CultureInfo.GetCultureInfo(language);
        }

        LocalizationSource.Instance.Attach(localizer);
        BasaltApp.Started += _ => services.GetRequiredService<IThemeService>().Apply(settings.Theme, settings.AccentColor, settings.InterfaceFontSize);
    }
}
