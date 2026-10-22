using System.Net.NetworkInformation;
using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

/// <summary>Connectivity of desktop platforms via <see cref="NetworkInterface.GetIsNetworkAvailable"/>.</summary>
internal sealed class SystemConnectivityMonitor : IConnectivityMonitor
{
    public SystemConnectivityMonitor() =>
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Changed?.Invoke(this, e.IsAvailable);

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler<bool>? Changed;
}
