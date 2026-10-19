using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>Syntax that produced a link (spec 02 §5).</summary>
public enum LinkKind
{
    WikiLink,
    MarkdownLink,
    Embed,
    MarkdownEmbed,
    FrontmatterLink,
}

/// <summary>What the part after '#' points to.</summary>
public enum SubpathKind
{
    None,
    Heading,
    Block,
}

/// <summary>
/// A link occurrence in a document (spec 02 §5). <see cref="RawTarget"/> is the trimmed destination as written;
/// <see cref="Subpath"/> is the text after '#' (a block subpath keeps its '^'); <see cref="TargetSpan"/> covers only
/// the destination so that it can be rewritten precisely.
/// </summary>
public sealed record Link(
    LinkKind Kind,
    string RawTarget,
    string? Subpath,
    SubpathKind SubpathKind,
    string? Alias,
    SourceSpan Span,
    SourceSpan TargetSpan)
{
    public bool IsEmbed => Kind is LinkKind.Embed or LinkKind.MarkdownEmbed;

    /// <summary>"[[#Heading]]": a link to a heading or block of the same note.</summary>
    public bool IsInternalToSource => RawTarget.Length == 0;
}
