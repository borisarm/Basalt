using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

/// <summary>Desktop background sync: the in-process timer of the coordinator is enough.</summary>
internal sealed class NoopBackgroundSyncScheduler : IBackgroundSyncScheduler
{
    public void Schedule(TimeSpan earliest)
    {
    }

    public void Cancel()
    {
    }
}
