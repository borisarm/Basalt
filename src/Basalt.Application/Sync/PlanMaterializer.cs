using System.Text;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Text;

namespace Basalt.Application.Sync;

/// <summary>
/// Downloads what the plan needs, verifies every blob SHA, runs the three-way merges and names conflict copies
/// (spec 06 §6, step 5). Text/binary is re-checked with the bytes (ADR-0017). Bulk downloads go through the zipball
/// when more than <see cref="ZipballThreshold"/> remote blobs are needed (spec 06 §9.5).
/// </summary>
public sealed class PlanMaterializer
{
    private const int ZipballThreshold = 200;

    private readonly IVaultFileSystem _fileSystem;
    private readonly IRemoteRepository _repository;

    public PlanMaterializer(IVaultFileSystem fileSystem, IRemoteRepository repository)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(repository);
        _fileSystem = fileSystem;
        _repository = repository;
    }

    public async Task<Result<MaterializedPlan>> MaterializeAsync(
        SyncPlan plan,
        Manifest local,
        RemoteSnapshot remote,
        SyncPolicy policy,
        DateTimeOffset now,
        string deviceName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(deviceName);

        var needed = NeededShas(plan);
        var archive = needed.Count > ZipballThreshold && remote.Head is not null
            ? await LoadArchiveAsync(needed, remote.Head.Commit, ct).ConfigureAwait(false)
            : Result<IReadOnlyDictionary<BlobSha, byte[]>>.Success(new Dictionary<BlobSha, byte[]>());
        if (archive.IsFailure)
        {
            return Result<MaterializedPlan>.Failure(archive.Error);
        }

        var target = plan.ExpectedNewBase.Entries.ToDictionary(entry => entry.Path, entry => entry);
        var downloaded = plan.Actions.OfType<SyncAction.Download>().Select(action => action.Path).ToHashSet();
        var run = new Run(
            _fileSystem,
            _repository,
            archive.Value,
            policy,
            local,
            target,
            downloaded,
            now,
            deviceName);
        foreach (var action in plan.Actions)
        {
            var applied = await run.ApplyAsync(action, ct).ConfigureAwait(false);
            if (applied.IsFailure)
            {
                return Result<MaterializedPlan>.Failure(applied.Error);
            }
        }

        var manifest = Manifest.Create(target.Values);
        return manifest.IsSuccess
            ? new MaterializedPlan(manifest.Value, run.Contents, run.Conflicts)
            : Result<MaterializedPlan>.Failure(manifest.Error);
    }

    private static HashSet<BlobSha> NeededShas(SyncPlan plan)
    {
        var needed = new HashSet<BlobSha>();
        foreach (var action in plan.Actions)
        {
            switch (action)
            {
                case SyncAction.Download download:
                    needed.Add(download.RemoteSha);
                    break;
                case SyncAction.MergeText merge:
                    needed.Add(merge.RemoteSha);
                    if (merge.BaseSha is { } baseSha)
                    {
                        needed.Add(baseSha);
                    }

                    break;
                case SyncAction.CreateConflictCopy { Side: ConflictSide.Remote } copy:
                    needed.Add(copy.Sha);
                    break;
            }
        }

        return needed;
    }

    private async Task<Result<IReadOnlyDictionary<BlobSha, byte[]>>> LoadArchiveAsync(
        HashSet<BlobSha> needed,
        CommitSha commit,
        CancellationToken ct)
    {
        var archive = await _repository.GetArchiveAsync(commit, ct).ConfigureAwait(false);
        if (archive.IsFailure)
        {
            return Result<IReadOnlyDictionary<BlobSha, byte[]>>.Failure(archive.Error);
        }

        var bySha = new Dictionary<BlobSha, byte[]>();
        foreach (var content in archive.Value.Values)
        {
            var sha = GitBlobHasher.Compute(content);
            if (needed.Contains(sha))
            {
                bySha[sha] = content;
            }
        }

        return bySha;
    }

    private sealed class Run(
        IVaultFileSystem fileSystem,
        IRemoteRepository repository,
        IReadOnlyDictionary<BlobSha, byte[]> archive,
        SyncPolicy policy,
        Manifest local,
        Dictionary<VaultPath, ManifestEntry> target,
        HashSet<VaultPath> downloaded,
        DateTimeOffset now,
        string deviceName)
    {
        private readonly Dictionary<BlobSha, byte[]> _contents = [];
        private readonly List<SyncConflict> _conflicts = [];
        private readonly HashSet<VaultPath> _reservedCopies = [];

        public IReadOnlyDictionary<BlobSha, byte[]> Contents => _contents;

        public IReadOnlyList<SyncConflict> Conflicts => _conflicts;

        public async Task<Result<Unit>> ApplyAsync(SyncAction action, CancellationToken ct)
        {
            switch (action)
            {
                case SyncAction.Download download:
                    return await ApplyDownloadAsync(download, ct).ConfigureAwait(false);
                case SyncAction.Upload upload:
                    return ApplyUpload(upload);
                case SyncAction.DeleteLocal deleteLocal:
                    target.Remove(deleteLocal.Path);
                    return Result.Success();
                case SyncAction.DeleteRemote deleteRemote:
                    target.Remove(deleteRemote.Path);
                    return Result.Success();
                case SyncAction.NoOp:
                    return Result.Success();
                case SyncAction.MergeText merge:
                    return await ApplyMergeAsync(merge, ct).ConfigureAwait(false);
                case SyncAction.CreateConflictCopy copy:
                    return await ApplyCopyAsync(copy, ct).ConfigureAwait(false);
                default:
                    return new Error(RemoteErrors.ValidationFailed, "Unknown sync action.");
            }
        }

        private async Task<Result<Unit>> ApplyDownloadAsync(SyncAction.Download download, CancellationToken ct)
        {
            var content = await FetchAsync(download.RemoteSha, ct).ConfigureAwait(false);
            if (content.IsFailure)
            {
                return Result.Failure(content.Error);
            }

            _contents[download.RemoteSha] = content.Value;
            target[download.Path] = new ManifestEntry(download.Path, download.RemoteSha, content.Value.LongLength);
            return Result.Success();
        }

        private Result<Unit> ApplyUpload(SyncAction.Upload upload)
        {
            if (local.Get(upload.Path) is { } entry)
            {
                target[upload.Path] = entry;
            }

            return Result.Success();
        }

        private async Task<Result<Unit>> ApplyMergeAsync(SyncAction.MergeText merge, CancellationToken ct)
        {
            var localContent = await ReadLocalAsync(merge.Path, ct).ConfigureAwait(false);
            if (localContent.IsFailure)
            {
                return Result.Failure(localContent.Error);
            }

            var remoteContent = await FetchAsync(merge.RemoteSha, ct).ConfigureAwait(false);
            if (remoteContent.IsFailure)
            {
                return Result.Failure(remoteContent.Error);
            }

            var baseContent = await FetchBaseAsync(merge, localContent.Value, remoteContent.Value, ct).ConfigureAwait(false);
            if (baseContent.IsFailure)
            {
                return Result.Failure(baseContent.Error);
            }

            var isText = TextBinaryClassifier.IsText(merge.Path, localContent.Value)
                && TextBinaryClassifier.IsText(merge.Path, remoteContent.Value)
                && (merge.BaseSha is null || TextBinaryClassifier.IsText(merge.Path, baseContent.Value));
            if (!isText)
            {
                return ResolveBinary(merge.Path, localContent.Value, remoteContent.Value);
            }

            var merged = ThreeWayTextMerger.Merge(
                Encoding.UTF8.GetString(baseContent.Value),
                Encoding.UTF8.GetString(localContent.Value),
                Encoding.UTF8.GetString(remoteContent.Value),
                new MergeOptions(deviceName, "GitHub"));
            if (merged is MergeResult.Clean clean)
            {
                Add(path: merge.Path, content: Encoding.UTF8.GetBytes(clean.Text));
                return Result.Success();
            }

            var regions = ((MergeResult.Conflicted)merged).Regions;
            switch (policy.TextConflicts)
            {
                case TextConflictPolicy.AutoMergeOrMarkers:
                    Add(merge.Path, Encoding.UTF8.GetBytes(merged.Text));
                    _conflicts.Add(new SyncConflict(merge.Path, SyncConflictKind.Markers, Regions: regions));
                    return Result.Success();
                case TextConflictPolicy.PreferLocal:
                    Add(merge.Path, localContent.Value);
                    return Result.Success();
                case TextConflictPolicy.PreferRemote:
                    SetRemote(merge.Path, remoteContent.Value);
                    return Result.Success();
                default:
                    SetRemote(merge.Path, remoteContent.Value);
                    var copy = AddCopy(merge.Path, localContent.Value);
                    if (copy.IsFailure)
                    {
                        return Result.Failure(copy.Error);
                    }

                    _conflicts.Add(new SyncConflict(
                        merge.Path,
                        SyncConflictKind.ConflictCopy,
                        copy.Value,
                        regions));
                    return Result.Success();
            }
        }

        private Result<Unit> ResolveBinary(VaultPath path, byte[] localContent, byte[] remoteContent)
        {
            switch (policy.BinaryConflicts)
            {
                case BinaryConflictPolicy.PreferLocal:
                    Add(path, localContent);
                    return Result.Success();
                case BinaryConflictPolicy.PreferRemote:
                    SetRemote(path, remoteContent);
                    return Result.Success();
                default:
                    SetRemote(path, remoteContent);
                    var copy = AddCopy(path, localContent);
                    if (copy.IsFailure)
                    {
                        return Result.Failure(copy.Error);
                    }

                    _conflicts.Add(new SyncConflict(path, SyncConflictKind.ConflictCopy, copy.Value));
                    return Result.Success();
            }
        }

        private async Task<Result<Unit>> ApplyCopyAsync(SyncAction.CreateConflictCopy copy, CancellationToken ct)
        {
            byte[] content;
            if (copy.Side == ConflictSide.Local)
            {
                var read = await ReadLocalAsync(copy.Path, ct).ConfigureAwait(false);
                if (read.IsFailure)
                {
                    return Result.Failure(read.Error);
                }

                content = read.Value;
            }
            else
            {
                var fetched = await FetchAsync(copy.Sha, ct).ConfigureAwait(false);
                if (fetched.IsFailure)
                {
                    return Result.Failure(fetched.Error);
                }

                content = fetched.Value;
            }

            var copyPath = AddCopy(copy.Path, content);
            if (copyPath.IsFailure)
            {
                return Result.Failure(copyPath.Error);
            }

            _conflicts.Add(new SyncConflict(
                copy.Path,
                downloaded.Contains(copy.Path) ? SyncConflictKind.ConflictCopy : SyncConflictKind.FileFolder,
                copyPath.Value));
            return Result.Success();
        }

        private async Task<Result<byte[]>> FetchBaseAsync(
            SyncAction.MergeText merge,
            byte[] localContent,
            byte[] remoteContent,
            CancellationToken ct)
        {
            if (merge.BaseSha is not { } baseSha)
            {
                return Array.Empty<byte>();
            }

            if (baseSha == merge.LocalSha)
            {
                return localContent;
            }

            if (baseSha == merge.RemoteSha)
            {
                return remoteContent;
            }

            var fetched = await FetchAsync(baseSha, ct).ConfigureAwait(false);
            return fetched.IsFailure && fetched.Error.Code == RemoteErrors.NotFound
                ? Array.Empty<byte>()
                : fetched;
        }

        private async Task<Result<byte[]>> FetchAsync(BlobSha sha, CancellationToken ct)
        {
            if (archive.TryGetValue(sha, out var archived))
            {
                return archived;
            }

            var fetched = await repository.GetBlobAsync(sha, ct).ConfigureAwait(false);
            if (fetched.IsFailure)
            {
                return fetched;
            }

            var actual = GitBlobHasher.Compute(fetched.Value);
            return actual == sha
                ? fetched.Value
                : new Error(SyncErrors.ShaMismatch, "A downloaded blob does not match its SHA.")
                    .With("expected", sha.ToString())
                    .With("actual", actual.ToString());
        }

        private async Task<Result<byte[]>> ReadLocalAsync(VaultPath path, CancellationToken ct)
        {
            var read = await fileSystem.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return read.IsSuccess
                ? read.Value
                : new Error(SyncErrors.LocalFileMissing, "The local file disappeared during the sync.")
                    .With("path", path.ToString())
                    .With("cause", read.Error.Code);
        }

        private void Add(VaultPath path, byte[] content)
        {
            var sha = GitBlobHasher.Compute(content);
            _contents[sha] = content;
            target[path] = new ManifestEntry(path, sha, content.LongLength);
        }

        private void SetRemote(VaultPath path, byte[] content)
        {
            var sha = GitBlobHasher.Compute(content);
            target[path] = new ManifestEntry(path, sha, content.LongLength);
        }

        private Result<VaultPath> AddCopy(VaultPath path, byte[] content)
        {
            var existing = target.Keys
                .Concat(local.Entries.Select(entry => entry.Path))
                .Concat(_reservedCopies);
            var name = ConflictCopyNamer.Name(path, existing, now, deviceName);
            if (name.IsFailure)
            {
                return name;
            }

            _reservedCopies.Add(name.Value);
            var sha = GitBlobHasher.Compute(content);
            _contents[sha] = content;
            target[name.Value] = new ManifestEntry(name.Value, sha, content.LongLength);
            return name.Value;
        }
    }
}
