using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.UI.Editor;

namespace Basalt.UI.Views;

/// <summary>The note page on iPhone/iPad: TextBox editor (ADR-0006) and keyboard accessory bar.</summary>
public partial class MobileNoteView : UserControl
{
    public MobileNoteView() => AvaloniaXamlLoader.Load(this);

    private MarkdownTextBoxEditor Editor => this.FindControl<MarkdownTextBoxEditor>("MobileEditor")!;

    private void OnInsertLink(object? sender, RoutedEventArgs e) => Editor.Insert("[[");

    private void OnInsertHeading(object? sender, RoutedEventArgs e) => Editor.Insert("# ");

    private void OnInsertTask(object? sender, RoutedEventArgs e) => Editor.Insert("- [ ] ");

    private void OnUndo(object? sender, RoutedEventArgs e) => Editor.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Editor.Redo();

    private void OnIndent(object? sender, RoutedEventArgs e) => Editor.Indent();

    private void OnOutdent(object? sender, RoutedEventArgs e) => Editor.Outdent();

    private void OnHideKeyboard(object? sender, RoutedEventArgs e) => Editor.HideKeyboard();
}
