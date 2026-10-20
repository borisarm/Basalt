using System.Collections.Immutable;
using System.Text;
using Basalt.Application.Ports.Index;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;

namespace Basalt.Application.Indexing;

/// <summary>
/// In-memory metadata of a vault (spec 08 §3): files, lookups by name/path/alias/tag and the <see cref="LinkGraph"/>.
/// Readers get immutable snapshots without locking; writers build a new snapshot and swap the reference.
/// </summary>
public interface IMetadataCache
{
    MetadataSnapshot Current { get; }
}

/// <summary>An immutable state of the cache. Implements <see cref="IVaultFileCatalog"/> for link resolution.</summary>
public sealed class MetadataSnapshot : IVaultFileCatalog
{
    private static readonly ImmutableDictionary<string, ImmutableHashSet<VaultPath>> EmptyIndex =
        ImmutableDictionary.Create<string, ImmutableHashSet<VaultPath>>(StringComparer.Ordinal);

    internal MetadataSnapshot(
        ImmutableDictionary<VaultPath, IndexedNoteSummary> files,
        ImmutableDictionary<string, ImmutableHashSet<VaultPath>> byName,
        ImmutableDictionary<string, ImmutableHashSet<VaultPath>> byPath,
        ImmutableDictionary<string, ImmutableHashSet<VaultPath>> byAlias,
        ImmutableDictionary<string, ImmutableHashSet<VaultPath>> byTag,
        LinkGraph graph)
    {
        Files = files;
        ByName = byName;
        ByPath = byPath;
        ByAlias = byAlias;
        ByTag = byTag;
        Graph = graph;
    }

    public static MetadataSnapshot Empty { get; } = new(ImmutableDictionary<VaultPath, IndexedNoteSummary>.Empty, EmptyIndex, EmptyIndex, EmptyIndex, EmptyIndex, LinkGraph.Empty);

    public ImmutableDictionary<VaultPath, IndexedNoteSummary> Files { get; }

    public LinkGraph Graph { get; }

    /// <summary>Upper-cased file name (with extension) → files.</summary>
    internal ImmutableDictionary<string, ImmutableHashSet<VaultPath>> ByName { get; }

    internal ImmutableDictionary<string, ImmutableHashSet<VaultPath>> ByPath { get; }

    /// <summary>Upper-cased alias → notes.</summary>
    public ImmutableDictionary<string, ImmutableHashSet<VaultPath>> ByAlias { get; }

    /// <summary>Tag fold key → notes (only the exact tag; nested tags are separate keys).</summary>
    public ImmutableDictionary<string, ImmutableHashSet<VaultPath>> ByTag { get; }

    public IReadOnlyCollection<VaultPath> FindByName(string fileName) =>
        ByName.GetValueOrDefault(fileName.ToUpperInvariant(), []);

    public IReadOnlyCollection<VaultPath> FindByPath(VaultPath path) =>
        ByPath.GetValueOrDefault(path.CaseFoldKey, []);
}

/// <summary>
/// Maintains the snapshot. When the set of files changes, only the sources whose links could resolve differently are
/// re-resolved: those with a link whose last segment names the added/removed file (property-tested against a full
/// rebuild).
/// </summary>
public sealed class MetadataCache : IMetadataCache
{
    private readonly Lock _gate = new();

    public MetadataSnapshot Current { get; private set; } = MetadataSnapshot.Empty;

    /// <summary>Builds a snapshot from scratch: all files first, then every link resolved once.</summary>
    public static MetadataSnapshot Build(IEnumerable<IndexedNoteSummary> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var state = files.Aggregate(MetadataSnapshot.Empty, AddFile);
        var graph = LinkGraph.Empty;
        foreach (var source in state.Files.Values)
        {
            graph = graph.WithSource(source.Path, Resolve(source, state));
        }

        return With(state, graph);
    }

    public void Load(IEnumerable<IndexedNoteSummary> files)
    {
        var snapshot = Build(files);
        lock (_gate)
        {
            Current = snapshot;
        }
    }

