using Basalt.Application.Ports.Platform;
using BackgroundTasks;
using Foundation;

namespace Basalt.iOS;

/// <summary>
/// BGAppRefreshTask of iOS (spec 06 §11, identifier <c>net.basalt.sync.refresh</c>): schedules opportunistic refreshes and
/// runs the coordinator inside the time granted, rescheduling the next one.
/// </summary>
internal sealed class IosBackgroundSyncScheduler : IBackgroundSyncScheduler
{
    public const string Identifier = "net.basalt.sync.refresh";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static bool _registered;

    private readonly Func<CancellationToken, Task> _refresh;

    public IosBackgroundSyncScheduler(Func<CancellationToken, Task> refresh)
    {
        _refresh = refresh;
        if (!_registered)
        {
            _registered = BGTaskScheduler.Shared.Register(Identifier, null, Handle);
        }
    }

    public void Schedule(TimeSpan earliest)
    {
        var request = new BGAppRefreshTaskRequest(Identifier)
        {
            EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(earliest.TotalSeconds),
        };
        _ = BGTaskScheduler.Shared.Submit(request, out _);
    }

    public void Cancel() => BGTaskScheduler.Shared.Cancel(Identifier);

    private void Handle(BGTask task)
    {
        var cancellation = new CancellationTokenSource();
        var expired = false;
        task.ExpirationHandler = () =>
        {
            expired = true;
            cancellation.Cancel();
        };

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(25));
                await _refresh(budget.Token).ConfigureAwait(false);
                success = !expired;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                success = false;
            }
            finally
            {
                cancellation.Dispose();
                Schedule(RefreshInterval);
                task.SetTaskCompleted(success);
            }
        });
    }
}
