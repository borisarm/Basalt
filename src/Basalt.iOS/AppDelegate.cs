using Avalonia;
using Avalonia.iOS;
using Basalt.UI;

namespace Basalt.iOS;

/// <summary>iOS entry point: single-view Avalonia lifetime (spec 07 §4) and the head's composition root.</summary>
[Foundation.Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<BasaltApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        IosComposition.Attach();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    public override void DidEnterBackground(UIApplication application) => _ = IosLifecycle.BackgroundAsync();
}
