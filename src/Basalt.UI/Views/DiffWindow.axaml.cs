using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Diff;

namespace Basalt.UI.Views;

/// <summary>Window with a side-by-side diff (spec 07 §8).</summary>
public partial class DiffWindow : Window
{
    public DiffWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public DiffWindow(DiffRequest request)
        : this()
    {
        ArgumentNullException.ThrowIfNull(request);
        Title = request.Title;
        this.FindControl<TextBlock>("LeftTitle")!.Text = request.LeftTitle;
        this.FindControl<TextBlock>("RightTitle")!.Text = request.RightTitle;
        this.FindControl<DiffView>("Diff")!.Document = DiffDocument.Build(request.Left, request.Right);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
