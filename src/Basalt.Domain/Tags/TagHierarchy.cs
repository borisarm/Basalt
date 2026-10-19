namespace Basalt.Domain.Tags;

/// <summary>A node of the tag tree. <see cref="Count"/> aggregates its own occurrences and those of nested tags.</summary>
public sealed record TagNode(string Segment, string FullName, int Count, IReadOnlyList<TagNode> Children);

/// <summary>Tree of tags with aggregated counters: <c>#a/b</c> counts for <c>a</c> and <c>a/b</c> (spec 02 §6).</summary>
public sealed class TagHierarchy
{
    private TagHierarchy(IReadOnlyList<TagNode> roots) => Roots = roots;

    public IReadOnlyList<TagNode> Roots { get; }

    /// <summary>Builds the tree from tag occurrences; the first spelling seen of each tag is the displayed one.</summary>
    public static TagHierarchy Build(IEnumerable<Tag> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var root = new MutableNode(string.Empty, string.Empty);
        foreach (var tag in occurrences)
        {
            var node = root;
            foreach (var prefix in tag.SelfAndAncestors())
            {
                node = node.Child(prefix);
                node.Count++;
            }
        }

        return new TagHierarchy(root.Freeze().Children);
    }

    public TagNode? Find(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var level = Roots;
        TagNode? found = null;
        foreach (var prefix in tag.SelfAndAncestors())
        {
            found = level.FirstOrDefault(n => string.Equals(n.FullName, prefix.Name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return null;
            }

            level = found.Children;
        }

        return found;
    }

    private sealed class MutableNode(string segment, string fullName)
    {
        private readonly Dictionary<string, MutableNode> _children = new(StringComparer.Ordinal);

        public int Count { get; set; }

        public MutableNode Child(Tag prefix)
        {
            if (!_children.TryGetValue(prefix.FoldKey, out var child))
            {
                // Keep the spelling of the first occurrence for the parent part already created.
                var parentName = fullName.Length == 0 ? string.Empty : fullName + "/";
                var segmentName = prefix.Segments[^1];
                child = new MutableNode(segmentName, parentName + segmentName);
                _children[prefix.FoldKey] = child;
            }

            return child;
        }

        public TagNode Freeze() => new(
            segment,
            fullName,
            Count,
            [.. _children.Values
                .OrderBy(c => c.Segment(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Segment(), StringComparer.Ordinal)
                .Select(c => c.Freeze())]);

        private string Segment() => segment;
    }
}
