using Avalonia;
using Avalonia.iOS;
using Basalt.UI;
using Foundation;

namespace Basalt.iOS;

/// <summary>iOS entry point: single-view Avalonia lifetime (spec 07 §4) and the head's composition root.</summary>
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<BasaltApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        IosComposition.Attach();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
