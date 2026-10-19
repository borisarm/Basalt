using Basalt.Domain.Notes;

namespace Basalt.Application.Ports.Markdown;

/// <summary>Context for rendering a note.</summary>
public sealed record RenderContext(VaultPath Source);

/// <summary>
/// Reading-mode model (spec 07 §6): a tree of blocks and inlines independent of HTML and of the UI toolkit. Every
/// block keeps its 0-based source line (scroll sync, task toggling) and an optional block id (<c>^id</c>).
/// </summary>
public sealed record RenderDocument(IReadOnlyList<RenderBlock> Blocks);

public abstract record RenderBlock
{
    public int Line { get; init; }

    public string? BlockId { get; init; }
}

public sealed record HeadingBlock(int Level, IReadOnlyList<RenderInline> Inlines) : RenderBlock;

public sealed record ParagraphBlock(IReadOnlyList<RenderInline> Inlines) : RenderBlock;

/// <summary>A list; <see cref="Start"/> is the first number of ordered lists.</summary>
public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItemBlock> Items) : RenderBlock;

/// <summary>A list item; <see cref="TaskStatus"/> is set for task items (' ' open, 'x' done).</summary>
public sealed record ListItemBlock(IReadOnlyList<RenderBlock> Blocks, char? TaskStatus) : RenderBlock;

public sealed record QuoteBlock(IReadOnlyList<RenderBlock> Blocks) : RenderBlock;

public enum CalloutFolding
{
    None,
    Collapsed,
    Expanded,
}

/// <summary><c>&gt; [!type]± Title</c>; <see cref="Type"/> is normalised to its canonical name (tip, warning...).</summary>
public sealed record CalloutBlock(string Type, IReadOnlyList<RenderInline> Title, CalloutFolding Folding, IReadOnlyList<RenderBlock> Blocks) : RenderBlock;

/// <summary>Fenced or indented code, math blocks (<c>math</c>) and raw HTML shown as text (<c>html</c>).</summary>
public sealed record CodeBlock(string? Language, string Code) : RenderBlock;

public enum ColumnAlignment
{
    None,
    Left,
    Center,
    Right,
}

public sealed record TableBlock(IReadOnlyList<ColumnAlignment> Alignments, IReadOnlyList<IReadOnlyList<RenderInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<RenderInline>>> Rows) : RenderBlock;

/// <summary>A paragraph made only of a Markdown image.</summary>
public sealed record ImageBlock(string Source, bool IsExternal, string? AltText) : RenderBlock;

/// <summary>A paragraph made only of an embed (<c>![[...]]</c> or an internal Markdown image).</summary>
public sealed record EmbedBlock(EmbedInline Embed) : RenderBlock;

public sealed record ThematicBreakBlock : RenderBlock;

public sealed record Footnote(string Label, IReadOnlyList<RenderBlock> Blocks);

public sealed record FootnotesBlock(IReadOnlyList<Footnote> Footnotes) : RenderBlock;

public abstract record RenderInline;

public sealed record TextInline(string Text) : RenderInline;

public sealed record EmphasisInline(IReadOnlyList<RenderInline> Children) : RenderInline;

public sealed record StrongInline(IReadOnlyList<RenderInline> Children) : RenderInline;

public sealed record StrikethroughInline(IReadOnlyList<RenderInline> Children) : RenderInline;

public sealed record HighlightInline(IReadOnlyList<RenderInline> Children) : RenderInline;

public sealed record CodeInline(string Code) : RenderInline;

/// <summary>A link: internal (<see cref="Target"/> + optional <see cref="Subpath"/>) or external (URL).</summary>
public sealed record LinkInline(bool IsExternal, string Target, string? Subpath, IReadOnlyList<RenderInline> Children) : RenderInline;

/// <summary>An embed: <see cref="Width"/>/<see cref="Height"/> come from <c>|300</c> or <c>|300x200</c>.</summary>
public sealed record EmbedInline(string Target, string? Subpath, string? Alias, int? Width, int? Height) : RenderInline;

public sealed record ImageInline(string Source, bool IsExternal, string? AltText) : RenderInline;

public sealed record TagInline(string Tag) : RenderInline;

public sealed record LineBreakInline(bool IsHard) : RenderInline;

public sealed record FootnoteReferenceInline(string Label) : RenderInline;

/// <summary>Produces the reading-mode model (spec 03 §1.2, 07 §6).</summary>
public interface IMarkdownRenderer
{
    RenderDocument Render(string content, RenderContext context);
}
