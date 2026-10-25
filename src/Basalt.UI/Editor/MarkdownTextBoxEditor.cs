using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Basalt.Presentation.Workspace;

namespace Basalt.UI.Editor;

/// <summary>
/// iOS editor (ADR-0006): a multiline <see cref="TextBox"/> with native IME and a simplified experience, behind the same
/// <see cref="IEditorBridge"/> as the desktop AvaloniaEdit control.
/// </summary>
public sealed class MarkdownTextBoxEditor : UserControl, IEditorBridge
{
    private readonly TextBox _text;
    private NoteTabViewModel? _tab;
    private bool _setting;

    public MarkdownTextBoxEditor()
    {
        _text = new TextBox
        {
            Name = "Editor",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16),
            FontFamily = new FontFamily("Menlo, Menlo-Regular, monospace"),
            FontSize = 16,
        };
        _text.TextChanged += (_, _) =>
        {
            if (!_setting)
            {
                _tab?.OnTextChanged();
            }
        };
        Content = _text;
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    /// <summary>The underlying text box (accessory bar actions and tests).</summary>
    public TextBox TextBox => _text;

    public int Caret => _text.CaretIndex;

    public string GetText() => _text.Text ?? string.Empty;

    public void SetText(string text, int caret)
    {
        _setting = true;
        try
        {
            _text.Text = text;
            _text.CaretIndex = Math.Clamp(caret, 0, text.Length);
        }
        finally
        {
            _setting = false;
        }
    }

    public void Insert(string text) => Replace(_text.CaretIndex, 0, text);

    public void Undo() => _text.Undo();

    public void Redo() => _text.Redo();

    /// <summary>Indents every selected line by two spaces.</summary>
    public void Indent()
    {
        var (start, end) = SelectionLines();
        var lines = GetText()[start..end].Split('\n');
        var indented = string.Join('\n', lines.Select(line => "  " + line));
        Replace(start, end - start, indented);
    }

    /// <summary>Removes up to two leading spaces from every selected line.</summary>
    public void Outdent()
    {
        var (start, end) = SelectionLines();
        var lines = GetText()[start..end].Split('\n');
        var outdented = string.Join('\n', lines.Select(line =>
            line.StartsWith("  ", StringComparison.Ordinal) ? line[2..]
            : line.StartsWith(' ') ? line[1..]
            : line));
        Replace(start, end - start, outdented);
    }

    /// <summary>Hides the software keyboard (the accessory bar's "hide" key).</summary>
    public void HideKeyboard() => TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);

    private (int Start, int End) SelectionLines()
    {
        var value = GetText();
        var selectionStart = _text.SelectionStart;
        var selectionEnd = _text.SelectionEnd;
        var start = Math.Max(0, value.LastIndexOf('\n', Math.Max(0, selectionStart - 1)) + 1);
        var end = selectionEnd <= selectionStart
            ? value.IndexOf('\n', selectionStart) is var index && index >= 0 ? index : value.Length
            : value.IndexOf('\n', selectionEnd) is var index2 && index2 >= 0 ? index2 : value.Length;
        return (start, end);
    }

    // Edits go through the selection so that the TextBox records them on its undo stack (assigning Text does not);
    // TextChanged then tells the tab.
    private void Replace(int start, int length, string text)
    {
        _text.SelectionStart = start;
        _text.SelectionEnd = start + length;
        _text.SelectedText = text;
        _text.CaretIndex = start + text.Length;
    }

    private void Attach()
    {
        if (DataContext is not NoteTabViewModel tab || ReferenceEquals(tab, _tab))
        {
            return;
        }

        Detach();
        _tab = tab;
        tab.AttachEditor(this);
    }

    private void Detach()
    {
        _tab?.DetachEditor();
        _tab = null;
    }
}
