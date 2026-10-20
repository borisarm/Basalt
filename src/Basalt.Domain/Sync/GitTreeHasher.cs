using System.Security.Cryptography;
using System.Text;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>Where a tree entry points to (spec 06 §9.4). Executables are stored as files and symlinks are ignored.</summary>
public enum GitTreeEntryKind
{
    File,
    Directory,
}

/// <summary>One entry of a Git tree: a name, a mode (implicit in the kind) and the object it points to.</summary>
public readonly record struct GitTreeEntry
{
    private GitTreeEntry(string name, GitTreeEntryKind kind, string sha)
    {
        Name = name;
        Kind = kind;
        Sha = sha;
    }

    public string Name { get; }

    public GitTreeEntryKind Kind { get; }

    /// <summary>The 40-character lower-case SHA-1 of the blob or subtree.</summary>
    public string Sha { get; }

    public static GitTreeEntry File(string name, BlobSha sha) => Create(name, GitTreeEntryKind.File, sha.ToString());

    public static GitTreeEntry Directory(string name, TreeSha sha) => Create(name, GitTreeEntryKind.Directory, sha.ToString());

    internal string Mode => Kind == GitTreeEntryKind.Directory ? "40000" : "100644";

    private static GitTreeEntry Create(string name, GitTreeEntryKind kind, string sha)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new ArgumentException($"'{name}' is not a valid tree entry name.", nameof(name));
        }

        return new GitTreeEntry(name, kind, sha);
    }
}

/// <summary>
/// Computes the SHA-1 of Git tree objects (spec 06 §9.4): <c>tree {length}\0</c> followed by
/// <c>{mode} {name}\0{20 sha bytes}</c> per entry, sorted as Git does (directories compare as if their name ended in
/// <c>/</c>).
/// </summary>
public static class GitTreeHasher
{
    public static TreeSha Compute(IEnumerable<GitTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sliced = entries.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sliced)
        {
            if (!seen.Add(entry.Name))
            {
                throw new ArgumentException($"Duplicate tree entry '{entry.Name}'.", nameof(entries));
            }
        }

        return FromEntries(sliced);
    }

    /// <summary>Hashes a flat manifest (vault-relative paths to blobs), creating the nested trees it needs.</summary>
    public static TreeSha Compute(IReadOnlyDictionary<VaultPath, BlobSha> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = new Node();
        foreach (var (path, sha) in files)
        {
            if (path.IsRoot)
            {
                throw new ArgumentException("The vault root is not a file.", nameof(files));
            }

            var node = root;
            var segments = path.ToString().Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node.Folder(segments[i]);
            }

            var name = segments[^1];
            if (node.Files.ContainsKey(name))
            {
                throw new ArgumentException($"Duplicate file '{path}'.", nameof(files));
            }

            if (node.Folders.ContainsKey(name))
            {
                throw new ArgumentException($"'{path}' is both a file and a folder.", nameof(files));
            }

            node.Files.Add(name, sha);
        }

        return root.Compute();
    }

    private static TreeSha FromEntries(List<GitTreeEntry> entries)
    {
        entries.Sort((a, b) => CompareUtf8(SortName(a), SortName(b)));
        var length = entries.Sum(e => Encoding.UTF8.GetByteCount(e.Mode) + 1 + Encoding.UTF8.GetByteCount(e.Name) + 1 + 20);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("tree " + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.ASCII.GetBytes(entry.Mode + " "));
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Name));
            hash.AppendData([(byte)0]);
            hash.AppendData(Convert.FromHexString(entry.Sha));
        }

        return TreeSha.FromBytes(hash.GetHashAndReset());
    }

    private static string SortName(GitTreeEntry entry) =>
        entry.Kind == GitTreeEntryKind.Directory ? entry.Name + "/" : entry.Name;

    private static int CompareUtf8(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var comparison = a[i].CompareTo(b[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private sealed class Node
    {
        public Dictionary<string, BlobSha> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Node> Folders { get; } = new(StringComparer.Ordinal);

        public Node Folder(string name)
        {
            if (Files.ContainsKey(name))
            {
                throw new ArgumentException($"'{name}' is both a file and a folder.");
            }

            if (!Folders.TryGetValue(name, out var child))
            {
                child = new Node();
                Folders.Add(name, child);
            }

            return child;
        }

        public TreeSha Compute()
        {
            var entries = new List<GitTreeEntry>(Files.Count + Folders.Count);
            entries.AddRange(Files.Select(file => GitTreeEntry.File(file.Key, file.Value)));
            entries.AddRange(Folders.Select(folder => GitTreeEntry.Directory(folder.Key, folder.Value.Compute())));
            return FromEntries(entries);
        }
    }
}
