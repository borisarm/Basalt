using System.Globalization;
using Avalonia.Controls.ApplicationLifetimes;
using Basalt.Adapters.Platform.Apple;
using Basalt.Application.Commands;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Settings;
using Basalt.Application.Sync;
using Basalt.Composition;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Shell;
using Basalt.UI;
using Basalt.UI.Localization;
using Basalt.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Basalt.iOS;

/// <summary>Composition root of the iOS head (spec 12 §3): core, Apple adapters, presentation and UI.</summary>
internal static class IosComposition
{
    private static bool _attached;

    /// <summary>Builds the container and hooks the single-view root before Avalonia starts.</summary>
    public static void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        var services = BuildServices();
        BasaltApp.Services = services;
        _ = services.GetRequiredService<IBackgroundSyncScheduler>();
        ApplySettings(services);
        BasaltApp.Started += app =>
        {
            if (app.ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                var shell = services.GetRequiredService<ShellFactory>().Create();
                IosLifecycle.Configure(shell, services);
                single.MainView = new Basalt.UI.Views.MainView(
                    shell,
                    services.GetRequiredService<NotificationService>(),
                    services.GetRequiredService<IFontScaleProvider>());
            }
        };
    }

    /// <summary>The head's container (validated on build, spec 09 §8).</summary>
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddBasaltCore()
            .AddBasaltApplePlatformAdapter()
            .AddBasaltPresentation(CommandPlatform.MacOS, new ShellOptions(MultipleWindows: false))
            .AddBasaltUi();
        services.Replace(ServiceDescriptor.Singleton<IWindowService, IosWindowService>());
        services.Replace(ServiceDescriptor.Singleton<IConnectivityMonitor, IosConnectivityMonitor>());
        services.Replace(ServiceDescriptor.Singleton<IFontScaleProvider, IosFontScaleProvider>());
        services.AddSingleton(sp => new IosBackgroundSyncScheduler(
            ct => sp.GetRequiredService<SyncCoordinator>().RunBackgroundRefreshAsync(ct)));
        services.Replace(ServiceDescriptor.Singleton<IBackgroundSyncScheduler>(
            sp => sp.GetRequiredService<IosBackgroundSyncScheduler>()));

        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new SerilogLoggerFactory(
            new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(sp.GetRequiredService<IPlatformPaths>().LogsDirectory, "basalt-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger(),
            dispose: true)));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static void ApplySettings(IServiceProvider services)
    {
        var settings = services.GetRequiredService<IAppSettingsStore>().GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        var localizer = services.GetRequiredService<ILocalizer>();
        if (settings.Language is { } language)
        {
            localizer.Culture = CultureInfo.GetCultureInfo(language);
        }

        LocalizationSource.Instance.Attach(localizer);
        BasaltApp.Started += _ =>
            services.GetRequiredService<IThemeService>().Apply(settings.Theme, settings.AccentColor, settings.InterfaceFontSize);
    }
}
