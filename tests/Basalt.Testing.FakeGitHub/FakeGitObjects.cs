using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.FakeGitHub;

/// <summary>A repository stored by <see cref="FakeGitHubServer"/> (spec 06 §14).</summary>
public sealed class FakeRepository
{
    private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeGitObject> _objects = new(StringComparer.Ordinal);

    internal FakeRepository(int id, string owner, string name, bool isPrivate, bool allowPush)
    {
        Id = id;
        Owner = owner;
        Name = name;
        Private = isPrivate;
        AllowPush = allowPush;
    }

    public int Id { get; }

    public string Owner { get; internal set; }

    public string Name { get; internal set; }

    public string DefaultBranch { get; set; } = "main";

    public bool Private { get; set; }

    public bool Archived { get; set; }

    public bool AllowPush { get; set; }

    public bool IsEmpty
    {
        get
        {
            lock (Gate)
            {
                return _refs.Count == 0;
            }
        }
    }

    internal object Gate { get; } = new();

    internal Dictionary<string, string> Refs => _refs;

    internal Dictionary<string, FakeGitObject> Objects => _objects;

    /// <summary>Head commit of a branch, or null when the branch does not exist.</summary>
    public string? Head(string? branch = null)
    {
        lock (Gate)
        {
            return _refs.TryGetValue(branch ?? DefaultBranch, out var sha) ? sha : null;
        }
    }

    /// <summary>Branch names with their head SHA.</summary>
    public IReadOnlyDictionary<string, string> Branches
    {
        get
        {
            lock (Gate)
            {
                return new Dictionary<string, string>(_refs, StringComparer.Ordinal);
            }
        }
    }
}

internal abstract record FakeGitObject(string Sha);

internal sealed record FakeGitBlob(string Sha, byte[] Content) : FakeGitObject(Sha);

internal sealed record FakeGitTreeEntry(string Mode, string Type, string Name, string Sha);

internal sealed record FakeGitTree(string Sha, IReadOnlyList<FakeGitTreeEntry> Entries) : FakeGitObject(Sha);

internal sealed record FakeGitCommit(
    string Sha,
    string Tree,
    IReadOnlyList<string> Parents,
    string Message,
    GitSignature Author,
    GitSignature Committer) : FakeGitObject(Sha);

internal sealed record FakeTreeLine(string Path, string Mode, string Type, string Sha, long? Size);

internal static class FakeGitStore
{
    public static BlobSha PutBlob(FakeRepository repository, ReadOnlySpan<byte> content)
    {
        var sha = GitBlobHasher.Compute(content);
        lock (repository.Gate)
        {
            repository.Objects.TryAdd(sha.ToString(), new FakeGitBlob(sha.ToString(), content.ToArray()));
        }

        return sha;
    }

    public static TreeSha PutFiles(FakeRepository repository, IReadOnlyDictionary<string, byte[]> files)
    {
        lock (repository.Gate)
        {
            return PutFilesLocked(repository, files);
        }
    }

    public static CommitSha CreateCommit(
        FakeRepository repository,
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyList<CommitSha> parents,
        string message,
        GitSignature author,
        GitSignature committer)
    {
        lock (repository.Gate)
        {
            var tree = PutFilesLocked(repository, files);
            return PutCommitLocked(repository, tree, parents, message, author, committer);
        }
    }

    public static CommitSha CreateCommit(
        FakeRepository repository,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        string message,
        GitSignature author,
        GitSignature committer)
    {
        lock (repository.Gate)
        {
            return PutCommitLocked(repository, tree, parents, message, author, committer);
        }
    }

