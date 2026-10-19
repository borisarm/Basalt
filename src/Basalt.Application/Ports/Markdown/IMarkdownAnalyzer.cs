using Basalt.Domain.Metadata;

namespace Basalt.Application.Ports.Markdown;

/// <summary>
/// Extracts <see cref="NoteMetadata"/> from Markdown (spec 03 §1.2, 05). Synchronous, pure with respect to its input
/// and thread-safe.
/// </summary>
public interface IMarkdownAnalyzer
{
    /// <summary>Analyzes a whole note. Never throws for malformed Markdown or YAML (invalid frontmatter is reported in
    /// <see cref="Frontmatter.Error"/>).</summary>
    NoteMetadata Analyze(string content);
}
