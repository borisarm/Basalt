using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Basalt.UI.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
