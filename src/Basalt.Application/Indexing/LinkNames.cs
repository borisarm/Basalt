using System.Text;
using Basalt.Domain.Links;

namespace Basalt.Application.Indexing;

/// <summary>Which file names a link could resolve to by name (used to find links affected by file-set changes).</summary>
internal static class LinkNames
{
    /// <summary>The link's last path segment, upper-cased, with and without ".md".</summary>
    public static HashSet<string> CandidateNames(Link link)
    {
        var target = link.Kind is LinkKind.MarkdownLink or LinkKind.MarkdownEmbed ? Uri.UnescapeDataString(link.RawTarget) : link.RawTarget;
        target = target.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC).TrimStart('<').TrimEnd('>');
        var name = target[(target.LastIndexOf('/') + 1)..].ToUpperInvariant();
        return name.Length == 0 ? [] : [name, name + ".MD"];
    }
}
