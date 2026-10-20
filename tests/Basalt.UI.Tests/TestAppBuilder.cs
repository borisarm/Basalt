using Avalonia;
using Avalonia.Headless;

namespace Basalt.UI.Tests;

/// <summary>
/// The real <see cref="BasaltApp"/> (theme and design tokens) on the headless platform, with Skia so that
/// <c>CaptureRenderedFrame</c> works (graph rendering tests).
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BasaltApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
