using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Diff;

namespace Basalt.UI.Views;

/// <summary>Side-by-side diff aligned by hunks (spec 07 §8).</summary>
public partial class DiffView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<DiffLine>?> DocumentProperty =
        AvaloniaProperty.Register<DiffView, IReadOnlyList<DiffLine>?>(nameof(Document));

    private readonly ItemsControl _rows;

    public DiffView()
    {
        AvaloniaXamlLoader.Load(this);
        _rows = this.FindControl<ItemsControl>("Rows")!;
    }

    /// <summary>The aligned rows; setting them refreshes the list.</summary>
    public IReadOnlyList<DiffLine>? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            _rows.ItemsSource = Document;
        }
    }
}
