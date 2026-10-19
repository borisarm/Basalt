using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;

namespace Basalt.Adapters.Markdown.Tests;

/// <summary>Stable, readable JSON shape of <see cref="NoteMetadata"/> for golden files.</summary>
internal static class MetadataProjection
{
    public static object Project(NoteMetadata metadata, string content) => new
    {
        Frontmatter = metadata.Frontmatter is { } fm
            ? new { Values = Value(fm.Values), Span = Span(fm.Span), fm.Error }
            : null,
        Headings = metadata.Headings.Select(h => new { h.Level, h.Text, h.Line, h.Slug }),
        Links = metadata.Links.Select(l => LinkShape(l, content)),
        Embeds = metadata.Embeds.Select(l => LinkShape(l, content)),
        Tags = metadata.Tags.Select(t => new { t.Tag.Name, t.Source, t.Line }),
        Blocks = metadata.Blocks.Select(b => new { Id = b.Id.Value, b.Line }),
        Tasks = metadata.Tasks.Select(t => new { t.Line, Status = t.Status.ToString(), t.Text }),
        metadata.Aliases,
        metadata.WordCount,
    };

    private static object LinkShape(Link link, string content) => new
    {
        link.Kind,
        link.RawTarget,
        link.Subpath,
        link.SubpathKind,
        link.Alias,
        Span = Span(link.Span),
        Text = Slice(content, link.Span),
        TargetSpan = Span(link.TargetSpan),
        TargetText = Slice(content, link.TargetSpan),
    };

    private static string Span(SourceSpan span) => $"{span.Start}+{span.Length} @{span.Line}:{span.Column}";

    private static string Slice(string content, SourceSpan span) =>
        span.Start >= 0 && span.End <= content.Length ? content.Substring(span.Start, span.Length) : "<out of range>";

    private static object? Value(StructuredValue value) => value switch
    {
        StructuredValue.NullValue => null,
        StructuredValue.BoolValue b => b.Value,
        StructuredValue.NumberValue n => $"number:{n.Raw}",
        StructuredValue.TextValue t => t.Value,
        StructuredValue.DateValue d => $"date:{d.Raw}",
        StructuredValue.DateTimeValue d => $"datetime:{d.Raw}",
        StructuredValue.ListValue l => l.Items.Select(Value).ToList(),
        StructuredValue.MapValue m => m.Entries.ToDictionary(e => e.Key, e => Value(e.Value)),
        _ => throw new InvalidOperationException(),
    };
}
