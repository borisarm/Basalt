using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Fakes;

/// <summary>Controllable connectivity for tests.</summary>
public sealed class FakeConnectivityMonitor : IConnectivityMonitor
{
    public FakeConnectivityMonitor(bool online = true) => IsOnline = online;

    public bool IsOnline { get; private set; }

    public event EventHandler<bool>? Changed;

    public void SetOnline(bool online)
    {
        if (online == IsOnline)
        {
            return;
        }

        IsOnline = online;
        Changed?.Invoke(this, online);
    }
}
