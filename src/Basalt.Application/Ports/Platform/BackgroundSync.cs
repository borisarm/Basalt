namespace Basalt.Application.Ports.Platform;

/// <summary>Opportunistic background sync (spec 03 §1.7): BGTaskScheduler on iOS, no-op on desktop.</summary>
public interface IBackgroundSyncScheduler
{
    void Schedule(TimeSpan earliest);

    void Cancel();
}
