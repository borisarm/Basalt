using Basalt.Application.Ports.Platform;
using CoreFoundation;
using Network;

namespace Basalt.iOS;

/// <summary>Connectivity of iOS via NWPathMonitor (spec 03 §1.7).</summary>
internal sealed class IosConnectivityMonitor : IConnectivityMonitor
{
    private readonly NWPathMonitor _monitor = new();
    private bool _isOnline = true;

    public IosConnectivityMonitor()
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

    public event EventHandler<bool>? Changed;
}
