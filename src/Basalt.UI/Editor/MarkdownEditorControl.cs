using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using Basalt.Presentation.Editor;
using Basalt.Presentation.Workspace;
using TextMateSharp.Grammars;
using EditorTextEdit = Basalt.Presentation.Editor.TextEdit;

namespace Basalt.UI.Editor;

/// <summary>
/// The desktop Markdown editor (spec 07 §5.1): AvaloniaEdit with TextMate highlighting, Obsidian syntax colouring,
/// folding, completion, find/replace and the editor behaviours of <see cref="IEditorCommands"/>. It is the
/// <see cref="IEditorBridge"/> of its <see cref="NoteTabViewModel"/> (its DataContext).
/// </summary>
public sealed class MarkdownEditorControl : UserControl, IEditorBridge
{
    private readonly TextEditor _editor;
    private readonly FoldingManager _folding;
    private readonly DispatcherTimer _foldingTimer;
    private NoteTabViewModel? _tab;
    private bool _settingText;
    private int _lastLine = -1;
    private CompletionWindow? _completion;

    public MarkdownEditorControl()
    {
        _editor = new TextEditor
        {
            Name = "Editor",
            WordWrap = true,
            ShowLineNumbers = false,
            FontSize = 15,
            Padding = new Thickness(24, 12),
            Background = Brushes.Transparent,
        };
        Content = _editor;
        InstallHighlighting();
        _editor.TextArea.TextView.LineTransformers.Add(new ObsidianSyntaxColorizer(this));
        _folding = FoldingManager.Install(_editor.TextArea);
        _foldingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _foldingTimer.Tick += (_, _) =>
        {
            _foldingTimer.Stop();
            UpdateFoldings();
        };
        SearchPanel.Install(_editor);

        _editor.Document.TextChanged += OnTextChanged;
        _editor.TextArea.Caret.PositionChanged += OnCaretMoved;
        _editor.TextArea.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _editor.TextArea.AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Attach(DataContext as NoteTabViewModel);
        ActualThemeVariantChanged += (_, _) => InstallHighlighting();
    }

    /// <summary>The wrapped AvaloniaEdit control (tests, focus).</summary>
    public TextEditor Editor => _editor;

    /// <summary>The current folding regions (headings and code blocks).</summary>
    public IEnumerable<FoldingSection> Foldings => _folding.AllFoldings;

    public int Caret => _editor.CaretOffset;

    public string GetText() => _editor.Text;

    public void SetText(string text, int caret)
    {
        _settingText = true;
        try
        {
            _editor.Document.Text = text;
            _editor.CaretOffset = Math.Clamp(caret, 0, text.Length);
        }
        finally
        {
            _settingText = false;
        }

        UpdateFoldings();
    }

    /// <summary>Shows the completion list for the link or tag at the caret, if any.</summary>
    public async Task<int> ShowCompletionsAsync()
    {
        if (_tab is null)
        {
            return 0;
        }

        var items = await _tab.GetCompletionsAsync(State, CancellationToken.None).ConfigureAwait(true);
        _completion?.Close();
        if (items.Count == 0)
        {
            return 0;
        }

        _completion = new CompletionWindow(_editor.TextArea);
        foreach (var item in items)
        {
            _completion.CompletionList.CompletionData.Add(new CompletionData(this, item));
        }

        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
        return items.Count;
    }

    /// <summary>Applies an accepted completion (also used by tests).</summary>
    public async Task AcceptAsync(CompletionItem item)
    {
        if (_tab is null)
        {
            return;
        }

        var edit = await _tab.AcceptCompletionAsync(State, item, CancellationToken.None).ConfigureAwait(true);
        if (edit.IsSuccess)
        {
            Apply(new EditResult([edit.Value], edit.Value.Offset + edit.Value.Replacement.Length, 0));
        }
    }

    private TextBufferState State => new(_editor.Text, _editor.SelectionStart, _editor.SelectionLength);

    private void Attach(NoteTabViewModel? tab)
    {
        _tab?.DetachEditor();
        _tab = tab;
        tab?.AttachEditor(this);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (!_settingText)
        {
            _tab?.OnTextChanged();
            _foldingTimer.Stop();
            _foldingTimer.Start();
        }
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        var line = _editor.TextArea.Caret.Line - 1;
        if (line != _lastLine)
        {
            _lastLine = line;
            _tab?.OnCaretLineChanged(line);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab is null || _completion is not null)
        {
            return;
        }

        var commands = _tab.Editing;
        var command = KeyGesture(e) switch
        {
            (Key.Enter, KeyModifiers.None) => commands.Enter(State),
            (Key.Tab, KeyModifiers.None) => commands.Indent(State),
            (Key.Tab, KeyModifiers.Shift) => commands.Outdent(State),
            (Key.B, var m) when IsMod(m) => commands.ToggleWrap(State, "**"),
            (Key.I, var m) when IsMod(m) => commands.ToggleWrap(State, "*"),
            (Key.K, var m) when IsMod(m) => commands.InsertLink(State),
            (Key.Enter, var m) when IsMod(m) => commands.ToggleTask(State),
            _ => null,
        };

        if (command is not null)
        {
            Apply(command);
            e.Handled = true;
            return;
        }

        if (KeyGesture(e) is (Key.V, var paste) && IsMod(paste))
        {
            e.Handled = TryPasteImage();
        }
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_tab is null || e.Text is not { Length: 1 } typed)
        {
            return;
        }

