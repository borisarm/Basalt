using Basalt.Application.Ports.Platform;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

public sealed class SystemConnectivityMonitorContract : ConnectivityMonitorContract
{
    protected override Task<IConnectivityMonitor> CreateSutAsync() =>
        Task.FromResult<IConnectivityMonitor>(new ServiceCollection()
            .AddBasaltPlatformAdapter(Path.Combine(Path.GetTempPath(), "basalt-connectivity-" + Guid.NewGuid().ToString("N")))
            .BuildServiceProvider()
            .GetRequiredService<IConnectivityMonitor>());
}
