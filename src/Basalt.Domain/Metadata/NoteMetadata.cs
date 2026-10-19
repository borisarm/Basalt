using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Metadata;

/// <summary>Where a tag occurrence comes from.</summary>
public enum TagSource
{
    Text,
    Frontmatter,
}

/// <summary>A tag occurrence; <see cref="Line"/> is 0-based (frontmatter tags use the line of their key).</summary>
public sealed record TagOccurrence(Tag Tag, TagSource Source, int Line);

/// <summary>A block id (<c>^id</c>) and the 0-based line where it is declared.</summary>
public sealed record BlockReference(BlockId Id, int Line);

/// <summary>A task list item (<c>- [ ]</c> / <c>- [x]</c>).</summary>
/// <param name="Status">The character between brackets (' ' for open tasks).</param>
public sealed record TaskItem(int Line, char Status, string Text)
{
    public bool IsDone => Status is not ' ';
}

/// <summary>
/// YAML frontmatter (spec 02 §4, 05 §5): ordered values, the source range of the whole block (delimiters included)
/// and, when the YAML is invalid, the parse error (values are then empty).
/// </summary>
public sealed record Frontmatter(StructuredValue.MapValue Values, SourceSpan Span, string? Error = null)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Everything Basalt derives from a note's text (spec 02 §4). Recomputable at any time from the content through
/// <c>IMarkdownAnalyzer</c>.
/// </summary>
public sealed record NoteMetadata(
    Frontmatter? Frontmatter,
    IReadOnlyList<Heading> Headings,
    IReadOnlyList<Link> Links,
    IReadOnlyList<Link> Embeds,
    IReadOnlyList<TagOccurrence> Tags,
    IReadOnlyList<BlockReference> Blocks,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<string> Aliases,
    int WordCount)
{
    public static NoteMetadata Empty { get; } = new(null, [], [], [], [], [], [], [], 0);

    /// <summary>Links and embeds in document order.</summary>
    public IEnumerable<Link> AllLinks => Links.Concat(Embeds).OrderBy(l => l.Span.Start);

    /// <summary>Distinct tags (case-insensitive), first spelling wins.</summary>
    public IEnumerable<Tag> DistinctTags => Tags.Select(t => t.Tag).Distinct();
}
