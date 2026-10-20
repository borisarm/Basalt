using System.Globalization;
using System.IO.Compression;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Adapters.GitHub;

/// <summary>
/// Adapts <see cref="GitHubApiClient"/> to <see cref="IRemoteRepository"/> (spec 03 §1.4, 06 §9). Create-tree calls are
/// chunked; trees reported as truncated are walked subtree by subtree; zipball extraction never writes outside the
/// vault.
/// </summary>
internal sealed class GitHubRemoteRepository : IRemoteRepository
{
    private const int TreeChangeChunk = 1_000;

    private readonly GitHubApiClient _client;
    private readonly RemoteRepositoryRef _reference;

    public GitHubRemoteRepository(GitHubApiClient client, RemoteRepositoryRef reference)
    {
        _client = client;
        _reference = reference;
    }

    public async Task<Result<RemoteHead?>> GetHeadAsync(CancellationToken ct)
    {
        var reference = await _client.GetRefAsync(null, ct).ConfigureAwait(false);
        if (reference.IsFailure)
        {
            return reference.Error.Code is RemoteErrors.NotFound or RemoteErrors.RepositoryEmpty
                ? Result<RemoteHead?>.Success(null)
                : Result<RemoteHead?>.Failure(reference.Error);
        }

        var sha = CommitSha.Parse(reference.Value.Value!.Object.Sha).Value;
        var commit = await _client.GetCommitAsync(sha, ct).ConfigureAwait(false);
        return commit.IsSuccess
            ? new RemoteHead(sha, TreeSha.Parse(commit.Value.Tree.Sha).Value)
            : Result<RemoteHead?>.Failure(commit.Error);
    }

    public async Task<Result<Manifest>> GetTreeAsync(TreeSha tree, CancellationToken ct)
    {
        var root = await _client.GetTreeAsync(tree, recursive: true, ct).ConfigureAwait(false);
        if (root.IsFailure)
        {
            return Result<Manifest>.Failure(root.Error);
        }

        var entries = new List<GitHubTreeEntryDto>();
        if (root.Value.Truncated)
        {
            var walked = await WalkTreeAsync(tree, string.Empty, entries, ct).ConfigureAwait(false);
            if (walked.IsFailure)
            {
                return Result<Manifest>.Failure(walked.Error);
            }
        }
        else
        {
            entries.AddRange(root.Value.Tree);
        }

        var manifest = new List<ManifestEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Type != "blob")
            {
                continue;
            }

            var sha = BlobSha.Parse(entry.Sha);
            if (sha.IsFailure)
            {
                return Result<Manifest>.Failure(sha.Error);
            }

            var size = entry.Size;
            if (size is null)
            {
                var blob = await _client.GetBlobAsync(sha.Value, ct).ConfigureAwait(false);
                if (blob.IsFailure)
                {
                    return Result<Manifest>.Failure(blob.Error);
                }

                size = blob.Value.Length;
            }

            var path = VaultPath.Create(entry.Path);
            if (path.IsFailure)
            {
                return Result<Manifest>.Failure(path.Error);
            }

