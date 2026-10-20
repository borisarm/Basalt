namespace Basalt.Domain.Notes;

/// <summary>A heading of the outline with the headings nested under it.</summary>
public sealed record OutlineNode(Heading Heading, IReadOnlyList<OutlineNode> Children);

/// <summary>The outline (F-011): headings nested under the closest preceding heading of a lower level.</summary>
public static class OutlineBuilder
{
    public static IReadOnlyList<OutlineNode> Build(IEnumerable<Heading> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        var roots = new List<Builder>();
        var open = new Stack<Builder>();
        foreach (var heading in headings)
        {
            while (open.Count > 0 && open.Peek().Heading.Level >= heading.Level)
            {
                open.Pop();
            }

            var node = new Builder(heading);
            (open.Count == 0 ? roots : open.Peek().Children).Add(node);
            open.Push(node);
        }

        return [.. roots.Select(r => r.Freeze())];
    }

    private sealed class Builder(Heading heading)
    {
        public Heading Heading { get; } = heading;

        public List<Builder> Children { get; } = [];

        public OutlineNode Freeze() => new(Heading, [.. Children.Select(c => c.Freeze())]);
    }
}
