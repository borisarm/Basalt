using Basalt.Domain.Common;
using Basalt.Domain.Metadata;

namespace Basalt.Application.Ports.Markdown;

/// <summary>
/// Format-preserving frontmatter edits (spec 03 §1.2, 05 §5): only the lines of the affected key change when the value
/// is a scalar or a block list; otherwise the YAML block is regenerated in canonical style keeping key order. The rest
/// of the document is never touched.
/// </summary>
public interface IFrontmatterEditor
{
    /// <summary>Sets (adds or replaces) a top-level property, creating the frontmatter if there is none.</summary>
    string SetProperty(string content, string key, StructuredValue value);

    /// <summary>Removes a top-level property; returns the content unchanged when it does not exist.</summary>
    string RemoveProperty(string content, string key);

    /// <summary>Parses YAML text (without delimiters).</summary>
    /// <returns><c>frontmatter.invalid_yaml</c>, <c>frontmatter.not_a_mapping</c> or <c>frontmatter.too_large</c> on failure.</returns>
    Result<Frontmatter> Parse(string yaml);

    /// <summary>Renders values as a complete frontmatter block (delimiters included) in canonical style.</summary>
    string Render(StructuredValue.MapValue values, string lineEnding = "\n");
}
