using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Fakes;

/// <summary>Records background sync requests for tests.</summary>
public sealed class FakeBackgroundSyncScheduler : IBackgroundSyncScheduler
{
    public TimeSpan? Scheduled { get; private set; }

    public int Cancellations { get; private set; }

    public void Schedule(TimeSpan earliest) => Scheduled = earliest;

    public void Cancel()
    {
        Cancellations++;
        Scheduled = null;
    }
}
