using System.Text;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>Resolves link targets following Obsidian's rules (spec 05 §3).</summary>
public static class LinkResolver
{
    private const string NoteExtension = ".md";
    private static readonly string[] ExternalSchemes = ["http:", "https:", "mailto:", "obsidian:", "file:"];

    public static LinkResolution Resolve(Link link, VaultPath source, IVaultFileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Resolve(link.RawTarget, link.Kind, source, catalog);
    }

    /// <param name="target">Destination without subpath, as written.</param>
    public static LinkResolution Resolve(string target, LinkKind kind, VaultPath source, IVaultFileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalog);
        var markdown = kind is LinkKind.MarkdownLink or LinkKind.MarkdownEmbed;
        if (markdown && IsExternal(target))
        {
            return new LinkResolution.Unresolved(target.Trim());
        }

        var normalized = Normalize(target, markdown);
        if (normalized.Length == 0)
        {
            return new LinkResolution.Resolved(source);
        }

        if (markdown)
        {
            // Spec 05 §3.3: relative to the source first, then absolute, then as a wikilink.
            var direct = FindExact(source.Parent, normalized, catalog) ?? FindExact(VaultPath.Root, normalized, catalog);
            return direct is { } found ? new LinkResolution.Resolved(found) : ResolveLikeWikiLink(normalized, source, catalog);
        }

        return ResolveLikeWikiLink(normalized, source, catalog);
    }

    /// <summary>True for targets with a URL scheme that is never resolved inside the vault.</summary>
    public static bool IsExternal(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var trimmed = target.Trim();
        return ExternalSchemes.Any(s => trimmed.StartsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static LinkResolution ResolveLikeWikiLink(string target, VaultPath source, IVaultFileCatalog catalog)
    {
        if (!target.Contains('/', StringComparison.Ordinal))
        {
            return Choose(target, ByName(target, catalog), source);
        }

        if (target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith("../", StringComparison.Ordinal))
        {
            return FindExact(source.Parent, target, catalog) is { } relative
                ? new LinkResolution.Resolved(relative)
                : new LinkResolution.Unresolved(target);
        }

        if (FindExact(VaultPath.Root, target, catalog) is { } absolute)
        {
            return new LinkResolution.Resolved(absolute);
        }

        var name = target[(target.LastIndexOf('/') + 1)..];
        var suffixed = ByName(name, catalog)
            .Where(p => EndsWithSuffix(p, target))
            .ToList();
        return Choose(target, suffixed, source);
    }

    private static IReadOnlyCollection<VaultPath> ByName(string name, IVaultFileCatalog catalog)
    {
        if (HasExtension(name))
        {
            var exact = catalog.FindByName(name);
            if (exact.Count > 0)
            {
                return exact;
            }
        }

        return catalog.FindByName(name + NoteExtension);
    }

    private static bool EndsWithSuffix(VaultPath path, string target)
    {
        var text = path.ToString();
        return EndsWithSegments(text, target) || EndsWithSegments(text, target + NoteExtension);
    }

    private static bool EndsWithSegments(string path, string suffix) =>
        path.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase) || string.Equals(path, suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks up <paramref name="relative"/> (with ".md" when it has no extension) from a folder.</summary>
    private static VaultPath? FindExact(VaultPath folder, string relative, IVaultFileCatalog catalog)
    {
        string[] candidates = HasExtension(relative) ? [relative, relative + NoteExtension] : [relative + NoteExtension];
        foreach (var candidate in candidates)
        {
            var combined = folder.Combine(candidate);
            if (combined.IsFailure)
            {
                continue;
            }

            var matches = catalog.FindByPath(combined.Value);
            if (matches.Count > 0)
            {
                return matches.Contains(combined.Value) ? combined.Value : matches.Order().First();
            }
        }

        return null;
    }

    private static LinkResolution Choose(string target, IReadOnlyCollection<VaultPath> candidates, VaultPath source)
    {
        if (candidates.Count == 0)
        {
            return new LinkResolution.Unresolved(target);
        }

        if (candidates.Count == 1)
        {
            return new LinkResolution.Resolved(candidates.First());
        }

        var expectedName = target[(target.LastIndexOf('/') + 1)..];
        var ranked = candidates
            .OrderBy(p => IsExactCase(p, expectedName) ? 0 : 1)
            .ThenBy(p => p.Parent == source.Parent ? 0 : 1)
            .ThenBy(p => p.Segments.Count)
            .ThenBy(p => p.ToString(), StringComparer.Ordinal)
            .ToList();
        return new LinkResolution.Ambiguous(ranked[0], ranked);
    }

    private static bool IsExactCase(VaultPath path, string name) =>
        path.Name == name || path.Name == name + NoteExtension;

    private static bool HasExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1;
    }

    // Spec 05 §3 step 1: trim, '\' → '/', percent-decoding (Markdown links only), NFC.
    private static string Normalize(string target, bool markdown)
    {
        var text = target.Trim();
        if (markdown)
        {
            if (text.StartsWith('<') && text.EndsWith('>'))
            {
                text = text[1..^1].Trim();
            }

            text = Uri.UnescapeDataString(text);
        }

        return text.Replace('\\', '/').Normalize(NormalizationForm.FormC);
    }
}
