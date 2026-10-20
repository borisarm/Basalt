using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Settings;

namespace Basalt.UI.Views;

/// <summary>A desktop window with the settings screen (spec 07 §2, F-014).</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? _settings;

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SettingsWindow(SettingsViewModel settings)
        : this()
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        DataContext = settings;
        Opened += async (_, _) => await settings.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        Closed += (_, _) => settings.Dispose();
    }

    public SettingsViewModel? Settings => _settings;
}
