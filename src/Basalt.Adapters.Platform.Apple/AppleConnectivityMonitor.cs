using Basalt.Application.Ports.Platform;

#if IOS
using CoreFoundation;
using Network;
#else
using System.Net.NetworkInformation;
#endif

namespace Basalt.Adapters.Platform.Apple;

/// <summary>
/// Connectivity of Apple platforms (spec 03 §1.7): NWPathMonitor on iOS and NetworkChange on macOS.
/// </summary>
internal sealed class AppleConnectivityMonitor : IConnectivityMonitor
{
#if IOS
    private readonly NWPathMonitor _monitor = new();
    private bool _isOnline = true;

    public AppleConnectivityMonitor()
    {
        _monitor.SetQueue(DispatchQueue.MainQueue);
        _monitor.SetUpdatedHandler(path =>
        {
            var online = path.Status == NWPathStatus.Satisfied;
            if (online != _isOnline)
            {
                _isOnline = online;
                Changed?.Invoke(this, online);
            }
        });
        _monitor.Start();
    }

    public bool IsOnline => _isOnline;
#else
    public AppleConnectivityMonitor() =>
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Changed?.Invoke(this, e.IsAvailable);

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();
#endif

    public event EventHandler<bool>? Changed;
}
