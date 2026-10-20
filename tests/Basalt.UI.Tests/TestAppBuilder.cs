using Avalonia;
using Avalonia.Headless;

namespace Basalt.UI.Tests;

/// <summary>The real <see cref="BasaltApp"/> (theme and design tokens) on the headless platform.</summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BasaltApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
