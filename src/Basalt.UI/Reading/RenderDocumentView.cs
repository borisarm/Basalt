using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Notes;
using Basalt.Presentation.Workspace;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Basalt.UI.Reading;

/// <summary>
/// Reading mode (spec 07 §6): turns a <see cref="RenderDocument"/> into Avalonia controls, one item per block in a
/// virtualizing list; links open through the tab, task boxes toggle tasks, embeds and images load asynchronously.
/// </summary>
public sealed class RenderDocumentView : UserControl
{
    public static readonly StyledProperty<RenderDocument?> DocumentProperty = AvaloniaProperty.Register<RenderDocumentView, RenderDocument?>(nameof(Document));
    public static readonly StyledProperty<NoteTabViewModel?> TabProperty = AvaloniaProperty.Register<RenderDocumentView, NoteTabViewModel?>(nameof(Tab));

    private readonly ItemsControl _blocks;

    public RenderDocumentView()
    {
        _blocks = new ItemsControl
        {
            Name = "Blocks",
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<RenderBlock>((block, _) => block is null || Tab is null ? null : new BlockRenderer(Tab, Ancestors).Block(block)),
        };
        Content = new ScrollViewer
        {
            FontFamily = BlockRenderer.Font("BasaltFontReading"),
            Content = new Border
            {
                Padding = new Thickness(56, 48, 56, 72),
                MaxWidth = 860,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = _blocks,
            },
        };
    }

    public RenderDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public NoteTabViewModel? Tab
    {
        get => GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    /// <summary>The notes displayed around this one (nested embeds).</summary>
    public IReadOnlyList<VaultPath> Ancestors { get; init; } = [];

    /// <summary>The block controls currently built (tests).</summary>
    public ItemsControl Blocks => _blocks;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty || change.Property == TabProperty)
        {
            _blocks.ItemsSource = Document?.Blocks;
        }
    }
}

/// <summary>Builds the control of one block.</summary>
internal sealed class BlockRenderer(NoteTabViewModel tab, IReadOnlyList<VaultPath> ancestors)
{
    // Design system: note title 36, headings from 24 down, reading text 17 with a 1.7 line height (Literata).
    private const double ReadingSize = 17;
    private const double ReadingLineHeight = 29;
    private static readonly double[] HeadingSizes = [34, 24, 20, 18, 17, 16];

    private double _size = ReadingSize;

    public Control Block(RenderBlock block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Text(paragraph.Inlines, ReadingSize, FontWeight.Normal, new Thickness(0, 0, 0, 14), lineHeight: ReadingLineHeight),
        ListBlock list => List(list),
        QuoteBlock quote => Quote(quote.Blocks, null),
        CalloutBlock callout => Quote(callout.Blocks, callout),
        CodeBlock code => Code(code),
        TableBlock table => Table(table),
        ImageBlock image => Image(image.Source, image.IsExternal, image.AltText, null, null),
        EmbedBlock embed => Embed(embed.Embed),
        ThematicBreakBlock => new Border { Height = 1, Margin = new Thickness(0, 12), Background = Brush("BasaltBorderBrush") },
        FootnotesBlock notes => Footnotes(notes),
        _ => new TextBlock { Text = string.Empty },
    };

    internal static FontFamily Font(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is FontFamily family ? family : FontFamily.Default;

    private SelectableTextBlock Heading(HeadingBlock heading)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var margin = level == 1 ? new Thickness(0, 0, 0, 16) : new Thickness(0, 22, 0, 10);
        return Text(heading.Inlines, HeadingSizes[level - 1], level == 1 ? FontWeight.Bold : FontWeight.SemiBold, margin);
    }

