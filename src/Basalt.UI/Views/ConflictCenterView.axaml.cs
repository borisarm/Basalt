using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Application.Sync;
using Basalt.Presentation.Sync;

namespace Basalt.UI.Views;

/// <summary>Conflict center (F-015.6): list of conflicts and the four resolutions.</summary>
public partial class ConflictCenterView : UserControl
{
    private readonly ComboBox _resolution;

    public ConflictCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        _resolution = this.FindControl<ComboBox>("ResolutionBox")!;
        _resolution.SelectedIndex = 0;
    }

    private void OnResolutionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ConflictCenterViewModel viewModel)
        {
            viewModel.Resolution = _resolution.SelectedIndex switch
            {
                1 => ConflictResolutionKind.KeepRemote,
                2 => ConflictResolutionKind.KeepBoth,
                3 => ConflictResolutionKind.Merged,
                _ => ConflictResolutionKind.KeepLocal,
            };
        }
    }
}
