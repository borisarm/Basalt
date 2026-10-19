using System.Text;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Domain.Links;

/// <summary>Context of a rewrite: the source note before/after the operation, the renames and both vault states.</summary>
public sealed record RewriteRequest(
    VaultPath SourceBefore,
    VaultPath SourceAfter,
    IReadOnlyDictionary<VaultPath, VaultPath> Renames,
    IVaultFileCatalog Before,
    IVaultFileCatalog After,
    VaultSettings Settings);

/// <summary>Rewritten text and the number of link destinations that changed.</summary>
public sealed record RewriteResult(string Text, int ChangedLinks);

/// <summary>
/// Rewrites link destinations after renames/moves (spec 02 §5.2). Only <see cref="Link.TargetSpan"/> is touched, so
/// alias, subpath and the rest of the document are preserved byte for byte (invariant I2). A link is rewritten only
/// when, after the operation, it would no longer resolve to its (possibly renamed) target — which also covers links
/// that become ambiguous and relative links of a moved note. The original syntax (wikilink/Markdown), relative or
/// full-path style and an explicit ".md" are preserved.
/// </summary>
public static class LinkRewriter
{
    public static RewriteResult Rewrite(string text, IReadOnlyList<Link> links, RewriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(request);

        var replacements = new List<(SourceSpan Span, string Text)>();
        foreach (var link in links)
        {
            if (NewDestination(text, link, request) is { } destination)
            {
                replacements.Add((link.TargetSpan, destination));
            }
        }

        // Stryker disable once Block : equivalent (no replacements leaves the text unchanged)
        if (replacements.Count == 0)
        {
            return new RewriteResult(text, 0);
        }

        var builder = new StringBuilder(text);
        foreach (var (span, replacement) in replacements.OrderByDescending(r => r.Span.Start))
        {
            builder.Remove(span.Start, span.Length).Insert(span.Start, replacement);
        }

        return new RewriteResult(builder.ToString(), replacements.Count);
    }

    private static string? NewDestination(string text, Link link, RewriteRequest request)
    {
        var markdown = link.Kind is LinkKind.MarkdownLink or LinkKind.MarkdownEmbed;
        // Stryker disable once Logical : equivalent (internal and external links never resolve to a renamed file)
        if (link.IsInternalToSource || (markdown && LinkResolver.IsExternal(link.RawTarget)))
        {
            return null;
        }

        if (LinkResolver.Resolve(link, request.SourceBefore, request.Before).TargetPath is not { } oldTarget)
        {
            return null;
        }

        var desired = request.Renames.TryGetValue(oldTarget, out var renamed) ? renamed : oldTarget;
        var kind = markdown ? LinkKind.MarkdownLink : LinkKind.WikiLink;
        if (LinkResolver.Resolve(link.RawTarget, kind, request.SourceAfter, request.After).TargetPath == desired)
        {
            return null;
        }

        var current = text.Substring(link.TargetSpan.Start, link.TargetSpan.Length);
        var generated = Generate(link.RawTarget, oldTarget, desired, markdown, request);
        // Stryker disable once Conditional : equivalent (a generated destination always resolves, the current one does not)
        return generated == current ? null : generated;
    }

    private static string Generate(string raw, VaultPath oldTarget, VaultPath desired, bool markdown, RewriteRequest request)
    {
        var written = (markdown ? Uri.UnescapeDataString(raw) : raw).Replace('\\', '/');
        var preferred = written.StartsWith("./", StringComparison.Ordinal) || written.StartsWith("../", StringComparison.Ordinal)
            ? LinkPathStyle.Relative
            : written.Contains('/', StringComparison.Ordinal) && IsFullPath(written, oldTarget)
                ? LinkPathStyle.Absolute
                : request.Settings.LinkPathStyle;
        var keepExtension = !markdown && written.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var kind = markdown ? LinkKind.MarkdownLink : LinkKind.WikiLink;

        string? fallback = null;
        foreach (var style in new[] { preferred, LinkPathStyle.Absolute, LinkPathStyle.Relative })
        {
            var candidate = LinkFormatter.FormatTarget(desired, request.SourceAfter, style, markdown ? LinkFormat.Markdown : LinkFormat.Wikilink, request.After);
            if (keepExtension && desired.Extension == "md" && !candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                candidate += ".md";
            }

            // Stryker disable once Assignment : the fallback is only reached when no style resolves (unreachable for existing targets)
            fallback ??= candidate;
            if (LinkResolver.Resolve(candidate, kind, request.SourceAfter, request.After).TargetPath == desired)
            {
                return candidate;
            }
        }

        return fallback!;
    }

    private static bool IsFullPath(string written, VaultPath target)
    {
        var path = target.ToString();
        return string.Equals(written, path, StringComparison.OrdinalIgnoreCase)
            || (target.Extension == "md" && string.Equals(written + ".md", path, StringComparison.OrdinalIgnoreCase));
    }
}
