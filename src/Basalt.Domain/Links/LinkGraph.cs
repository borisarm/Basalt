using System.Collections.Immutable;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>A link of a source note with its resolution (<see cref="Target"/> null when unresolved).</summary>
public sealed record ResolvedLink(VaultPath Source, Link Link, VaultPath? Target)
{
    /// <summary>Case-insensitive key of an unresolved target (the normalised raw target).</summary>
    public string UnresolvedKey => Link.RawTarget.Trim().Replace('\\', '/').ToUpperInvariant();
}

/// <summary>
/// Immutable projection of links (spec 02 §5.3): source → links and target → incoming links, including unresolved
/// targets. Updated per source, so maintaining it is proportional to what changed.
/// </summary>
public sealed class LinkGraph
{
    private readonly ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>> _outgoing;
    private readonly ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>> _incoming;
    private readonly ImmutableDictionary<string, ImmutableArray<ResolvedLink>> _unresolved;

    private LinkGraph(
        ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>> outgoing,
        ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>> incoming,
        ImmutableDictionary<string, ImmutableArray<ResolvedLink>> unresolved)
    {
        _outgoing = outgoing;
        _incoming = incoming;
        _unresolved = unresolved;
    }

    public static LinkGraph Empty { get; } = new(
        ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>>.Empty,
        ImmutableDictionary<VaultPath, ImmutableArray<ResolvedLink>>.Empty,
        ImmutableDictionary.Create<string, ImmutableArray<ResolvedLink>>(StringComparer.Ordinal));

    public IEnumerable<VaultPath> Sources => _outgoing.Keys;

    /// <summary>Unresolved target keys with the links pointing at them.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<ResolvedLink>> Unresolved => _unresolved;

    public IReadOnlyList<ResolvedLink> Outgoing(VaultPath source) => _outgoing.GetValueOrDefault(source, []);

    public IReadOnlyList<ResolvedLink> Incoming(VaultPath target) => _incoming.GetValueOrDefault(target, []);

    /// <summary>Links whose unresolved target equals <paramref name="rawTarget"/> ignoring case.</summary>
    public IReadOnlyList<ResolvedLink> IncomingUnresolved(string rawTarget)
    {
        ArgumentNullException.ThrowIfNull(rawTarget);
        return _unresolved.GetValueOrDefault(rawTarget.Trim().Replace('\\', '/').ToUpperInvariant(), []);
    }

    /// <summary>Replaces all outgoing links of <paramref name="source"/>.</summary>
    public LinkGraph WithSource(VaultPath source, IEnumerable<ResolvedLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var withoutOld = WithoutSource(source);
        var list = links.ToImmutableArray();
        if (list.IsEmpty)
        {
            return withoutOld;
        }

        var incoming = withoutOld._incoming.ToBuilder();
        var unresolved = withoutOld._unresolved.ToBuilder();
        foreach (var link in list)
        {
            if (link.Target is { } target)
            {
                incoming[target] = incoming.GetValueOrDefault(target, []).Add(link);
            }
            else
            {
                unresolved[link.UnresolvedKey] = unresolved.GetValueOrDefault(link.UnresolvedKey, []).Add(link);
            }
        }

        return new LinkGraph(withoutOld._outgoing.SetItem(source, list), incoming.ToImmutable(), unresolved.ToImmutable());
    }

    public LinkGraph WithoutSource(VaultPath source)
    {
        if (!_outgoing.TryGetValue(source, out var old))
        {
            return this;
        }

        var incoming = _incoming.ToBuilder();
        var unresolved = _unresolved.ToBuilder();
        foreach (var link in old)
        {
            if (link.Target is { } target)
            {
                Remove(incoming, target, link);
            }
            else
            {
                Remove(unresolved, link.UnresolvedKey, link);
            }
        }

        return new LinkGraph(_outgoing.Remove(source), incoming.ToImmutable(), unresolved.ToImmutable());
    }

    private static void Remove<TKey>(ImmutableDictionary<TKey, ImmutableArray<ResolvedLink>>.Builder map, TKey key, ResolvedLink link)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var links))
        {
            return;
        }

        var remaining = links.Remove(link, ReferenceEqualityComparer.Instance);
        if (remaining.IsEmpty)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = remaining;
        }
    }
}
