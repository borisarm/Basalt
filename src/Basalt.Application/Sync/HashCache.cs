using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>One <c>(path, size, mtime) → BlobSha</c> mapping of the sync hash cache (spec 06 §4).</summary>
public sealed record HashCacheEntry
{
    public HashCacheEntry(VaultPath path, long size, long modifiedUtcTicks, BlobSha sha)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (path.IsRoot)
        {
            throw new ArgumentException("The vault root is not a file.", nameof(path));
        }

        Path = path;
        Size = size;
        ModifiedUtcTicks = modifiedUtcTicks;
        Sha = sha;
    }

    public VaultPath Path { get; }

    public long Size { get; }

    public long ModifiedUtcTicks { get; }

    public BlobSha Sha { get; }
}

/// <summary>
/// Cache of blob hashes keyed by path, size and modification time, so unchanged files are not hashed again
/// (spec 06 §4). Losing it or corrupting it only costs performance, never correctness.
/// </summary>
public sealed class HashCache : IEquatable<HashCache>
{
    private readonly Dictionary<VaultPath, HashCacheEntry> _byPath;

    private HashCache(IReadOnlyList<HashCacheEntry> entries)
    {
        Entries = entries;
        _byPath = entries.ToDictionary(entry => entry.Path);
    }

    private static readonly HashCache EmptyCache = new(Array.Empty<HashCacheEntry>());

    public static HashCache Empty => EmptyCache;

    /// <summary>Entries in ordinal path order.</summary>
    public IReadOnlyList<HashCacheEntry> Entries { get; }

    public int Count => Entries.Count;

    /// <summary>Builds a cache; if the same path appears more than once the last entry wins.</summary>
    public static HashCache Create(IEnumerable<HashCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byPath = new Dictionary<VaultPath, HashCacheEntry>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            byPath[entry.Path] = entry;
        }

        var ordered = byPath.Values.ToList();
        ordered.Sort((a, b) => a.Path.CompareTo(b.Path));
        return new HashCache(ordered);
    }

    public bool TryGet(VaultPath path, long size, long modifiedUtcTicks, out BlobSha sha)
    {
        if (_byPath.TryGetValue(path, out var entry) && entry.Size == size && entry.ModifiedUtcTicks == modifiedUtcTicks)
        {
            sha = entry.Sha;
            return true;
        }

        sha = default;
        return false;
    }

    public bool Equals(HashCache? other) => other is not null && Entries.SequenceEqual(other.Entries);

    public override bool Equals(object? obj) => Equals(obj as HashCache);

    public static bool operator ==(HashCache? left, HashCache? right) => Equals(left, right);

    public static bool operator !=(HashCache? left, HashCache? right) => !Equals(left, right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}