            manifest.Add(new ManifestEntry(path.Value, sha.Value, size.Value));
        }

        return Manifest.Create(manifest);
    }

    public async Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct) =>
        await _client.GetBlobAsync(sha, ct).ConfigureAwait(false);

    public async Task<Result<BlobSha>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var blob = await _client.CreateBlobAsync(content, ct).ConfigureAwait(false);
        return blob.IsSuccess ? BlobSha.Parse(blob.Value.Sha) : Result<BlobSha>.Failure(blob.Error);
    }

    public async Task<Result<TreeSha>> CreateTreeAsync(
        TreeSha? baseTree,
        IReadOnlyList<TreeChange> changes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var current = baseTree;
        for (var offset = 0; offset < changes.Count; offset += TreeChangeChunk)
        {
            var chunk = changes.Skip(offset).Take(TreeChangeChunk).ToArray();
            var tree = await _client.CreateTreeAsync(current, chunk, ct).ConfigureAwait(false);
            if (tree.IsFailure)
            {
                return Result<TreeSha>.Failure(tree.Error);
            }

            current = TreeSha.Parse(tree.Value.Sha).Value;
        }

        if (changes.Count == 0)
        {
            var empty = await _client.CreateTreeAsync(baseTree, [], ct).ConfigureAwait(false);
            return empty.IsSuccess
                ? TreeSha.Parse(empty.Value.Sha)
                : Result<TreeSha>.Failure(empty.Error);
        }

        return Result<TreeSha>.Success(current!.Value);
    }

    public async Task<Result<CommitSha>> CreateCommitAsync(
        string message,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        CommitAuthor author,
        CancellationToken ct)
    {
        var commit = await _client.CreateCommitAsync(message, tree, parents, author, ct).ConfigureAwait(false);
        return commit.IsSuccess
            ? CommitSha.Parse(commit.Value.Sha)
            : Result<CommitSha>.Failure(commit.Error);
    }

    public async Task<Result<Unit>> UpdateBranchAsync(CommitSha newHead, CommitSha? expectedOld, CancellationToken ct)
    {
        var head = await GetHeadAsync(ct).ConfigureAwait(false);
        if (head.IsFailure)
        {
            return Result<Unit>.Failure(head.Error);
        }

        if (expectedOld is { } expected && head.Value?.Commit != expected)
        {
            return Result<Unit>.Failure(NonFastForward());
        }

        if (head.Value is null)
        {
            var created = await _client.CreateRefAsync(_reference.Branch, newHead, ct).ConfigureAwait(false);
            return created.IsSuccess ? Result<Unit>.Success(Unit.Value) : Result<Unit>.Failure(created.Error);
        }

        var updated = await _client.UpdateRefAsync(_reference.Branch, newHead, force: false, ct).ConfigureAwait(false);
        return updated.IsSuccess ? Result<Unit>.Success(Unit.Value) : Result<Unit>.Failure(updated.Error);
    }

    public async Task<Result<IReadOnlyList<RemoteCommitInfo>>> GetFileHistoryAsync(
        VaultPath path,
        int max,
        CancellationToken ct)
    {
        var page = await _client.GetFileHistoryAsync(path, max, ct).ConfigureAwait(false);
        if (page.IsFailure)
        {
            return Result<IReadOnlyList<RemoteCommitInfo>>.Failure(page.Error);
        }

        var history = new List<RemoteCommitInfo>(page.Value.Items.Count);
        foreach (var commit in page.Value.Items.Take(max))
        {
            var when = DateTimeOffset.TryParse(
                commit.Commit.Author?.Date,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            history.Add(new RemoteCommitInfo(
                CommitSha.Parse(commit.Sha).Value,
                commit.Commit.Message,
                commit.Commit.Author?.Name ?? string.Empty,
                commit.Commit.Author?.Email ?? string.Empty,
                when));
        }

        return Result<IReadOnlyList<RemoteCommitInfo>>.Success(history);
    }

    public async Task<Result<byte[]?>> GetFileAtCommitAsync(
        VaultPath path,
        CommitSha commit,
        CancellationToken ct)
    {
        var existing = await _client.GetCommitAsync(commit, ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return existing.Error.Code is RemoteErrors.NotFound or RemoteErrors.RepositoryEmpty
                ? Result<byte[]?>.Failure(new Error(RemoteErrors.NotFound, $"Commit {commit} does not exist."))
                : Result<byte[]?>.Failure(existing.Error);
        }

        var content = await _client.GetFileAtAsync(path, commit.ToString(), ct).ConfigureAwait(false);
        if (content.IsFailure)
        {
            return content.Error.Code == RemoteErrors.NotFound
                ? Result<byte[]?>.Success(null)
                : Result<byte[]?>.Failure(content.Error);
        }

        return Result<byte[]?>.Success(content.Value);
    }

    /// <summary>Downloads and extracts a zipball in memory, rejecting paths that escape the vault (spec 06 §9.5).</summary>
    internal async Task<Result<IReadOnlyDictionary<VaultPath, byte[]>>> GetZipballFilesAsync(
        CommitSha commit,
        CancellationToken ct)
    {
        var archive = await _client.DownloadZipballAsync(commit, ct).ConfigureAwait(false);
        if (archive.IsFailure)
        {
            return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(archive.Error);
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive.Value), ZipArchiveMode.Read);
            var files = new Dictionary<VaultPath, byte[]>();
            string? root = null;
            foreach (var entry in zip.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.EndsWith('/'))
                {
                    continue;
                }

                var slash = normalized.IndexOf('/');
                if (slash < 0)
                {
                    return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(ZipSlip(normalized));
                }

                var prefix = normalized[..slash];
                root ??= prefix;
                if (prefix != root)
                {
                    return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(ZipSlip(normalized));
                }

                var relative = normalized[(slash + 1)..];
                var path = VaultPath.Create(relative);
                if (path.IsFailure)
                {
                    return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(ZipSlip(normalized));
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (!files.TryAdd(path.Value, buffer.ToArray()))
                {
                    return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(
                        new Error(RemoteErrors.ValidationFailed, $"Duplicate zip entry '{relative}'."));
                }
            }

            return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Success(files);
        }
        catch (InvalidDataException exception)
        {
            return Result<IReadOnlyDictionary<VaultPath, byte[]>>.Failure(
                new Error(RemoteErrors.ValidationFailed, exception.Message));
        }
    }

    private async Task<Result<Unit>> WalkTreeAsync(
        TreeSha tree,
        string prefix,
        List<GitHubTreeEntryDto> entries,
        CancellationToken ct)
    {
        var result = await _client.GetTreeAsync(tree, recursive: false, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result<Unit>.Failure(result.Error);
        }

        foreach (var entry in result.Value.Tree)
        {
            if (entry.Type == "tree")
            {
                var child = TreeSha.Parse(entry.Sha);
                if (child.IsFailure)
                {
                    return Result<Unit>.Failure(child.Error);
                }

                var walked = await WalkTreeAsync(child.Value, prefix + entry.Path + "/", entries, ct).ConfigureAwait(false);
                if (walked.IsFailure)
                {
                    return walked;
                }
            }
            else
            {
                entries.Add(entry with { Path = prefix + entry.Path });
            }
        }

        return Result<Unit>.Success(Unit.Value);
    }

    private static Error NonFastForward() => new(
        RemoteErrors.NonFastForward,
        "The branch already moved past the expected head.");

    private static Error ZipSlip(string entry) => new(
        RemoteErrors.ValidationFailed,
        $"Zip entry '{entry}' escapes the vault.");
}
