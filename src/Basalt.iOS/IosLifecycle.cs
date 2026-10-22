using Basalt.Application.Ports.Platform;
using Basalt.Application.Sync;
using Basalt.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.iOS;

/// <summary>iOS lifecycle (spec 07 §4): flush the open workspace and sync while the system grants background time.</summary>
internal static class IosLifecycle
{
    private static ShellViewModel? _shell;
    private static IServiceProvider? _services;

    public static void Configure(ShellViewModel shell, IServiceProvider services)
    {
        _shell = shell;
        _services = services;
    }

    /// <summary>Called by <c>DidEnterBackground</c>: flush, sync best-effort and schedule the next BGAppRefresh.</summary>
    public static async Task BackgroundAsync()
    {
        if (_shell is not { } shell || _services is not { } services)
        {
            return;
        }

        services.GetRequiredService<IBackgroundSyncScheduler>().Schedule(TimeSpan.FromMinutes(15));
        var application = UIApplication.SharedApplication;
        var identifier = application.BeginBackgroundTask("net.basalt.sync.background", () => { });
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await shell.FlushAsync(budget.Token).ConfigureAwait(true);
            await services.GetRequiredService<SyncCoordinator>().RunAppBackgroundAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            application.EndBackgroundTask(identifier);
        }
    }
}
