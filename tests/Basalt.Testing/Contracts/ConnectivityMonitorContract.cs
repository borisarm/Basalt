using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IConnectivityMonitor"/> (spec 03 §1.7).</summary>
public abstract class ConnectivityMonitorContract : ContractTest<IConnectivityMonitor>
{
    [Fact]
    public void IsOnline_IsReadable()
    {
        _ = Sut.IsOnline;
    }
}