    private SelectableTextBlock Text(
        IReadOnlyList<RenderInline> inlines,
        double size,
        FontWeight weight,
        Thickness margin,
        double lineHeight = double.NaN)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight, Margin = margin, Foreground = Brush("BasaltTextPrimaryBrush") };
        if (!double.IsNaN(lineHeight))
        {
            text.LineHeight = lineHeight;
        }

        _size = size;
        text.Inlines = [.. inlines.Select(Inline)];
        return text;
    }

    private Inline Inline(RenderInline inline) => inline switch
    {
        TextInline text => new Run(text.Text),
        StrongInline strong => Span(strong.Children, s => s.FontWeight = FontWeight.Bold),
        EmphasisInline emphasis => Span(emphasis.Children, s => s.FontStyle = FontStyle.Italic),
        StrikethroughInline strike => Span(strike.Children, s => s.TextDecorations = TextDecorations.Strikethrough),
        HighlightInline highlight => Span(highlight.Children, s => s.Background = Brush("BasaltHighlightBrush")),
        CodeInline code => new Run(code.Code) { FontFamily = Font("BasaltFontMono"), FontSize = _size * 0.85, Background = Brush("BasaltCodeBackgroundBrush") },
        LinkInline link => Link(link),
        TagInline tag => new Run("#" + tag.Tag) { Foreground = Brush("BasaltTagBrush"), Background = Brush("BasaltTagSoftBrush") },
        LineBreakInline breaks => breaks.IsHard ? new LineBreak() : new Run(" "),
        FootnoteReferenceInline reference => new Run($"[{reference.Label}]") { FontSize = 11, Foreground = Brush("BasaltLinkBrush") },
        EmbedInline embed => new Run(embed.Alias ?? embed.Target) { Foreground = Brush("BasaltLinkBrush") },
        ImageInline image => new InlineUIContainer(Image(image.Source, image.IsExternal, image.AltText, null, null)),
        _ => new Run(),
    };

    private Span Span(IReadOnlyList<RenderInline> children, Action<Span> style)
    {
        var span = new Span();
        span.Inlines.AddRange(children.Select(Inline));
        style(span);
        return span;
    }

    private InlineUIContainer Link(LinkInline link)
    {
        var label = new TextBlock
        {
            Text = PlainText(link.Children) is { Length: > 0 } text ? text : link.Target,
            Foreground = Brush("BasaltLinkBrush"),
            FontSize = _size,
            TextDecorations = link.IsExternal ? TextDecorations.Underline : null,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        label.PointerPressed += (_, e) =>
        {
            if (link.IsExternal)
            {
                _ = TopLevel.GetTopLevel(label)?.Launcher.LaunchUriAsync(new Uri(link.Target, UriKind.Absolute));
            }
            else
            {
                var newTab = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                _ = tab.OpenLinkAsync(link.Target, link.Subpath, newTab, CancellationToken.None);
            }
        };
        // An inline control sits on the baseline by its bottom edge; pull it down by the descent to line up the text.
        label.Margin = new Thickness(0, 0, 0, -Math.Round(_size * 0.28));
        return new InlineUIContainer(label);
    }

    private StackPanel List(ListBlock list)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var number = list.Start;
        foreach (var item in list.Items)
        {
            Control marker = item.TaskStatus is { } status
                ? TaskBox(item, status)
                : new TextBlock { Text = list.Ordered ? $"{number}." : "•", Margin = new Thickness(0, 0, 8, 0), Foreground = Brush("BasaltTextSecondaryBrush") };
            number++;
            var content = new StackPanel();
            foreach (var child in item.Blocks)
            {
                content.Children.Add(Block(child));
            }

            var row = new DockPanel();
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private CheckBox TaskBox(ListItemBlock item, char status)
    {
        var box = new CheckBox { IsChecked = status != ' ', Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        box.IsCheckedChanged += (_, _) => _ = tab.ToggleTaskAsync(item.Line, CancellationToken.None);
        return box;
    }

    private Border Quote(IReadOnlyList<RenderBlock> blocks, CalloutBlock? callout)
    {
        var content = new StackPanel();
        if (callout is not null)
        {
            var title = Text(callout.Title, 15, FontWeight.SemiBold, new Thickness(0, 0, 0, 4));
            title.FontFamily = Font("BasaltFontUi");
            if (title.Inlines!.Count == 0)
            {
                title.Text = char.ToUpperInvariant(callout.Type[0]) + callout.Type[1..];
            }

            content.Children.Add(title);
        }

        foreach (var block in blocks)
        {
            content.Children.Add(Block(block));
        }

        if (callout is not null)
        {
            // Callouts read as UI (the design's "Safety" box): interface font on the callout surface, no rule.
            content.SetValue(TextElement.FontFamilyProperty, Font("BasaltFontUi"));
        }

        return new Border
        {
            BorderBrush = Brush("BasaltAccentSoftBrush"),
            BorderThickness = callout is null ? new Thickness(3, 0, 0, 0) : default,
            Background = callout is null ? null : Brush("BasaltCalloutBrush"),
            CornerRadius = callout is null ? new CornerRadius(0) : new CornerRadius(10),
            Padding = callout is null ? new Thickness(18, 2, 0, 2) : new Thickness(18, 14, 18, 4),
            Margin = new Thickness(0, 4, 0, 16),
            Child = callout?.Folding == CalloutFolding.Collapsed ? new Expander { Header = content.Children[0], Content = content } : content,
        };
    }

    private static Border Code(CodeBlock code)
    {
        var copy = new Button
        {
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Data = (Geometry)Avalonia.Application.Current!.FindResource("IconCopy")!,
            },
            Padding = new Thickness(6),
            MinHeight = 26,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Avalonia.Automation.AutomationProperties.SetName(copy, "Copy");
        copy.Click += (_, _) => _ = TopLevel.GetTopLevel(copy)?.Clipboard?.SetTextAsync(code.Code);
        return new Border
        {
            Background = Brush("BasaltCodeBackgroundBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new Panel
            {
                Children =
                {
                    new SelectableTextBlock { Text = code.Code, FontFamily = Font("BasaltFontMono"), FontSize = 13, TextWrapping = TextWrapping.NoWrap },
                    copy,
                },
            },
        };
    }

    private Grid Table(TableBlock table)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (var c = 0; c < table.Header.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rows = new List<IReadOnlyList<IReadOnlyList<RenderInline>>> { table.Header };
        rows.AddRange(table.Rows);
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count && c < table.Header.Count; c++)
            {
                var cell = Text(rows[r][c], 14, r == 0 ? FontWeight.SemiBold : FontWeight.Normal, new Thickness(0));
                cell.TextAlignment = table.Alignments.ElementAtOrDefault(c) switch
                {
                    ColumnAlignment.Center => TextAlignment.Center,
                    ColumnAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                var border = new Border { BorderBrush = Brush("BasaltBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4), Child = cell };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return grid;
    }

    private Control Image(string source, bool external, string? alt, int? width, int? height)
    {
        if (external)
        {
            // No network access in reading mode: external images show their description.
            return new TextBlock { Text = alt ?? source, Foreground = Brush("BasaltTextMutedBrush"), FontStyle = FontStyle.Italic };
        }

        var image = new Image { MaxWidth = width ?? 800, Stretch = Stretch.Uniform };
        if (width is not null)
        {
            image.Width = width.Value;
        }

        if (height is not null)
        {
            image.Height = height.Value;
        }

        _ = LoadImageAsync(image, source);
        return image;
    }

    private async Task LoadImageAsync(Image image, string source)
    {
        if (await tab.GetEmbedAsync(source, null, ancestors, CancellationToken.None).ConfigureAwait(true) is ImageEmbed embed)
        {
            image.Source = await ImageCache.Shared.GetAsync(embed.Path, () => tab.ReadAttachmentAsync(embed.Path, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    private ContentControl Embed(EmbedInline embed)
    {
        var host = new ContentControl { Margin = new Thickness(0, 0, 0, 10) };
        _ = LoadEmbedAsync(host, embed);
        return host;
    }

    private async Task LoadEmbedAsync(ContentControl host, EmbedInline embed)
    {
        host.Content = await tab.GetEmbedAsync(embed.Target, embed.Subpath, ancestors, CancellationToken.None).ConfigureAwait(true) switch
        {
            NoteEmbed note => new Border
            {
                BorderBrush = Brush("BasaltBorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 0),
                Background = Brush("BasaltSurfaceBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new RenderDocumentView { Tab = tab, Document = note.Document, Ancestors = [.. ancestors, note.Path] },
            },
            ImageEmbed image => ImageFromPath(image.Path, embed.Width, embed.Height),
            FileEmbed file => new TextBlock { Text = file.Path.Name, Foreground = Brush("BasaltLinkBrush") },
            CycleEmbed cycle => new TextBlock { Text = "↻ " + cycle.Path.NameWithoutExtension, Foreground = Brush("BasaltTextMutedBrush") },
            MissingEmbed missing => new TextBlock { Text = missing.Target, Foreground = Brush("BasaltUnresolvedLinkBrush"), FontStyle = FontStyle.Italic },
            _ => null,
        };
    }

    private Image ImageFromPath(VaultPath path, int? width, int? height)
    {
        var image = new Image { MaxWidth = width ?? 800, Stretch = Stretch.Uniform };
        if (width is not null)
        {
            image.Width = width.Value;
        }

        if (height is not null)
        {
            image.Height = height.Value;
        }

        _ = SetAsync();
        return image;

        async Task SetAsync() => image.Source = await ImageCache.Shared.GetAsync(path, () => tab.ReadAttachmentAsync(path, CancellationToken.None)).ConfigureAwait(true);
    }

    private StackPanel Footnotes(FootnotesBlock notes)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var note in notes.Footnotes)
        {
            panel.Children.Add(new TextBlock { Text = $"[{note.Label}]", FontSize = 12, Foreground = Brush("BasaltTextMutedBrush") });
            foreach (var block in note.Blocks)
            {
                panel.Children.Add(Block(block));
            }
        }

        return panel;
    }

    private static string PlainText(IEnumerable<RenderInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        TextInline text => text.Text,
        CodeInline code => code.Code,
        StrongInline s => PlainText(s.Children),
        EmphasisInline e => PlainText(e.Children),
        StrikethroughInline s => PlainText(s.Children),
        HighlightInline h => PlainText(h.Children),
        _ => string.Empty,
    }));

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, Avalonia.Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;
}