        if (_tab.Editing.AutoClose(State, typed[0]) is { } closed)
        {
            Apply(closed);
            e.Handled = true;
        }

        if (typed[0] is '[' or '#' || _completion is null && CompletionTrigger.Detect(State.Text, State.SelectionStart) is not null)
        {
            Dispatcher.UIThread.Post(() => _ = ShowCompletionsAsync());
        }
    }

    /// <summary>Pastes a PNG from the clipboard as an attachment (F-003.5); text pastes as usual.</summary>
    private bool TryPasteImage()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || _tab is null)
        {
            return false;
        }

        _ = PasteImageAsync(clipboard);
        return false;
    }

    private async Task PasteImageAsync(IClipboard clipboard)
    {
        using var bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
        if (bitmap is null || _tab is null)
        {
            return;
        }

        using var png = new MemoryStream();
        bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var embed = await _tab.PasteImageAsync(png.ToArray(), "png", CancellationToken.None).ConfigureAwait(true);
        if (embed.IsSuccess)
        {
            Apply(new EditResult([new EditorTextEdit(_editor.CaretOffset, 0, embed.Value)], _editor.CaretOffset + embed.Value.Length, 0));
        }
    }

    /// <summary>Applies edits as one undoable step and sets the resulting selection.</summary>
    public void Apply(EditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var document = _editor.Document;
        document.BeginUpdate();
        try
        {
            foreach (var edit in result.Edits.Select((e, i) => (Edit: e, Index: i)).OrderByDescending(x => x.Edit.Offset).ThenByDescending(x => x.Index))
            {
                document.Replace(edit.Edit.Offset, edit.Edit.Length, edit.Edit.Replacement);
            }
        }
        finally
        {
            document.EndUpdate();
        }

        _editor.Select(result.SelectionStart, result.SelectionLength);
        _editor.CaretOffset = result.SelectionStart + result.SelectionLength;
    }

    private static (Key Key, KeyModifiers Modifiers) KeyGesture(KeyEventArgs e) => (e.Key, e.KeyModifiers);

    private static bool IsMod(KeyModifiers modifiers) => modifiers is KeyModifiers.Control or KeyModifiers.Meta;

    private void InstallHighlighting()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var registry = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
        var textMate = _editor.InstallTextMate(registry);
        textMate.SetGrammar(registry.GetScopeByLanguageId(registry.GetLanguageByExtension(".md").Id));
    }

    /// <summary>Folds headings (to the next heading of the same or a higher level) and fenced code blocks.</summary>
    private void UpdateFoldings()
    {
        var document = _editor.Document;
        var foldings = new List<NewFolding>();
        var open = new Stack<(int Level, int Start)>();
        int? fence = null;
        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length);
            if (text.TrimStart().StartsWith("```", StringComparison.Ordinal) || text.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fence is { } start)
                {
                    foldings.Add(new NewFolding(start, line.EndOffset));
                    fence = null;
                }
                else
                {
                    fence = line.EndOffset;
                }

                continue;
            }

            if (fence is null && HeadingLevel(text) is { } level)
            {
                while (open.Count > 0 && open.Peek().Level >= level)
                {
                    var heading = open.Pop();
                    AddFolding(foldings, heading.Start, line.Offset - 1);
                }

                open.Push((level, line.EndOffset));
            }
        }

        while (open.Count > 0)
        {
            AddFolding(foldings, open.Pop().Start, document.TextLength);
        }

        _folding.UpdateFoldings(foldings.OrderBy(f => f.StartOffset), -1);
    }

    private static void AddFolding(List<NewFolding> foldings, int start, int end)
    {
        if (end > start)
        {
            foldings.Add(new NewFolding(start, end));
        }
    }

    private static int? HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is > 0 and <= 6 && (level == line.Length || line[level] == ' ') ? level : null;
    }

    /// <summary>An entry of the completion window.</summary>
    private sealed class CompletionData(MarkdownEditorControl owner, CompletionItem item) : ICompletionData
    {
        public IImage? Image => null;

        public string Text => item.Display;

        public object Content => item.Detail is { } detail ? $"{item.Display}  —  {detail}" : item.Display;

        public object? Description => item.Detail;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => _ = owner.AcceptAsync(item);
    }
}
