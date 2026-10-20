using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Basalt.UI.Editor;

/// <summary>
/// Obsidian syntax on top of the TextMate Markdown grammar (ADR-0016): wikilinks and embeds, tags, <c>==highlights==</c>
/// and <c>%%comments%%</c>, coloured with the design tokens.
/// </summary>
internal sealed partial class ObsidianSyntaxColorizer(Control owner) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        Paint(line, WikiLink(), text, "BasaltLinkBrush");
        Paint(line, Tag(), text, "BasaltTagBrush");
        Paint(line, Comment(), text, "BasaltTextMutedBrush");
        foreach (Match match in Highlight().Matches(text))
        {
            ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length, element =>
                element.BackgroundBrush = Brush("BasaltHighlightBrush"));
        }
    }

    private void Paint(DocumentLine line, Regex pattern, string text, string brush)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var group = match.Groups["value"].Success ? match.Groups["value"] : (Group)match;
            ChangeLinePart(line.Offset + group.Index, line.Offset + group.Index + group.Length, element => element.TextRunProperties.SetForegroundBrush(Brush(brush)));
        }
    }

    private IBrush? Brush(string key) => owner.TryFindResource(key, owner.ActualThemeVariant, out var value) ? value as IBrush : null;

    [GeneratedRegex(@"!?\[\[[^\]\n]+\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_&#])(?<value>#[\p{L}\p{M}\p{N}_\-/]*[\p{L}\p{M}_\-/][\p{L}\p{M}\p{N}_\-/]*)")]
    private static partial Regex Tag();

    [GeneratedRegex(@"%%.*?%%")]
    private static partial Regex Comment();

    [GeneratedRegex(@"==[^=\n]+==")]
    private static partial Regex Highlight();
}
