using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IBackgroundSyncScheduler"/> (spec 03 §1.7).</summary>
public abstract class BackgroundSyncSchedulerContract : ContractTest<IBackgroundSyncScheduler>
{
    [Fact]
    public void ScheduleAndCancel_DoNotThrow()
    {
        Sut.Schedule(TimeSpan.FromMinutes(15));
        Sut.Cancel();
        Sut.Cancel();
    }
}
