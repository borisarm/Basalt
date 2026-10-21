using System.Text;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>One publish attempt: what to publish, the remote state it was planned against, and the last synced base.</summary>
public sealed record PublishRequest(MaterializedPlan Plan, RemoteSnapshot Remote, Manifest Baseline);

/// <summary>Commit message, author and retry policy of a publish (spec 06 §6, step 7 and §8).</summary>
public sealed record PublishOptions(
    string Message,
    CommitAuthor Author,
    int MaxAttempts = 5,
    Func<int, TimeSpan>? RetryDelay = null);

/// <summary>Outcome of publishing; <see cref="Head"/> is the remote head after the cycle (null for an empty vault).</summary>
public sealed record PublishResult(RemoteHead? Head, bool Published, int Attempts);

/// <summary>Re-reads the remote and re-plans after a non-fast-forward (spec 06 §6, step 7).</summary>
public delegate Task<Result<PublishRequest>> PublishRefresh(CancellationToken ct);

/// <summary>
/// Uploads the target manifest (spec 06 §6, step 7): inline text or blobs, chunked create-tree, tree SHA verification,
/// commit and fast-forward ref update, retrying through a refresh callback when the branch moved. An empty repository is
/// bootstrapped with <c>.basalt/vault.json</c> through the contents API. A cycle creates at most one commit after that
/// bootstrap.
/// </summary>
public sealed class RemotePublisher
{
    private const int MaxEntriesPerRequest = 1_000;
    private const int MaxInlineBytesPerRequest = 8 * 1024 * 1024;

    private static readonly VaultPath VaultConfigPath = VaultPath.Create(".basalt/vault.json").Value;

    private readonly IVaultFileSystem _fileSystem;
    private readonly IRemoteRepository _repository;
    private readonly TimeProvider _time;

    public RemotePublisher(IVaultFileSystem fileSystem, IRemoteRepository repository, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(repository);
        _fileSystem = fileSystem;
        _repository = repository;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<PublishResult>> PublishAsync(
        PublishRequest request,
        PublishOptions options,
        PublishRefresh? refresh,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "At least one publish attempt is required.");
        }

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            var attemptResult = await PublishOnceAsync(request, options, ct).ConfigureAwait(false);
            if (attemptResult.IsSuccess)
            {
                return new PublishResult(attemptResult.Value.Head, attemptResult.Value.Published, attempt);
            }

            if (attempt == options.MaxAttempts || refresh is null || !IsRetryable(attemptResult.Error))
            {
                return Result<PublishResult>.Failure(attemptResult.Error);
            }