    public static IReadOnlyDictionary<string, byte[]> Flatten(FakeRepository repository, string treeSha)
    {
        lock (repository.Gate)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            FlattenLocked(repository, treeSha, string.Empty, files);
            return files;
        }
    }

    public static IReadOnlyList<FakeTreeLine> ListTree(
        FakeRepository repository,
        string treeSha,
        bool recursive,
        int maxEntries,
        out bool truncated)
    {
        lock (repository.Gate)
        {
            var lines = new List<FakeTreeLine>();
            truncated = ListTreeLocked(repository, treeSha, string.Empty, recursive, maxEntries, lines);
            return lines;
        }
    }

    public static FakeGitCommit? FindCommit(FakeRepository repository, string sha)
    {
        lock (repository.Gate)
        {
            return repository.Objects.TryGetValue(sha, out var value) ? value as FakeGitCommit : null;
        }
    }

    public static FakeGitTree? FindTree(FakeRepository repository, string sha)
    {
        lock (repository.Gate)
        {
            return repository.Objects.TryGetValue(sha, out var value) ? value as FakeGitTree : null;
        }
    }

    public static byte[]? FindBlob(FakeRepository repository, string sha)
    {
        lock (repository.Gate)
        {
            return repository.Objects.TryGetValue(sha, out var value) ? (value as FakeGitBlob)?.Content : null;
        }
    }

    public static string? ResolveCommit(FakeRepository repository, string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            reference = repository.DefaultBranch;
        }

        lock (repository.Gate)
        {
            if (repository.Objects.ContainsKey(reference))
            {
                return reference;
            }

            var name = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
            if (repository.Refs.TryGetValue(name, out var sha))
            {
                return sha;
            }

            return null;
        }
    }

    public static TreeSha? TreeOf(FakeRepository repository, string commitSha)
    {
        var commit = FindCommit(repository, commitSha);
        return commit is null ? null : TreeSha.Parse(commit.Tree).Value;
    }

    public static byte[]? ReadFile(FakeRepository repository, string commitSha, string path)
    {
        lock (repository.Gate)
        {
            if (!repository.Objects.TryGetValue(commitSha, out var value) || value is not FakeGitCommit commit)
            {
                return null;
            }

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            FlattenLocked(repository, commit.Tree, string.Empty, files);
            return files.TryGetValue(NormalizePath(path), out var content) ? content : null;
        }
    }

    public static bool IsAncestor(FakeRepository repository, string ancestor, string descendant)
    {
        lock (repository.Gate)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(descendant);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == ancestor)
                {
                    return true;
                }

                if (!seen.Add(current) || !repository.Objects.TryGetValue(current, out var value) || value is not FakeGitCommit commit)
                {
                    continue;
                }

                foreach (var parent in commit.Parents)
                {
                    pending.Push(parent);
                }
            }

            return false;
        }
    }

    public static IReadOnlyList<FakeGitCommit> History(FakeRepository repository, string head, string? path)
    {
        lock (repository.Gate)
        {
            var history = new List<FakeGitCommit>();
            var current = head;
            string? previousTree = null;
            while (current is not null && repository.Objects.TryGetValue(current, out var value) && value is FakeGitCommit commit)
            {
                if (path is null || Differs(repository, previousTree, commit.Tree, NormalizePath(path)))
                {
                    history.Add(commit);
                }

                previousTree = commit.Tree;
                current = commit.Parents.Count > 0 ? commit.Parents[0] : null;
            }

            return history;
        }
    }

    public static void SetRef(FakeRepository repository, string branch, string sha)
    {
        lock (repository.Gate)
        {
            repository.Refs[branch] = sha;
        }
    }

    public static bool RemoveRef(FakeRepository repository, string branch)
    {
        lock (repository.Gate)
        {
            return repository.Refs.Remove(branch);
        }
    }

    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A path cannot be empty.", nameof(path));
        }

        return VaultPath.Create(normalized).Value.ToString();
    }

    private static TreeSha PutFilesLocked(FakeRepository repository, IReadOnlyDictionary<string, byte[]> files)
    {
        var root = new TreeBuilder();
        foreach (var (path, content) in files)
        {
            var segments = NormalizePath(path).Split('/');
            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node.Folder(segments[i]);
            }

            node.AddFile(segments[^1], PutBlobLocked(repository, content));
        }

        return PutNodeLocked(repository, root);
    }

    private static BlobSha PutBlobLocked(FakeRepository repository, byte[] content)
    {
        var sha = GitBlobHasher.Compute(content);
        repository.Objects.TryAdd(sha.ToString(), new FakeGitBlob(sha.ToString(), content));
        return sha;
    }

    private static TreeSha PutNodeLocked(FakeRepository repository, TreeBuilder node)
    {
        var entries = new List<FakeGitTreeEntry>();
        var hashEntries = new List<GitTreeEntry>();
        foreach (var (name, blob) in node.Files)
        {
            entries.Add(new FakeGitTreeEntry("100644", "blob", name, blob.ToString()));
            hashEntries.Add(GitTreeEntry.File(name, blob));
        }

        foreach (var (name, child) in node.Folders)
        {
            var childSha = PutNodeLocked(repository, child);
            entries.Add(new FakeGitTreeEntry("040000", "tree", name, childSha.ToString()));
            hashEntries.Add(GitTreeEntry.Directory(name, childSha));
        }

        var sha = GitTreeHasher.Compute(hashEntries);
        repository.Objects[sha.ToString()] = new FakeGitTree(sha.ToString(), entries);
        return sha;
    }

    private static CommitSha PutCommitLocked(
        FakeRepository repository,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        string message,
        GitSignature author,
        GitSignature committer)
    {
        var sha = GitCommitHasher.Compute(tree, parents, author, committer, message);
        repository.Objects[sha.ToString()] = new FakeGitCommit(
            sha.ToString(), tree.ToString(), parents.Select(parent => parent.ToString()).ToArray(), message, author, committer);
        return sha;
    }

    private static void FlattenLocked(FakeRepository repository, string treeSha, string prefix, Dictionary<string, byte[]> files)
    {
        if (!repository.Objects.TryGetValue(treeSha, out var value) || value is not FakeGitTree tree)
        {
            throw new InvalidOperationException($"Tree {treeSha} does not exist.");
        }

        foreach (var entry in tree.Entries)
        {
            var path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            if (entry.Type == "tree")
            {
                FlattenLocked(repository, entry.Sha, path, files);
            }
            else if (repository.Objects.TryGetValue(entry.Sha, out var blob) && blob is FakeGitBlob file)
            {
                files[path] = file.Content;
            }
            else
            {
                throw new InvalidOperationException($"Blob {entry.Sha} does not exist.");
            }
        }
    }

    private static bool ListTreeLocked(
        FakeRepository repository,
        string treeSha,
        string prefix,
        bool recursive,
        int maxEntries,
        List<FakeTreeLine> lines)
    {
        if (!repository.Objects.TryGetValue(treeSha, out var value) || value is not FakeGitTree tree)
        {
            throw new InvalidOperationException($"Tree {treeSha} does not exist.");
        }

        foreach (var entry in tree.Entries)
        {
            if (lines.Count >= maxEntries)
            {
                return true;
            }

            var path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            long? size = entry.Type == "blob" && repository.Objects.TryGetValue(entry.Sha, out var blob) && blob is FakeGitBlob file
                ? file.Content.Length
                : null;
            lines.Add(new FakeTreeLine(path, entry.Mode, entry.Type, entry.Sha, size));
            if (recursive && entry.Type == "tree"
                && ListTreeLocked(repository, entry.Sha, path, true, maxEntries, lines))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Differs(FakeRepository repository, string? previousTree, string tree, string path)
    {
        var current = ReadFileLocked(repository, tree, path);
        var previous = previousTree is null ? null : ReadFileLocked(repository, previousTree, path);
        return !(current ?? Array.Empty<byte>()).AsSpan().SequenceEqual(previous ?? Array.Empty<byte>());
    }

    private static byte[]? ReadFileLocked(FakeRepository repository, string treeSha, string path)
    {
        var segments = path.Split('/');
        var current = treeSha;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!repository.Objects.TryGetValue(current, out var value) || value is not FakeGitTree tree)
            {
                return null;
            }

            var entry = tree.Entries.FirstOrDefault(candidate => candidate.Name == segments[i]);
            if (entry is null)
            {
                return null;
            }

            if (i == segments.Length - 1)
            {
                return repository.Objects.TryGetValue(entry.Sha, out var blob) && blob is FakeGitBlob file ? file.Content : null;
            }

            if (entry.Type != "tree")
            {
                return null;
            }

            current = entry.Sha;
        }

        return null;
    }

    private sealed class TreeBuilder
    {
        public SortedDictionary<string, BlobSha> Files { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, TreeBuilder> Folders { get; } = new(StringComparer.Ordinal);

        public TreeBuilder Folder(string name)
        {
            if (!Folders.TryGetValue(name, out var child))
            {
                child = new TreeBuilder();
                Folders.Add(name, child);
            }

            return child;
        }

        public void AddFile(string name, BlobSha sha)
        {
            if (Folders.ContainsKey(name))
            {
                throw new InvalidOperationException($"'{name}' is both a file and a folder.");
            }

            Files[name] = sha;
        }
    }
}