    /// <summary>Adds or replaces files and returns the paths whose metadata or link resolution changed.</summary>
    public IReadOnlySet<VaultPath> Apply(IReadOnlyList<IndexedNoteSummary> upserts, IReadOnlyList<VaultPath> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);
        lock (_gate)
        {
            var state = Current;
            var changedNames = new HashSet<string>(StringComparer.Ordinal);
            var touched = new HashSet<VaultPath>();
            foreach (var path in removals.Where(state.Files.ContainsKey))
            {
                changedNames.Add(path.Name.ToUpperInvariant());
                state = RemoveFile(state, path);
                touched.Add(path);
            }

            foreach (var file in upserts)
            {
                if (!state.Files.ContainsKey(file.Path))
                {
                    changedNames.Add(file.Path.Name.ToUpperInvariant());
                }
                else
                {
                    state = RemoveFile(state, file.Path);
                }

                state = AddFile(state, file);
                touched.Add(file.Path);
            }

            // Re-resolve the changed sources and every source with a link that may now resolve differently.
            var graph = state.Graph;
            foreach (var removed in removals)
            {
                graph = graph.WithoutSource(removed);
            }

            var sources = new HashSet<VaultPath>(upserts.Select(u => u.Path));
            foreach (var source in state.Files.Values)
            {
                if (changedNames.Count > 0 && source.Metadata.AllLinks.Any(l => CandidateNames(l).Overlaps(changedNames)))
                {
                    sources.Add(source.Path);
                }
            }

            foreach (var path in sources)
            {
                var before = graph.Outgoing(path);
                var after = Resolve(state.Files[path], state);
                if (!before.Select(b => b.Target).SequenceEqual(after.Select(a => a.Target)))
                {
                    touched.Add(path);
                }

                graph = graph.WithSource(path, after);
            }

            Current = With(state, graph);
            return touched;
        }
    }

    public IReadOnlySet<VaultPath> Rename(VaultPath from, VaultPath to)
    {
        var file = Current.Files.GetValueOrDefault(from);
        if (file is null)
        {
            return new HashSet<VaultPath>();
        }

        var touched = new HashSet<VaultPath>(Apply([file with { Path = to, Kind = FileClassifier.Classify(to) }], [from])) { from, to };
        return touched;
    }

    private static List<ResolvedLink> Resolve(IndexedNoteSummary source, MetadataSnapshot catalog) =>
        [.. source.Metadata.AllLinks.Select(link => new ResolvedLink(source.Path, link, LinkResolver.Resolve(link, source.Path, catalog).TargetPath))];

    /// <summary>File names (upper case) a link could resolve to by name: its last segment with and without ".md".</summary>
    private static HashSet<string> CandidateNames(Link link)
    {
        var target = link.Kind is LinkKind.MarkdownLink or LinkKind.MarkdownEmbed ? Uri.UnescapeDataString(link.RawTarget) : link.RawTarget;
        target = target.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC).TrimStart('<').TrimEnd('>');
        var name = target[(target.LastIndexOf('/') + 1)..].ToUpperInvariant();
        return name.Length == 0 ? [] : [name, name + ".MD"];
    }

    private static MetadataSnapshot AddFile(MetadataSnapshot state, IndexedNoteSummary file)
    {
        var aliases = state.ByAlias;
        foreach (var alias in file.Metadata.Aliases)
        {
            aliases = AddTo(aliases, alias.ToUpperInvariant(), file.Path);
        }

        var tags = state.ByTag;
        foreach (var tag in file.Metadata.Tags)
        {
            tags = AddTo(tags, tag.Tag.FoldKey, file.Path);
        }

        return new MetadataSnapshot(
            state.Files.SetItem(file.Path, file),
            AddTo(state.ByName, file.Path.Name.ToUpperInvariant(), file.Path),
            AddTo(state.ByPath, file.Path.CaseFoldKey, file.Path),
            aliases,
            tags,
            state.Graph);
    }

    private static MetadataSnapshot RemoveFile(MetadataSnapshot state, VaultPath path)
    {
        var file = state.Files[path];
        var aliases = state.ByAlias;
        foreach (var alias in file.Metadata.Aliases)
        {
            aliases = RemoveFrom(aliases, alias.ToUpperInvariant(), path);
        }

        var tags = state.ByTag;
        foreach (var tag in file.Metadata.Tags)
        {
            tags = RemoveFrom(tags, tag.Tag.FoldKey, path);
        }

        return new MetadataSnapshot(
            state.Files.Remove(path),
            RemoveFrom(state.ByName, path.Name.ToUpperInvariant(), path),
            RemoveFrom(state.ByPath, path.CaseFoldKey, path),
            aliases,
            tags,
            state.Graph);
    }

    private static MetadataSnapshot With(MetadataSnapshot state, LinkGraph graph) =>
        new(state.Files, state.ByName, state.ByPath, state.ByAlias, state.ByTag, graph);

    private static ImmutableDictionary<string, ImmutableHashSet<VaultPath>> AddTo(ImmutableDictionary<string, ImmutableHashSet<VaultPath>> map, string key, VaultPath path) =>
        map.SetItem(key, map.GetValueOrDefault(key, []).Add(path));

    private static ImmutableDictionary<string, ImmutableHashSet<VaultPath>> RemoveFrom(ImmutableDictionary<string, ImmutableHashSet<VaultPath>> map, string key, VaultPath path)
    {
        if (!map.TryGetValue(key, out var set))
        {
            return map;
        }

        var remaining = set.Remove(path);
        return remaining.IsEmpty ? map.Remove(key) : map.SetItem(key, remaining);
    }
}
