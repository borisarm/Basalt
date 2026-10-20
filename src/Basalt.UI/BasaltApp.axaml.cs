using Avalonia.Markup.Xaml;

namespace Basalt.UI;

/// <summary>The Avalonia application: theme, design tokens and the services of the composition root.</summary>
public partial class BasaltApp : Avalonia.Application
{
    /// <summary>Set by the head before the app starts.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>Called by the head once the framework is ready (desktop: opens the first window).</summary>
    public static Action<BasaltApp>? Started { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Started?.Invoke(this);
        base.OnFrameworkInitializationCompleted();
    }
}
