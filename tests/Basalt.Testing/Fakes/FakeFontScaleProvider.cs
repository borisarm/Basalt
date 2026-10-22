using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Fakes;

/// <summary>Controllable Dynamic Type scale for tests.</summary>
public sealed class FakeFontScaleProvider : IFontScaleProvider
{
    public FakeFontScaleProvider(double scale = 1.0) => Scale = scale;

    public double Scale { get; private set; }

    public event EventHandler<double>? Changed;

    public void SetScale(double scale)
    {
        Scale = scale;
        Changed?.Invoke(this, scale);
    }
}
