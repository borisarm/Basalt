using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.Fakes;

/// <summary>
/// In-memory <see cref="IRemoteRepository"/> with real Git object SHAs (spec 03 §1.4, 06 §14). Used by application and
/// sync tests as the fast counterpart of <c>FakeGitHubServer</c>.
/// </summary>
public sealed class InMemoryRemoteRepository : IRemoteRepository
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<VaultPath, BlobSha>> _trees = [];
    private readonly Dictionary<string, InMemoryCommit> _commits = new(StringComparer.Ordinal);
    private string? _head;

    public InMemoryRemoteRepository(RemoteRepositoryRef reference, TimeProvider? clock = null)
    {
        Reference = reference;
        _clock = clock ?? TimeProvider.System;
    }

    public RemoteRepositoryRef Reference { get; }

    public Task<Result<RemoteHeadCheck>> GetHeadAsync(string? etag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_head is null || !_commits.TryGetValue(_head, out var commit))
            {
                return Task.FromResult(Result<RemoteHeadCheck>.Success(
                    new RemoteHeadCheck(null, null, NotModified: false)));
            }

            var currentETag = "\"" + commit.Sha + "\"";
            if (etag == currentETag)
            {
                return Task.FromResult(Result<RemoteHeadCheck>.Success(
                    new RemoteHeadCheck(null, currentETag, NotModified: true)));
            }

            return Task.FromResult(Result<RemoteHeadCheck>.Success(new RemoteHeadCheck(
                new RemoteHead(CommitSha.Parse(commit.Sha).Value, TreeSha.Parse(commit.Tree).Value),
                currentETag,
                NotModified: false)));
        }
    }

    public Task<Result<RemoteTreeSnapshot>> GetTreeAsync(TreeSha tree, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_trees.TryGetValue(tree.ToString(), out var files))
            {
                return Task.FromResult(Result<RemoteTreeSnapshot>.Failure(NotFound($"Tree {tree} does not exist.")));
            }

            var entries = files
                .Select(pair => new RemoteTreeFile(pair.Key, pair.Value, _blobs[pair.Value.ToString()].Length))
                .OrderBy(file => file.Path)
                .ToList();
            return Task.FromResult(Result<RemoteTreeSnapshot>.Success(
                new RemoteTreeSnapshot(entries, [])));
        }
    }

    public Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_blobs.TryGetValue(sha.ToString(), out var content)
                ? Result<byte[]>.Success(content)
                : Result<byte[]>.Failure(NotFound($"Blob {sha} does not exist.")));
        }
    }

    public Task<Result<IReadOnlyDictionary<VaultPath, byte[]>>> GetArchiveAsync(CommitSha commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_commits.TryGetValue(commit.ToString(), out var found))
            {
                return Task.FromResult(Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(
                    NotFound($"Commit {commit} does not exist.")));
            }

            if (!_trees.TryGetValue(found.Tree, out var files))
            {
                return Task.FromResult(Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(
                    NotFound($"Tree {found.Tree} does not exist.")));
            }

            var archive = files.ToDictionary(pair => pair.Key, pair => _blobs[pair.Value.ToString()]);
            return Task.FromResult(Result<IReadOnlyDictionary<VaultPath, byte[]>>.Success(archive));
        }
    }

    public Task<Result<BlobSha>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sha = GitBlobHasher.Compute(content.Span);
        lock (_gate)
        {
            _blobs.TryAdd(sha.ToString(), content.ToArray());
        }

        return Task.FromResult(Result<BlobSha>.Success(sha));
    }

    public Task<Result<TreeSha>> CreateTreeAsync(
        TreeSha? baseTree,
        IReadOnlyList<TreeChange> changes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var files = new Dictionary<VaultPath, BlobSha>();
            if (baseTree is { } baseSha)
            {
                if (!_trees.TryGetValue(baseSha.ToString(), out var baseFiles))
                {
                    return Task.FromResult(Result<TreeSha>.Failure(NotFound($"Tree {baseSha} does not exist.")));
                }

                foreach (var (path, sha) in baseFiles)
                {
                    files[path] = sha;
                }
            }

            foreach (var change in changes)
            {
                switch (change)
                {
                    case TreeChange.Upsert upsert:
                        if (!_blobs.ContainsKey(upsert.Sha.ToString()))
                        {
                            return Task.FromResult(Result<TreeSha>.Failure(
                                NotFound($"Blob {upsert.Sha} does not exist.")));
                        }

                        files[upsert.Path] = upsert.Sha;
                        break;
                    case TreeChange.Delete delete:
                        files.Remove(delete.Path);
                        break;
                    default:
                        return Task.FromResult(Result<TreeSha>.Failure(
                            new Error(RemoteErrors.ValidationFailed, "Unknown tree change.")));
                }
            }

            TreeSha tree;
            try
            {
                tree = GitTreeHasher.Compute(files);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(Result<TreeSha>.Failure(
                    new Error(RemoteErrors.ValidationFailed, exception.Message)));
            }

            _trees[tree.ToString()] = files;
            return Task.FromResult(Result<TreeSha>.Success(tree));
        }
    }

    public Task<Result<CommitSha>> CreateCommitAsync(
        string message,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        CommitAuthor author,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(author);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_trees.ContainsKey(tree.ToString()))
            {
                return Task.FromResult(Result<CommitSha>.Failure(NotFound($"Tree {tree} does not exist.")));
            }

            foreach (var parent in parents)
            {
                if (!_commits.ContainsKey(parent.ToString()))
                {
                    return Task.FromResult(Result<CommitSha>.Failure(NotFound($"Commit {parent} does not exist.")));
                }
            }

            var when = _clock.GetUtcNow();
            GitSignature signature;
            try
            {
                signature = new GitSignature(author.Name, author.Email, when);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(Result<CommitSha>.Failure(
                    new Error(RemoteErrors.ValidationFailed, exception.Message)));
            }

            CommitSha commit;
            try
            {
                commit = GitCommitHasher.Compute(tree, parents, signature, signature, message);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(Result<CommitSha>.Failure(
                    new Error(RemoteErrors.ValidationFailed, exception.Message)));
            }

            _commits[commit.ToString()] = new InMemoryCommit(
                commit.ToString(), tree.ToString(), parents.Select(parent => parent.ToString()).ToArray(),
                message, author.Name, author.Email, when);
            return Task.FromResult(Result<CommitSha>.Success(commit));
        }
    }

    public Task<Result<Unit>> UpdateBranchAsync(CommitSha newHead, CommitSha? expectedOld, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_commits.TryGetValue(newHead.ToString(), out var next))
            {
                return Task.FromResult(Result<Unit>.Failure(NotFound($"Commit {newHead} does not exist.")));
            }

            if (_head is null)
            {
                if (expectedOld is not null)
                {
                    return Task.FromResult(Result<Unit>.Failure(NonFastForward()));
                }

                _head = next.Sha;
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            }

            if (expectedOld is { } expected && expected.ToString() != _head)
            {
                return Task.FromResult(Result<Unit>.Failure(NonFastForward()));
            }

            if (!IsAncestor(_head, next.Sha))
            {
                return Task.FromResult(Result<Unit>.Failure(NonFastForward()));
            }

            _head = next.Sha;
            return Task.FromResult(Result<Unit>.Success(Unit.Value));
        }
    }

    public Task<Result<IReadOnlyList<RemoteCommitInfo>>> GetFileHistoryAsync(
        VaultPath path,
        int max,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var history = new List<RemoteCommitInfo>();
            if (max <= 0)
            {
                return Task.FromResult(Result<IReadOnlyList<RemoteCommitInfo>>.Success(history));
            }

            var current = _head;
            while (current is not null && history.Count < max && _commits.TryGetValue(current, out var commit))
            {
                var parentTree = commit.Parents.Count > 0 && _commits.TryGetValue(commit.Parents[0], out var parent)
                    ? parent.Tree
                    : null;
                if (Differs(parentTree, commit.Tree, path))
                {
                    history.Add(new RemoteCommitInfo(
                        CommitSha.Parse(commit.Sha).Value,
                        commit.Message,
                        commit.AuthorName,
                        commit.AuthorEmail,
                        commit.When));
                }

                current = commit.Parents.Count > 0 ? commit.Parents[0] : null;
            }

            return Task.FromResult(Result<IReadOnlyList<RemoteCommitInfo>>.Success(history));
        }
    }

    public Task<Result<byte[]?>> GetFileAtCommitAsync(VaultPath path, CommitSha commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_commits.TryGetValue(commit.ToString(), out var found))
            {
                return Task.FromResult(Result<byte[]?>.Failure(NotFound($"Commit {commit} does not exist.")));
            }

            if (!_trees.TryGetValue(found.Tree, out var files) || !files.TryGetValue(path, out var blob))
            {
                return Task.FromResult(Result<byte[]?>.Success(null));
            }

            return Task.FromResult(Result<byte[]?>.Success(_blobs[blob.ToString()]));
        }
    }

    private bool IsAncestor(string ancestor, string descendant)
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

            if (seen.Add(current) && _commits.TryGetValue(current, out var commit))
            {
                foreach (var parent in commit.Parents)
                {
                    pending.Push(parent);
                }
            }
        }

        return false;
    }

    private bool Differs(string? previousTree, string tree, VaultPath path)
    {
        var current = BlobAt(tree, path);
        var previous = previousTree is null ? null : BlobAt(previousTree, path);
        return current != previous;
    }

    private string? BlobAt(string tree, VaultPath path) =>
        _trees.TryGetValue(tree, out var files) && files.TryGetValue(path, out var sha) ? sha.ToString() : null;

    private static Error NotFound(string message) => new(RemoteErrors.NotFound, message);

    private static Error NonFastForward() => new(
        RemoteErrors.NonFastForward,
        "The new head is not a fast-forward of the current branch.");

    private sealed record InMemoryCommit(
        string Sha,
        string Tree,
        IReadOnlyList<string> Parents,
        string Message,
        string AuthorName,
        string AuthorEmail,
        DateTimeOffset When);
}
