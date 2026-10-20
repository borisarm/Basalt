using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>One file of a vault as seen by synchronisation (spec 02 §11). Executables use the same mode as files.</summary>
public readonly record struct ManifestEntry
{
    /// <summary>The only represented Git mode: regular non-executable file.</summary>
    public const string FileMode = "100644";

    public ManifestEntry(VaultPath path, BlobSha sha, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (path.IsRoot)
        {
            throw new ArgumentException("The vault root is not a file.", nameof(path));
        }

        Path = path;
        Sha = sha;
        Size = size;
    }

    public VaultPath Path { get; }

    public BlobSha Sha { get; }

    public long Size { get; }

    public string Mode { get; } = FileMode;
}

/// <summary>
/// Immutable set of <see cref="ManifestEntry"/> keyed by path (spec 02 §11). Two paths that only differ in case are a
/// collision (<c>path.case_collision</c>), because Windows and macOS cannot materialise both.
/// </summary>
public sealed class Manifest : IEquatable<Manifest>
{
    private readonly Dictionary<VaultPath, ManifestEntry> _byPath;

    private Manifest(IReadOnlyList<ManifestEntry> entries)
    {
        Entries = entries;
        _byPath = entries.ToDictionary(entry => entry.Path);
    }

    public static Manifest Empty { get; } = new([]);

    /// <summary>Entries in ordinal path order, so that hashing and commit summaries are deterministic.</summary>
    public IReadOnlyList<ManifestEntry> Entries { get; }

    public int Count => Entries.Count;

    public bool IsEmpty => Count == 0;

    public ManifestEntry? Get(VaultPath path) => _byPath.TryGetValue(path, out var entry) ? entry : null;

    public bool Contains(VaultPath path) => _byPath.ContainsKey(path);

    /// <summary>Builds a manifest, rejecting duplicate paths and case-fold collisions.</summary>
    public static Result<Manifest> Create(IEnumerable<ManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byCaseFold = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var result = new List<ManifestEntry>();
        foreach (var entry in entries)
        {
            if (entry.Path.IsRoot)
            {
                throw new ArgumentException("The vault root is not a file.", nameof(entries));
            }

            if (byCaseFold.TryGetValue(entry.Path.CaseFoldKey, out var clash))
            {
                return new Error(
                    DomainErrors.PathCaseCollision,
                    $"'{entry.Path}' collides with '{clash.Path}' on case-insensitive file systems.",
                    new Dictionary<string, object?> { ["path"] = entry.Path.ToString(), ["other"] = clash.Path.ToString() });
            }

            byCaseFold.Add(entry.Path.CaseFoldKey, entry);
            result.Add(entry);
        }

        result.Sort((a, b) => a.Path.CompareTo(b.Path));
        return new Manifest(result);
    }

    /// <summary>Adds or replaces an entry; replacing the same path is not a collision.</summary>
    public Result<Manifest> Add(ManifestEntry entry)
    {
        var entries = Entries.Where(existing => existing.Path != entry.Path).ToList();
        entries.Add(entry);
        return Create(entries);
    }

    /// <summary>Removes a path; removing an absent path returns the same manifest.</summary>
    public Manifest Remove(VaultPath path)
    {
        if (!Contains(path))
        {
            return this;
        }

        return Create(Entries.Where(entry => entry.Path != path)).Value;
    }

    public bool Equals(Manifest? other) => other is not null && Entries.SequenceEqual(other.Entries);

    public override bool Equals(object? obj) => Equals(obj as Manifest);

    public static bool operator ==(Manifest? left, Manifest? right) => Equals(left, right);

    public static bool operator !=(Manifest? left, Manifest? right) => !Equals(left, right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"Manifest({Count})";
}

/// <summary>The last state both sides agreed on (spec 06 §2 and §10).</summary>
public sealed record SyncBase
{
    public SyncBase(CommitSha? lastSyncedCommit, TreeSha? lastRemoteTree, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LastSyncedCommit = lastSyncedCommit;
        LastRemoteTree = lastRemoteTree;
        Manifest = manifest;
    }

    public static SyncBase Empty { get; } = new(null, null, Domain.Sync.Manifest.Empty);

    public CommitSha? LastSyncedCommit { get; }

    public TreeSha? LastRemoteTree { get; }

    public Manifest Manifest { get; }
}

/// <summary>A path that disappeared at <see cref="From"/> and appeared at <see cref="To"/> with the same blob.</summary>
public sealed record ManifestRename(VaultPath From, VaultPath To, BlobSha Sha);

/// <summary>
/// Difference between two manifests (spec 02 §11). Renames are informative: the paths still count as deleted and
/// added because Git has no rename operation.
/// </summary>
public sealed class ChangeSet
{
    private ChangeSet(
        IReadOnlyList<ManifestEntry> added,
        IReadOnlyList<ManifestEntry> modified,
        IReadOnlyList<VaultPath> deleted,
        IReadOnlyList<ManifestRename> renamed)
    {
        Added = added;
        Modified = modified;
        Deleted = deleted;
        Renamed = renamed;
    }

    public IReadOnlyList<ManifestEntry> Added { get; }

    public IReadOnlyList<ManifestEntry> Modified { get; }

    public IReadOnlyList<VaultPath> Deleted { get; }

    public IReadOnlyList<ManifestRename> Renamed { get; }

    public int Count => Added.Count + Modified.Count + Deleted.Count;

    public bool IsEmpty => Count == 0;

    public static ChangeSet Between(Manifest before, Manifest after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var added = after.Entries.Where(entry => !before.Contains(entry.Path)).ToList();
        var modified = after.Entries
            .Where(entry => before.Get(entry.Path) is { } previous && previous.Sha != entry.Sha)
            .ToList();
        var deleted = before.Entries
            .Where(entry => !after.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToList();

        return new ChangeSet(added, modified, deleted, Renames(before, after, added, deleted));
    }

    private static List<ManifestRename> Renames(
        Manifest before,
        Manifest after,
        IReadOnlyList<ManifestEntry> added,
        IReadOnlyList<VaultPath> deleted)
    {
        var removed = deleted
            .GroupBy(path => before.Get(path)!.Value.Sha)
            .ToDictionary(group => group.Key, group => group.OrderBy(path => path).ToList());
        var created = added
            .GroupBy(entry => entry.Sha)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Path).Select(entry => entry.Path).ToList());

        var renames = new List<ManifestRename>();
        foreach (var (sha, from) in removed)
        {
            if (!created.TryGetValue(sha, out var to))
            {
                continue;
            }

            for (var i = 0; i < Math.Min(from.Count, to.Count); i++)
            {
                renames.Add(new ManifestRename(from[i], to[i], sha));
            }
        }

        return [.. renames.OrderBy(rename => rename.From)];
    }
}
