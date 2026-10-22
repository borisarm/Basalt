using Basalt.Application.Ports.Platform;
using UIKit;

namespace Basalt.iOS;

/// <summary>Dynamic Type of iOS (spec 07 §4): maps <see cref="UIContentSizeCategory"/> to a base font scale.</summary>
internal sealed class IosFontScaleProvider : IFontScaleProvider, IDisposable
{
    private readonly NSObject _observer;

    public IosFontScaleProvider()
    {
        _observer = UIApplication.Notifications.ObserveContentSizeCategoryChanged(
            (_, _) => Changed?.Invoke(this, Scale));
    }

    public double Scale => UIApplication.SharedApplication.PreferredContentSizeCategory switch
    {
        UIContentSizeCategory.ExtraSmall => 0.82,
        UIContentSizeCategory.Small => 0.88,
        UIContentSizeCategory.Medium => 0.94,
        UIContentSizeCategory.Large => 1.0,
        UIContentSizeCategory.ExtraLarge => 1.06,
        UIContentSizeCategory.ExtraExtraLarge => 1.12,
        UIContentSizeCategory.ExtraExtraExtraLarge => 1.18,
        UIContentSizeCategory.AccessibilityMedium => 1.35,
        UIContentSizeCategory.AccessibilityLarge => 1.6,
        UIContentSizeCategory.AccessibilityExtraLarge => 1.9,
        UIContentSizeCategory.AccessibilityExtraExtraLarge => 2.35,
        UIContentSizeCategory.AccessibilityExtraExtraExtraLarge => 2.76,
        _ => 1.0,
    };

    public event EventHandler<double>? Changed;

    public void Dispose() => _observer.Dispose();
}