            var delay = options.RetryDelay?.Invoke(attempt) ?? TimeSpan.FromSeconds(1 << (attempt - 1));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }

            var refreshed = await refresh(ct).ConfigureAwait(false);
            if (refreshed.IsFailure)
            {
                return Result<PublishResult>.Failure(refreshed.Error);
            }

            request = refreshed.Value;
        }

        return new Error(RemoteErrors.NonFastForward, "The remote branch kept moving during the publish.");
    }

    private async Task<Result<(RemoteHead? Head, bool Published)>> PublishOnceAsync(
        PublishRequest request,
        PublishOptions options,
        CancellationToken ct)
    {
        var remote = request.Remote;
        var target = request.Plan.Target;
        if (target == remote.Manifest && (remote.Head is not null || target.IsEmpty))
        {
            return (remote.Head, false);
        }

        if (remote.Head is null)
        {
            var initialized = await InitializeAsync(request, options.Message, ct).ConfigureAwait(false);
            if (initialized.IsFailure)
            {
                return Result<(RemoteHead?, bool)>.Failure(initialized.Error);
            }

            remote = initialized.Value;
            if (target == remote.Manifest)
            {
                return (remote.Head, false);
            }
        }

        var changes = BuildChanges(remote.Manifest, target);
        if (changes.Count == 0)
        {
            return (remote.Head, false);
        }

        var existing = new HashSet<BlobSha>(remote.Manifest.Entries.Select(entry => entry.Sha));
        existing.UnionWith(request.Baseline.Entries.Select(entry => entry.Sha));
        var inline = new Dictionary<BlobSha, string>();
        foreach (var change in changes.OfType<TreeChange.Upsert>())
        {
            if (existing.Contains(change.Sha))
            {
                continue;
            }

            var content = await ContentAsync(change.Path, change.Sha, request.Plan, ct).ConfigureAwait(false);
            if (content.IsFailure)
            {
                return Result<(RemoteHead?, bool)>.Failure(content.Error);
            }

            var text = Encoding.UTF8.GetString(content.Value);
            if (TextBinaryClassifier.IsText(change.Path, content.Value)
                && Encoding.UTF8.GetByteCount(text) <= MaxInlineBytesPerRequest)
            {
                inline[change.Sha] = text;
                continue;
            }

            var blob = await _repository.CreateBlobAsync(content.Value, ct).ConfigureAwait(false);
            if (blob.IsFailure)
            {
                return Result<(RemoteHead?, bool)>.Failure(blob.Error);
            }

            if (blob.Value != change.Sha)
            {
                return ShaMismatch(change.Path, change.Sha, blob.Value);
            }
        }

        var pending = changes
            .Select(change => change is TreeChange.Upsert upsert
                ? (TreeChange)new TreeChange.Upsert(upsert.Path, upsert.Sha, inline.GetValueOrDefault(upsert.Sha))
                : change)
            .ToList();
        var current = remote.Head!.Tree;
        foreach (var chunk in Chunk(pending))
        {
            var created = await _repository.CreateTreeAsync(current, chunk, ct).ConfigureAwait(false);
            if (created.IsFailure)
            {
                return Result<(RemoteHead?, bool)>.Failure(created.Error);
            }

            current = created.Value;
        }

        var expected = GitTreeHasher.Compute(target.Entries.ToDictionary(entry => entry.Path, entry => entry.Sha));
        if (expected != current)
        {
            return new Error(SyncErrors.TreeMismatch, "The remote tree does not match the local target tree.")
                .With("expected", expected.ToString())
                .With("actual", current.ToString());
        }

        if (current == remote.Head.Tree)
        {
            return (remote.Head, false);
        }

        var commit = await _repository.CreateCommitAsync(
            options.Message,
            current,
            [remote.Head.Commit],
            options.Author,
            ct).ConfigureAwait(false);
        if (commit.IsFailure)
        {
            return Result<(RemoteHead?, bool)>.Failure(commit.Error);
        }

        var updated = await _repository.UpdateBranchAsync(commit.Value, remote.Head.Commit, ct).ConfigureAwait(false);
        if (updated.IsFailure)
        {
            return Result<(RemoteHead?, bool)>.Failure(updated.Error);
        }

        return (new RemoteHead(commit.Value, current), true);
    }

    private async Task<Result<RemoteSnapshot>> InitializeAsync(
        PublishRequest request,
        string message,
        CancellationToken ct)
    {
        var entry = request.Plan.Target.Get(VaultConfigPath);
        if (entry is null)
        {
            return new Error(SyncErrors.LocalFileMissing, "The vault configuration is missing from the target manifest.")
                .With("path", VaultConfigPath.ToString());
        }

        var content = await ContentAsync(VaultConfigPath, entry.Value.Sha, request.Plan, ct).ConfigureAwait(false);
        if (content.IsFailure)
        {
            return Result<RemoteSnapshot>.Failure(content.Error);
        }

        var initialized = await _repository.CreateInitialCommitAsync(VaultConfigPath, content.Value, message, ct)
            .ConfigureAwait(false);
        if (initialized.IsFailure)
        {
            return Result<RemoteSnapshot>.Failure(initialized.Error);
        }

        var head = await _repository.GetHeadAsync(null, ct).ConfigureAwait(false);
        if (head.IsFailure)
        {
            return Result<RemoteSnapshot>.Failure(head.Error);
        }

        if (head.Value.Head is not { } created)
        {
            return new Error(RemoteErrors.RepositoryEmpty, "The repository is still empty after its initial commit.");
        }

        var manifest = Manifest.Create([
            new ManifestEntry(VaultConfigPath, entry.Value.Sha, content.Value.LongLength),
        ]).Value;
        return new RemoteSnapshot(created, manifest, head.Value.ETag, [], NotModified: false);
    }

    private async Task<Result<byte[]>> ContentAsync(
        VaultPath path,
        BlobSha sha,
        MaterializedPlan plan,
        CancellationToken ct)
    {
        if (!plan.Contents.TryGetValue(sha, out var content))
        {
            var read = await _fileSystem.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (read.IsFailure)
            {
                return new Error(SyncErrors.LocalFileMissing, "The local file disappeared before it could be uploaded.")
                    .With("path", path.ToString())
                    .With("cause", read.Error.Code);
            }

            content = read.Value;
        }

        var actual = GitBlobHasher.Compute(content);
        return actual == sha ? content : ShaMismatch(path, sha, actual);
    }

    private static Error ShaMismatch(VaultPath path, BlobSha expected, BlobSha actual) =>
        new Error(SyncErrors.ShaMismatch, "A blob does not match its SHA.")
            .With("path", path.ToString())
            .With("expected", expected.ToString())
            .With("actual", actual.ToString());

    private static List<TreeChange> BuildChanges(Manifest remote, Manifest target)
    {
        var changes = new List<TreeChange>();
        foreach (var entry in target.Entries)
        {
            var current = remote.Get(entry.Path);
            if (current is null || current.Value.Sha != entry.Sha)
            {
                changes.Add(new TreeChange.Upsert(entry.Path, entry.Sha));
            }
        }

        foreach (var entry in remote.Entries)
        {
            if (!target.Contains(entry.Path))
            {
                changes.Add(new TreeChange.Delete(entry.Path));
            }
        }

        return changes;
    }

    private static List<List<TreeChange>> Chunk(IReadOnlyList<TreeChange> changes)
    {
        var chunks = new List<List<TreeChange>>();
        var current = new List<TreeChange>();
        long inlineBytes = 0;
        foreach (var change in changes)
        {
            var bytes = change is TreeChange.Upsert { Content: { } content }
                ? Encoding.UTF8.GetByteCount(content)
                : 0;
            if (current.Count > 0 && (current.Count == MaxEntriesPerRequest || inlineBytes + bytes > MaxInlineBytesPerRequest))
            {
                chunks.Add(current);
                current = [];
                inlineBytes = 0;
            }

            current.Add(change);
            inlineBytes += bytes;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private static bool IsRetryable(Error error) =>
        error.Code is RemoteErrors.NonFastForward or RemoteErrors.Conflict or RemoteErrors.RepositoryEmpty;
}
