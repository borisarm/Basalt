namespace Basalt.Application.Ports.Platform;

/// <summary>Base font scale of the system (Dynamic Type, spec 07 §4). 1.0 is the default size.</summary>
public interface IFontScaleProvider
{
    double Scale { get; }

    event EventHandler<double>? Changed;
}
