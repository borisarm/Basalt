using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Application.Sync;
using Basalt.Presentation.Sync;

namespace Basalt.UI.Views;

/// <summary>Conflict center (F-015.6): list of conflicts and the four resolutions.</summary>
public partial class ConflictCenterView : UserControl
{
    private readonly Dictionary<RadioButton, ConflictResolutionKind> _options;

    public ConflictCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        _options = new Dictionary<RadioButton, ConflictResolutionKind>
        {
            [this.FindControl<RadioButton>("KeepLocalOption")!] = ConflictResolutionKind.KeepLocal,
            [this.FindControl<RadioButton>("KeepRemoteOption")!] = ConflictResolutionKind.KeepRemote,
            [this.FindControl<RadioButton>("KeepBothOption")!] = ConflictResolutionKind.KeepBoth,
            [this.FindControl<RadioButton>("MergedOption")!] = ConflictResolutionKind.Merged,
        };
    }

    private void OnResolutionChanged(object? sender, RoutedEventArgs e)
    {
        // The handler also fires while the XAML loads, before the options are known.
        if (DataContext is ConflictCenterViewModel viewModel && sender is RadioButton { IsChecked: true } option
            && _options?.TryGetValue(option, out var resolution) == true)
        {
            viewModel.Resolution = resolution;
        }
    }
}
