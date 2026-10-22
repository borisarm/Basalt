using System.Net.NetworkInformation;
using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform.Apple;

/// <summary>Connectivity of macOS via <see cref="NetworkChange"/>; iOS registers its NWPathMonitor in the head.</summary>
internal sealed class AppleConnectivityMonitor : IConnectivityMonitor
{
    public AppleConnectivityMonitor() =>
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Changed?.Invoke(this, e.IsAvailable);

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler<bool>? Changed;
}
