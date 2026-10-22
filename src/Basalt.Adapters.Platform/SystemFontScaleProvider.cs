using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

/// <summary>Desktop font scale: the system does not provide Dynamic Type, so the base size is kept.</summary>
internal sealed class SystemFontScaleProvider : IFontScaleProvider
{
    public double Scale => 1.0;

    public event EventHandler<double>? Changed
    {
        add { }
        remove { }
    }
}
