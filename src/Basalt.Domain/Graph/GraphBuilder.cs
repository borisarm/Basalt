using Basalt.Domain.Links;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Graph;

/// <summary>
/// Builds graph models from the vault's files and links (spec 02 §8). Notes are always candidates; attachments,
/// unresolved targets and tags only when their filter is on; <see cref="GraphFilter.Include"/> restricts files; external
/// links and self-links are ignored and parallel edges collapse. The result does not depend on input order.
/// </summary>
public static class GraphBuilder
{
    public static GraphModel Build(IEnumerable<GraphFile> files, LinkGraph links, GraphFilter filter)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(filter);
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new HashSet<(string Source, string Target, GraphEdgeKind Kind)>();
        var included = files
            .Where(f => (f.Kind == FileKind.Note || (filter.ShowAttachments && f.Kind is not (FileKind.Internal or FileKind.Trash)))
                && (filter.Include is null || filter.Include.Contains(f.Path)))
            .OrderBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToList();
        var byPath = included.ToDictionary(f => f.Path);

        foreach (var file in included)
        {
            nodes[file.Path.ToString()] = new GraphNode(
                file.Path.ToString(),
                file.Kind == FileKind.Note ? GraphNodeKind.Note : GraphNodeKind.Attachment,
                file.Kind == FileKind.Note ? file.Path.NameWithoutExtension : file.Path.Name,
                file.Path,
                GroupOf(file.Path, filter.Groups));
        }

        foreach (var file in included.Where(f => f.Kind == FileKind.Note))
        {
            var source = file.Path.ToString();
            foreach (var link in links.Outgoing(file.Path))
            {
                var kind = link.Link.IsEmbed ? GraphEdgeKind.Embed : GraphEdgeKind.Link;
                if (link.Target is { } target)
                {
                    if (target != file.Path && byPath.ContainsKey(target))
                    {
                        edges.Add((source, target.ToString(), kind));
                    }
                }
                else if (filter.ShowUnresolved && !LinkResolver.IsExternal(link.Link.RawTarget))
                {
                    var id = "unresolved:" + link.UnresolvedKey;
                    nodes.TryAdd(id, new GraphNode(id, GraphNodeKind.Unresolved, link.Link.RawTarget.Trim(), null));
                    edges.Add((source, id, kind));
                }
            }

            if (filter.ShowTags)
            {
                foreach (var tag in file.Tags.Distinct())
                {
                    var id = "tag:" + tag.FoldKey;
                    nodes.TryAdd(id, new GraphNode(id, GraphNodeKind.Tag, tag.ToString(), null));
                    edges.Add((source, id, GraphEdgeKind.Tag));
                }
            }
        }

        if (!filter.ShowOrphans)
        {
            var connected = edges.SelectMany(e => new[] { e.Source, e.Target }).ToHashSet(StringComparer.Ordinal);
            foreach (var orphan in nodes.Keys.Where(id => !connected.Contains(id)).ToList())
            {
                nodes.Remove(orphan);
            }
        }

        return Freeze(nodes.Values, edges);
    }

    /// <summary>The nodes within <paramref name="depth"/> hops of <paramref name="center"/> (F-012.1), and the edges among them.</summary>
    public static GraphModel BuildLocal(IEnumerable<GraphFile> files, LinkGraph links, VaultPath center, int depth, LocalGraphDirection direction, GraphFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var graph = Build(files, links, filter with { ShowOrphans = true });
        var start = graph.IndexOf(center.ToString());
        if (start < 0)
        {
            return GraphModel.Empty;
        }

        var neighbours = Enumerable.Range(0, graph.Nodes.Count).Select(_ => new List<int>()).ToArray();
        foreach (var edge in graph.Edges)
        {
            if (direction != LocalGraphDirection.Incoming)
            {
                neighbours[edge.Source].Add(edge.Target);
            }

            if (direction != LocalGraphDirection.Outgoing)
            {
                neighbours[edge.Target].Add(edge.Source);
            }
        }

        var visited = new HashSet<int> { start };
        var frontier = new List<int> { start };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            frontier = [.. frontier.SelectMany(n => neighbours[n]).Where(visited.Add)];
        }

        return Freeze(
            visited.Select(i => graph.Nodes[i]),
            graph.Edges.Select(e => (graph.Nodes[e.Source].Id, graph.Nodes[e.Target].Id, e.Kind)));
    }

    private static int GroupOf(VaultPath path, IReadOnlyList<IReadOnlySet<VaultPath>>? groups)
    {
        for (var i = 0; i < (groups?.Count ?? 0); i++)
        {
            if (groups![i].Contains(path))
            {
                return i;
            }
        }

        return -1;
    }

    private static GraphModel Freeze(IEnumerable<GraphNode> nodes, IEnumerable<(string Source, string Target, GraphEdgeKind Kind)> edges)
    {
        var ordered = nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var index = ordered.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var frozen = edges
            .Where(e => index.ContainsKey(e.Source) && index.ContainsKey(e.Target)) // edges of dropped nodes go too
            .Select(e => new GraphEdge(index[e.Source], index[e.Target], e.Kind))
            .OrderBy(e => e.Source).ThenBy(e => e.Target).ThenBy(e => e.Kind)
            .ToList();
        return new GraphModel(ordered, frozen);
    }
}
