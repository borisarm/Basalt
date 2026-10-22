using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Diff;

namespace Basalt.UI.Views;

/// <summary>Side-by-side diff aligned by hunks (spec 07 §8).</summary>
public partial class DiffView : UserControl
{
    private readonly ItemsControl _rows;

    public DiffView()
    {
        AvaloniaXamlLoader.Load(this);
        _rows = this.FindControl<ItemsControl>("Rows")!;
    }

    /// <summary>The aligned rows; setting them refreshes the list.</summary>
    public IReadOnlyList<DiffLine>? Document
    {
        get => _rows.ItemsSource as IReadOnlyList<DiffLine>;
        set => _rows.ItemsSource = value;
    }
}
