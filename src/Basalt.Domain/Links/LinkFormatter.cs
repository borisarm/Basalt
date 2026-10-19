using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Domain.Links;

/// <summary>Generates link text for autocompletion and rewriting (spec 05 §3.1).</summary>
public static class LinkFormatter
{
    /// <summary>Destination text for <paramref name="target"/> as seen from <paramref name="source"/>.</summary>
    public static string FormatTarget(VaultPath target, VaultPath source, LinkPathStyle style, LinkFormat format, IVaultFileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var markdown = format == LinkFormat.Markdown;
        var path = style switch
        {
            LinkPathStyle.Relative => Relative(target, source, markdown),
            LinkPathStyle.Absolute => Display(target.ToString(), markdown),
            _ => Shortest(target, source, markdown, catalog),
        };
        return markdown ? EncodeMarkdownPath(path) : path;
    }

    /// <summary>Complete link (<c>[[...]]</c> or <c>[text](...)</c>) following the vault settings.</summary>
    public static string FormatLink(
        VaultPath target,
        VaultPath source,
        VaultSettings settings,
        IVaultFileCatalog catalog,
        string? subpath = null,
        string? alias = null,
        bool embed = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var destination = FormatTarget(target, source, settings.LinkPathStyle, settings.LinkFormat, catalog);
        var bang = embed ? "!" : string.Empty;
        if (settings.LinkFormat == LinkFormat.Markdown)
        {
            var anchor = subpath is null ? string.Empty : "#" + EncodeMarkdownPath(subpath);
            return $"{bang}[{alias ?? target.NameWithoutExtension}]({destination}{anchor})";
        }

        var hash = subpath is null ? string.Empty : "#" + subpath;
        var pipe = alias is null ? string.Empty : "|" + alias;
        return $"{bang}[[{destination}{hash}{pipe}]]";
    }

    /// <summary>Markdown destinations encode spaces as <c>%20</c>.</summary>
    public static string EncodeMarkdownPath(string path) => path.Replace(" ", "%20", StringComparison.Ordinal);

    // Name only when unique, otherwise the shortest path suffix that unambiguously resolves back to the target.
    private static string Shortest(VaultPath target, VaultPath source, bool markdown, IVaultFileCatalog catalog)
    {
        var segments = target.Segments;
        var kind = markdown ? LinkKind.MarkdownLink : LinkKind.WikiLink;
        for (var take = 1; take <= segments.Count; take++)
        {
            var candidate = Display(string.Join('/', segments.Skip(segments.Count - take)), markdown);
            // Accept when it resolves to the target, or to nothing at all (the target may not exist yet).
            var resolution = LinkResolver.Resolve(candidate, kind, source, catalog);
            if (resolution is LinkResolution.Unresolved || (resolution is LinkResolution.Resolved resolved && resolved.Path == target))
            {
                return candidate;
            }
        }

        return Display(target.ToString(), markdown);
    }

    private static string Relative(VaultPath target, VaultPath source, bool markdown)
    {
        var relative = Display(target.RelativeTo(source.Parent), markdown);
        // Wikilinks are only relative when they start with "./" or "../" (spec 05 §3 step 3.1).
        return markdown || relative.StartsWith("../", StringComparison.Ordinal) ? relative : "./" + relative;
    }

    // Wikilinks omit the ".md" of notes; Markdown links keep the full file name.
    private static string Display(string path, bool markdown) =>
        !markdown && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
}
