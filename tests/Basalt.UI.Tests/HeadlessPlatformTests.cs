using Avalonia.Controls;
using AvaloniaEdit;

namespace Basalt.UI.Tests;

/// <summary>Spike T0.4 / ADR-0007: Avalonia.Headless works under xUnit v3 and hosts AvaloniaEdit.</summary>
public sealed class HeadlessPlatformTests
{
    [Fact]
    public Task Window_WithTextEditor_RendersAndAcceptsText() => Headless.RunAsync(() =>
    {
        var editor = new TextEditor { Text = "# Title\n[[link]]" };
        var window = new Window { Content = editor, Width = 400, Height = 300 };

        window.Show();
        editor.Document.Insert(editor.Document.TextLength, "\nmore");

        editor.Text.Should().Be("# Title\n[[link]]\nmore");
        window.IsVisible.Should().BeTrue();
    });
}
