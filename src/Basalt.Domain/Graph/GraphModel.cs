using Basalt.Domain.Notes;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Graph;

public enum GraphNodeKind
{
    Note,
    Attachment,
    Unresolved,
    Tag,
}

public enum GraphEdgeKind
{
    Link,
    Embed,
    Tag,
}

/// <summary>
/// A graph node. <see cref="Id"/> is stable: the path for files, <c>unresolved:</c> + normalised target, <c>tag:</c> +
/// folded tag. <see cref="Group"/> is the index of the first colour group containing it, or -1.
/// </summary>
public sealed record GraphNode(string Id, GraphNodeKind Kind, string Label, VaultPath? Path, int Group = -1);

/// <summary>A directed edge between node indices (source links to target).</summary>
public readonly record struct GraphEdge(int Source, int Target, GraphEdgeKind Kind);

/// <summary>An immutable graph with nodes ordered by <see cref="GraphNode.Id"/> (ordinal) and deduplicated edges.</summary>
public sealed class GraphModel
{
    private Dictionary<string, int>? _index;

    public GraphModel(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public static GraphModel Empty { get; } = new([], []);

    public IReadOnlyList<GraphNode> Nodes { get; }

    public IReadOnlyList<GraphEdge> Edges { get; }

    /// <returns>The index of the node with that id, or -1.</returns>
    public int IndexOf(string id)
    {
        _index ??= Nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        return _index.GetValueOrDefault(id, -1);
    }
}

/// <summary>A vault file as the graph sees it.</summary>
public sealed record GraphFile(VaultPath Path, FileKind Kind, IReadOnlyList<Tag> Tags);

/// <summary>
/// Graph filters (spec 02 §8, F-012). <see cref="Include"/> restricts files to a set (the result of a search query
/// evaluated by the caller); <see cref="Groups"/> are colour groups (sets of paths, first match wins).
/// </summary>
public sealed record GraphFilter(
    bool ShowOrphans = true,
    bool ShowAttachments = false,
    bool ShowUnresolved = true,
    bool ShowTags = false,
    IReadOnlySet<VaultPath>? Include = null,
    IReadOnlyList<IReadOnlySet<VaultPath>>? Groups = null)
{
    public static GraphFilter Default { get; } = new();
}

public enum LocalGraphDirection
{
    Both,
    Outgoing,
    Incoming,
}
