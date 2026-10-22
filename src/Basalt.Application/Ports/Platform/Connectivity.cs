namespace Basalt.Application.Ports.Platform;

/// <summary>Whether the device currently has a usable network route (spec 03 §1.7); iOS uses NWPathMonitor.</summary>
public interface IConnectivityMonitor
{
    bool IsOnline { get; }

    event EventHandler<bool>? Changed;
}
