using Basalt.Application.Ports.Platform;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

public sealed class NoopBackgroundSyncSchedulerContract : BackgroundSyncSchedulerContract
{
    protected override Task<IBackgroundSyncScheduler> CreateSutAsync() =>
        Task.FromResult<IBackgroundSyncScheduler>(new ServiceCollection()
            .AddBasaltPlatformAdapter(Path.Combine(Path.GetTempPath(), "basalt-bgtask-" + Guid.NewGuid().ToString("N")))
            .BuildServiceProvider()
            .GetRequiredService<IBackgroundSyncScheduler>());
}
